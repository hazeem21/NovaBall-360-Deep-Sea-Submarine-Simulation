using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural jellyfish (Luminous Drift). Put it on the model root.
///
/// From real jellyfish footage:
///  * The bell pulses: a quick contraction (the rim pulls in and the bell narrows)
///    followed by a slower, softer relaxation while it coasts.
///  * Each contraction pushes it a short way bell-first; between pulses it slows and sinks a little.
///  * Oral arms and tentacles are passive: they hang and trail, a ripple runs down them
///    after each pulse, and they stream behind when the jelly moves or drifts.
///  * It drifts with the water and slowly tilts and turns; it never steers sharply.
///  * Optional bioluminescent glow that brightens with each pulse.
/// </summary>
[DisallowMultipleComponent]
public class JellyfishController : MonoBehaviour
{
    [Header("Pulse")]
    [Tooltip("Seconds per pulse.")]
    public float pulsePeriod = 2.2f;
    [Range(0.1f, 0.6f)] public float contractShare = 0.3f;
    [Tooltip("How far the rim bones fold inward on contraction (degrees).")]
    [Range(0f, 45f)] public float rimContract = 16f;
    [Tooltip("Bell shrink on contraction (fraction).")]
    [Range(0f, 0.25f)] public float bellSqueeze = 0.08f;
    [Tooltip("Pause between bursts of pulses (seconds, random up to this).")]
    public float restMax = 3f;

    [Header("Tentacles")]
    [Tooltip("Sway in degrees per bone at the tips.")]
    [Range(0f, 20f)] public float sway = 4f;
    [Range(0.02f, 1f)] public float swaySpeed = 0.18f;
    [Tooltip("How strongly tentacles stream behind when moving.")]
    [Range(0f, 40f)] public float dragBend = 14f;
    [Tooltip("Spring stiffness. Lower = looser, more lag.")]
    [Range(1f, 40f)] public float stiffness = 6f;
    [Range(0.1f, 1.5f)] public float damping = 0.6f;

    [Header("Movement")]
    public bool driveMovement = true;
    [Tooltip("Speed kick per pulse (metres per second).")]
    public float thrust = 0.3f;
    [Range(0.1f, 5f)] public float drag = 1.2f;
    public float sinkRate = 0.05f;
    [Tooltip("Slow background current (metres per second).")]
    public Vector3 current = new Vector3(0.05f, 0f, 0.03f);
    public float wanderRadius = 12f;
    [Tooltip("Max tilt of the bell away from straight up (degrees).")]
    [Range(0f, 60f)] public float maxTilt = 25f;

    [Header("Glow")]
    public bool pulseGlow = true;
    [ColorUsage(false, true)] public Color glowColor = new Color(0.25f, 0.8f, 1f) * 1.2f;
    [Range(0f, 4f)] public float glowBoost = 1.5f;

    [Header("Bones (found automatically if empty)")]
    public Transform bellBone;
    public List<Transform> chainRoots = new List<Transform>();

    class B { public Transform t; public Quaternion rest; public Vector3 sideAxis, twistAxis; public float v; public float a, av, b, bv; }
    class Chain { public List<B> bones = new List<B>(); public float seed; public Vector3 radialLocal; }

    readonly List<Chain> _chains = new List<Chain>();
    Vector3 _bellUpLocal = Vector3.up;   // bell axis in root space at rest
    Vector3 _bellRestScale;
    readonly List<Vector3> _childRestScale = new List<Vector3>();
    float _phase; bool _pulsing = true; float _restUntil; int _pulsesLeft;
    Vector3 _velocity, _home, _target; float _nextPick; float _spin;
    Quaternion _baseRot;
    Renderer _renderer; MaterialPropertyBlock _mpb; string _emitProp;
    bool _ready;

    void Awake()
    {
        if (bellBone == null || chainRoots.Count == 0) AutoFindBones();
        Initialise();
    }

    void Initialise()
    {
        _ready = false;
        if (bellBone == null || chainRoots.Count == 0) { Debug.LogWarning($"{name}: JellyfishController found no bell/tentacles.", this); return; }

        // Bell axis: from the average tentacle tip to the bell.
        Vector3 avgTip = Vector3.zero; int n = 0;
        foreach (var r in chainRoots) { if (r) { avgTip += Tip(r).position; n++; } }
        avgTip /= Mathf.Max(1, n);
        Vector3 upWorld = (bellBone.position - avgTip).normalized;
        _bellUpLocal = transform.InverseTransformDirection(upWorld);

        _chains.Clear();
        foreach (var r in chainRoots)
        {
            if (r == null) continue;
            var c = new Chain { seed = Random.value * 100f };
            Vector3 radial = Vector3.ProjectOnPlane(r.position - bellBone.position, upWorld);
            if (radial.sqrMagnitude < 1e-6f) radial = Vector3.ProjectOnPlane(Tip(r).position - bellBone.position, upWorld);
            radial = radial.sqrMagnitude > 1e-8f ? radial.normalized : transform.right;
            c.radialLocal = transform.InverseTransformDirection(radial);
            Vector3 side = Vector3.Cross(upWorld, radial).normalized;
            var list = new List<Transform>();
            for (var t = r; t != null; t = t.childCount > 0 ? t.GetChild(0) : null) list.Add(t);
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                Quaternion pr = t.parent ? t.parent.rotation : Quaternion.identity;
                var iq = Quaternion.Inverse(pr);
                c.bones.Add(new B { t = t, rest = t.localRotation, sideAxis = iq * side, twistAxis = iq * radial, v = list.Count > 1 ? (float)i / (list.Count - 1) : 0f });
            }
            _chains.Add(c);
        }

        _bellRestScale = bellBone.localScale;
        _childRestScale.Clear();
        foreach (Transform ch in bellBone) _childRestScale.Add(ch.localScale);

        _baseRot = transform.rotation;
        _home = transform.position;
        PickTarget();

        _renderer = GetComponentInChildren<SkinnedMeshRenderer>();
        if (_renderer && _renderer.sharedMaterial)
        {
            var m = _renderer.sharedMaterial;
            if (m.HasProperty("emissiveFactor")) _emitProp = "emissiveFactor";
            else if (m.HasProperty("_EmissiveColor")) _emitProp = "_EmissiveColor";   // HDRP Lit
            else if (m.HasProperty("_EmissionColor")) _emitProp = "_EmissionColor";   // URP / Built-in
            if (_emitProp == "_EmissionColor") m.EnableKeyword("_EMISSION");
            _mpb = new MaterialPropertyBlock();
        }
        _pulsesLeft = Random.Range(3, 7);
        _ready = _chains.Count > 0;
    }

    static Transform Tip(Transform t) { while (t.childCount > 0) t = t.GetChild(0); return t; }

    /// 1 = relaxed (open), 0 = fully contracted.
    float Openness(float p)
    {
        if (p < contractShare) { float x = p / contractShare; return 0.5f + 0.5f * Mathf.Cos(x * Mathf.PI); }
        float r = Mathf.InverseLerp(contractShare, 1f, p);
        return 1f - Mathf.Pow(1f - r, 2.2f);   // slow ease-out back to open
    }

    void Update()
    {
        if (!_ready) return;
        float dt = Time.deltaTime;

        // Pulse in bursts with rests in between, like real jellies.
        if (_pulsing)
        {
            _phase += dt / pulsePeriod;
            if (_phase >= 1f)
            {
                _phase -= 1f;
                if (--_pulsesLeft <= 0) { _pulsing = false; _restUntil = Time.time + Random.Range(0.5f, restMax); _phase = 0f; }
            }
            // Thrust during contraction, bell first.
            if (_phase < contractShare && driveMovement)
            {
                float k = Mathf.Sin(_phase / contractShare * Mathf.PI);
                _velocity += BellUp() * (thrust * k * Mathf.PI / 2f / (contractShare * pulsePeriod) * dt);
            }
        }
        else if (Time.time > _restUntil) { _pulsing = true; _pulsesLeft = Random.Range(3, 8); }

        if (!driveMovement) return;

        // Slowly tilt the bell towards the wander target, within maxTilt, and spin a little.
        Vector3 to = _target - transform.position;
        if (to.magnitude < 1.5f || Time.time > _nextPick) PickTarget();
        Vector3 desiredUp = Vector3.Slerp(Vector3.up, to.normalized, Mathf.Clamp01(maxTilt / 90f));
        if (Vector3.Angle(Vector3.up, desiredUp) > maxTilt) desiredUp = Vector3.RotateTowards(Vector3.up, desiredUp, maxTilt * Mathf.Deg2Rad, 0f);
        Vector3 curUp = BellUp();
        Quaternion tilt = Quaternion.FromToRotation(curUp, Vector3.Slerp(curUp, desiredUp, 1f - Mathf.Exp(-0.15f * dt)));
        _spin = 3f * dt;
        transform.rotation = Quaternion.AngleAxis(_spin, curUp) * tilt * transform.rotation;

        _velocity *= Mathf.Exp(-drag * dt);
        _velocity.y -= sinkRate * dt;
        transform.position += (_velocity + current) * dt;
    }

    Vector3 BellUp() => transform.TransformDirection(_bellUpLocal).normalized;

    void LateUpdate()
    {
        if (!_ready) return;
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        float t = Time.time;
        float o = _pulsing ? Openness(_phase) : 1f;
        float contract = 1f - o;

        // Bell squeeze: shrink the bell bone, keep tentacles their own size.
        float s = 1f - bellSqueeze * contract;
        bellBone.localScale = _bellRestScale * s;
        int idx = 0;
        foreach (Transform ch in bellBone) { if (idx < _childRestScale.Count) ch.localScale = _childRestScale[idx] / s; idx++; }

        Vector3 up = BellUp();
        Vector3 moveDir = _velocity + current;
        // Water flowing past the jelly, in the bell's plane, makes tentacles stream opposite to motion.
        Vector3 lateral = Vector3.ProjectOnPlane(moveDir, up);
        float along = Vector3.Dot(moveDir, up);

        foreach (var c in _chains)
        {
            Vector3 radial = transform.TransformDirection(c.radialLocal);
            float trail = -Vector3.Dot(lateral, radial) * dragBend * 4f;   // bend away from the flow
            float swept = Mathf.Max(0f, along) * dragBend * 2f;             // moving up: tentacles pull inward/together
            int n = c.bones.Count;
            for (int j = 0; j < n; j++)
            {
                var b = c.bones[j];
                // Rim fold on contraction, rippling down the tentacle afterwards.
                float ripple = 1f - (_pulsing ? Openness(Mathf.Repeat(_phase - 0.35f * b.v, 1f)) : 1f);
                float fold = (j == 0 ? rimContract : rimContract * 0.25f) * (j == 0 ? contract : ripple * (1f - b.v));
                float w = Mathf.Lerp(0.3f, 1f, b.v);
                float ph = t * swaySpeed * Mathf.PI * 2f - b.v * 3f + c.seed;
                float targetA = fold + (swept + trail) / Mathf.Max(1, n) + sway * w * Mathf.Sin(ph);
                float targetB = sway * 0.8f * w * Mathf.Sin(ph * 0.77f + c.seed * 1.3f);

                float k = stiffness * Mathf.Lerp(1.5f, 0.5f, b.v), damp = 2f * damping * Mathf.Sqrt(k);
                b.av += (k * (targetA - b.a) - damp * b.av) * dt; b.a += b.av * dt;
                b.bv += (k * (targetB - b.b) - damp * b.bv) * dt; b.b += b.bv * dt;
                b.t.localRotation = Quaternion.AngleAxis(b.b, b.twistAxis) * Quaternion.AngleAxis(b.a, b.sideAxis) * b.rest;
            }
        }

        if (pulseGlow && _renderer && _emitProp != null)
        {
            _renderer.GetPropertyBlock(_mpb);
            float g = 0.35f + glowBoost * contract + 0.1f * Mathf.Sin(t * 0.7f);
            _mpb.SetColor(_emitProp, glowColor * g);
            _renderer.SetPropertyBlock(_mpb);
        }
    }

    void PickTarget()
    {
        Vector3 r = Random.insideUnitSphere * wanderRadius; r.y *= 0.3f;
        _target = _home + r;
        _nextPick = Time.time + Random.Range(20f, 40f);
    }

    [ContextMenu("Auto-Find Bones")]
    public void AutoFindBones()
    {
        var smr = GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr == null) return;
        var bones = new HashSet<Transform>(smr.bones);
        Transform hub = null; int most = 0;
        foreach (var b in bones)
        {
            if (b == null) continue;
            int c = 0; foreach (Transform ch in b) if (bones.Contains(ch)) c++;
            if (c > most) { most = c; hub = b; }
        }
        if (hub == null) return;
        bellBone = hub;
        chainRoots.Clear();
        foreach (Transform ch in hub) if (bones.Contains(ch)) chainRoots.Add(ch);
        Debug.Log($"{name}: bell {hub.name}, {chainRoots.Count} tentacle/arm chains.", this);
    }
}
