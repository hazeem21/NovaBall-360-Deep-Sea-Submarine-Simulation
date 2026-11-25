using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Procedural manta ray swimming ("underwater flight"). Put it on the model root.
///
/// From slow-motion manta footage:
///  * The pectoral wings flap up and down in a slow wave. The base leads and the tips
///    follow later, so the tips whip and curl at the top and bottom of each stroke.
///  * The wave also runs front to back across the wing: the leading edge moves first,
///    the trailing edge follows.
///  * The downstroke (power) is a little quicker than the upstroke.
///  * The body bobs gently against the wings and pitches slightly.
///  * Mantas often stop flapping and glide with the wings held level or slightly raised.
///  * They bank into turns like a plane, and the thin tail trails behind.
/// </summary>
[DisallowMultipleComponent]
public class MantaController : MonoBehaviour
{
    [Header("Controls")]
    [Range(0f, 1f)] public float speed = 0.3f;
    [Range(-1f, 1f)] public float turn = 0f;
    public bool useKeyboard = false;

    [Header("Wing beat")]
    [Tooltip("Seconds per wing beat when cruising. Large mantas beat roughly every 4 to 5 seconds.")]
    public float cruisePeriod = 4.5f;
    [Tooltip("Seconds per wing beat at full speed.")]
    public float burstPeriod = 2.2f;
    [Tooltip("Wingtip swing each way, in degrees, summed along the wing.")]
    [Range(0f, 80f)] public float wingAmplitude = 50f;
    [Tooltip("How far the tip lags the base, in cycles.")]
    [Range(0f, 0.4f)] public float tipLag = 0.18f;
    [Tooltip("How far the trailing wing chain lags the leading one, in cycles.")]
    [Range(0f, 0.4f)] public float chordLag = 0.12f;
    [Tooltip("Extra downward curl of the tips during the downstroke (degrees).")]
    [Range(0f, 30f)] public float tipCurl = 10f;
    [Tooltip("Downstroke speed-up. 0 = symmetric beat.")]
    [Range(0f, 0.5f)] public float strokeAsymmetry = 0.2f;

    [Header("Gliding")]
    [Tooltip("Chance per beat to glide for a while instead of flapping (only when cruising).")]
    [Range(0f, 1f)] public float glideChance = 0.25f;
    [Tooltip("Wing angle while gliding (degrees, + = raised).")]
    public float glideDihedral = 6f;

    [Header("Body")]
    [Tooltip("Body bob against the wing beat, as a fraction of span.")]
    [Range(0f, 0.05f)] public float heave = 0.012f;
    [Tooltip("Body pitch rocking (degrees).")]
    [Range(0f, 10f)] public float pitchRock = 2.5f;
    [Tooltip("Tail wave (degrees per bone).")]
    [Range(0f, 15f)] public float tailWave = 3f;

    [Header("Movement")]
    public bool driveMovement = true;
    public bool autoWander = true;
    public float wanderRadius = 30f;
    public float wanderDepthRange = 5f;
    [Tooltip("Cruise speed in wingspans per second.")]
    public float cruiseSpansPerSecond = 0.25f;
    public float burstSpansPerSecond = 0.8f;
    public float turnRate = 20f;
    [Tooltip("Bank angle at full turn (degrees).")]
    public float maxBank = 30f;

    [Header("Bones (found automatically if empty)")]
    public Transform hubBone;
    public List<Transform> leftWingRoots = new List<Transform>();
    public List<Transform> rightWingRoots = new List<Transform>();
    public Transform tailRoot;
    public List<Transform> extraRoots = new List<Transform>();

    class B { public Transform t; public Quaternion rest; public Vector3 fwdAxis, rightAxis, upAxis; public float v; public float springA, springV; }
    class Chain { public List<B> bones = new List<B>(); public float side; public float chord; public float seed; }

    readonly List<Chain> _wings = new List<Chain>();
    readonly List<B> _tail = new List<B>();
    readonly List<Chain> _extras = new List<Chain>();
    Vector3 _hubRestPos;
    float _phase, _speedS, _speedVel, _turnS, _turnVel, _glide, _glideUntil;
    float _heading, _bank, _pitch, _span = 5f;
    Vector3 _home, _target; float _nextPick, _burstUntil;
    int _lastBeat;
    bool _ready;

    void Awake()
    {
        if (hubBone == null || leftWingRoots.Count == 0) AutoFindBones();
        Initialise();
    }

    void Initialise()
    {
        _ready = false;
        if (hubBone == null) { Debug.LogWarning($"{name}: MantaController needs a hub bone.", this); return; }
        _hubRestPos = hubBone.localPosition;

        float maxX = 0f;
        foreach (var r in leftWingRoots) maxX = Mathf.Max(maxX, Mathf.Abs(transform.InverseTransformPoint(Tip(r).position).x));
        foreach (var r in rightWingRoots) maxX = Mathf.Max(maxX, Mathf.Abs(transform.InverseTransformPoint(Tip(r).position).x));
        _span = Mathf.Max(0.5f, maxX * 2f);

        // Leading edge = the wing chain whose base is furthest forward.
        float zMax = float.MinValue, zMin = float.MaxValue;
        foreach (var r in leftWingRoots) { float z = transform.InverseTransformPoint(r.position).z; zMax = Mathf.Max(zMax, z); zMin = Mathf.Min(zMin, z); }
        foreach (var r in rightWingRoots) { float z = transform.InverseTransformPoint(r.position).z; zMax = Mathf.Max(zMax, z); zMin = Mathf.Min(zMin, z); }

        _wings.Clear();
        foreach (var r in leftWingRoots) AddWing(r, -1f, zMax, zMin);
        foreach (var r in rightWingRoots) AddWing(r, 1f, zMax, zMin);
        _tail.Clear();
        if (tailRoot) _tail.AddRange(MakeChain(tailRoot).bones);
        _extras.Clear();
        foreach (var r in extraRoots) if (r) _extras.Add(MakeChain(r));

        _home = transform.position;
        _heading = transform.eulerAngles.y;
        _speedS = speed;
        PickTarget();
        _ready = _wings.Count > 0;
    }

    void AddWing(Transform root, float side, float zMax, float zMin)
    {
        if (root == null) return;
        var c = MakeChain(root);
        c.side = side;
        c.chord = zMax - zMin > 1e-3f ? Mathf.InverseLerp(zMax, zMin, transform.InverseTransformPoint(root.position).z) : 0f;
        _wings.Add(c);
    }

    Chain MakeChain(Transform root)
    {
        var c = new Chain { seed = Random.value * 10f };
        var list = new List<Transform>();
        for (var t = root; t != null; t = t.childCount > 0 ? t.GetChild(0) : null) list.Add(t);
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            Quaternion pr = t.parent ? t.parent.rotation : Quaternion.identity;
            var iq = Quaternion.Inverse(pr);
            c.bones.Add(new B
            {
                t = t, rest = t.localRotation,
                fwdAxis = iq * transform.forward, rightAxis = iq * transform.right, upAxis = iq * transform.up,
                v = list.Count > 1 ? (float)i / (list.Count - 1) : 0f,
            });
        }
        return c;
    }

    static Transform Tip(Transform t) { while (t.childCount > 0) t = t.GetChild(0); return t; }

    // ------------------------------------------------------------------ update
    void Update()
    {
        if (!_ready) return;
        if (useKeyboard) ReadKeyboard();
        else if (autoWander && driveMovement) Wander();

        float dt = Time.deltaTime;
        _speedS = Mathf.SmoothDamp(_speedS, speed, ref _speedVel, 1.2f);
        _turnS = Mathf.SmoothDamp(_turnS, turn, ref _turnVel, 1.0f);

        float period = Mathf.Lerp(cruisePeriod, burstPeriod, _speedS);
        _phase += dt / period;

        // Decide on gliding once per beat.
        int beat = Mathf.FloorToInt(_phase);
        if (beat != _lastBeat)
        {
            _lastBeat = beat;
            if (_speedS < 0.4f && Time.time > _glideUntil && Random.value < glideChance)
                _glideUntil = Time.time + Random.Range(1f, 2.5f) * period;
        }
        float glideTarget = Time.time < _glideUntil ? 1f : 0f;
        _glide = Mathf.MoveTowards(_glide, glideTarget, dt / (period * 0.5f));

        if (!driveMovement) return;
        _heading += _turnS * turnRate * dt;
        _bank = Mathf.Lerp(_bank, -_turnS * maxBank, 1f - Mathf.Exp(-1.5f * dt));
        float targetPitch = 0f;
        if (autoWander && !useKeyboard)
            targetPitch = -Mathf.Clamp((_target.y - transform.position.y) * 3f, -15f, 15f);
        _pitch = Mathf.Lerp(_pitch, targetPitch, 1f - Mathf.Exp(-0.8f * dt));
        transform.rotation = Quaternion.Euler(_pitch, _heading, _bank);

        float spans = Mathf.Lerp(cruiseSpansPerSecond, burstSpansPerSecond, _speedS) * Mathf.Lerp(1f, 0.7f, _glide);
        transform.position += transform.forward * (spans * _span * dt);
    }

    float Stroke(float cycles)
    {
        // Phase-warped sine: a quicker downstroke.
        float a = cycles * Mathf.PI * 2f;
        return Mathf.Sin(a + strokeAsymmetry * Mathf.Sin(a));
    }

    void LateUpdate()
    {
        if (!_ready) return;
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        float amp = wingAmplitude * Mathf.Lerp(1f, 1.25f, _speedS) * (1f - _glide);

        foreach (var w in _wings)
        {
            int n = w.bones.Count;
            float weightSum = 0f;
            for (int j = 0; j < n; j++) weightSum += Mathf.Lerp(1.8f, 0.8f, w.bones[j].v);
            for (int j = 0; j < n; j++)
            {
                var b = w.bones[j];
                float share = Mathf.Lerp(1.8f, 0.8f, b.v) / weightSum;   // most of the flap comes from the shoulder
                float s = Stroke(_phase - tipLag * b.v - chordLag * w.chord);
                float a = amp * share * s;
                // Tips curl down on the downstroke (s falling) and lift on the upstroke.
                float ds = Stroke(_phase - tipLag * b.v - chordLag * w.chord + 0.25f);
                a += -tipCurl * b.v * b.v * ds * (1f - _glide) / Mathf.Max(1, n - 1);
                a += _glide * glideDihedral * share;
                // Slow wobble so it doesn't look mechanical.
                a += 1.5f * Mathf.Sin(Time.time * 0.37f + w.seed + b.v) * b.v;

                // Positive raises the wing on both sides.
                b.t.localRotation = Quaternion.AngleAxis(a * w.side, b.fwdAxis) * b.rest;
            }
        }

        // Body bob and rock against the wings.
        float body = Stroke(_phase - 0.1f) * (1f - _glide);
        if (hubBone)
        {
            Vector3 upP = hubBone.parent ? hubBone.parent.InverseTransformDirection(transform.up) : transform.up;
            hubBone.localPosition = _hubRestPos - upP * (heave * _span * body / Mathf.Max(0.0001f, hubBone.parent ? hubBone.parent.lossyScale.y : 1f));
        }

        // Tail: trailing wave plus following the turn, springy.
        for (int j = 0; j < _tail.Count; j++)
        {
            var b = _tail[j];
            float target = tailWave * Mathf.Sin((_phase - 0.3f - b.v * 0.6f) * Mathf.PI * 2f) * (0.4f + b.v);
            float yaw = -_turnS * 8f * b.v;
            float k = 20f, c = 2f * 0.5f * Mathf.Sqrt(k);
            b.springV += (k * (yaw - b.springA) - c * b.springV) * dt;
            b.springA += b.springV * dt;
            b.t.localRotation = Quaternion.AngleAxis(b.springA, b.upAxis) * Quaternion.AngleAxis(target + pitchRock * body * (j == 0 ? 1f : 0f), b.rightAxis) * b.rest;
        }

        // Cephalic lobes and other small parts: gentle curl.
        foreach (var c in _extras)
            foreach (var b in c.bones)
            {
                float a = 6f * Mathf.Sin(Time.time * 0.5f + c.seed) * b.v + 3f * body;
                b.t.localRotation = Quaternion.AngleAxis(a, b.rightAxis) * b.rest;
            }
    }

    // ----------------------------------------------------------------- wander
    void PickTarget()
    {
        Vector2 c = Random.insideUnitCircle * wanderRadius;
        _target = _home + new Vector3(c.x, Random.Range(-wanderDepthRange, wanderDepthRange), c.y);
        _nextPick = Time.time + Random.Range(15f, 30f);
        if (Random.value < 0.15f) _burstUntil = Time.time + Random.Range(3f, 6f);
    }

    void Wander()
    {
        Vector3 to = _target - transform.position; to.y = 0f;
        if (to.magnitude < _span || Time.time > _nextPick) { PickTarget(); return; }
        float angle = Mathf.DeltaAngle(_heading, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
        turn = Mathf.Clamp(angle / 70f, -1f, 1f);
        speed = Time.time < _burstUntil ? 1f : 0.25f;
    }

    void ReadKeyboard()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current; if (kb == null) return;
        speed = kb.leftShiftKey.isPressed ? 1f : kb.wKey.isPressed ? 0.6f : kb.sKey.isPressed ? 0f : 0.25f;
        turn = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
#endif
    }

    // -------------------------------------------------------------- auto-find
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
        hubBone = hub;
        leftWingRoots.Clear(); rightWingRoots.Clear(); extraRoots.Clear(); tailRoot = null;

        float maxLat = 0f;
        foreach (Transform ch in hub) if (bones.Contains(ch)) maxLat = Mathf.Max(maxLat, Mathf.Abs(transform.InverseTransformPoint(Tip(ch).position).x));
        float minZ = float.MaxValue; int bestLen = 0;
        foreach (Transform ch in hub)
        {
            if (!bones.Contains(ch)) continue;
            Vector3 tip = transform.InverseTransformPoint(Tip(ch).position);
            int len = 0; for (var t = ch; t != null; t = t.childCount > 0 ? t.GetChild(0) : null) len++;
            if (Mathf.Abs(tip.x) > 0.5f * maxLat) { (tip.x < 0 ? leftWingRoots : rightWingRoots).Add(ch); continue; }
            if (len >= 4 && tip.z < minZ) { minZ = tip.z; tailRoot = ch; bestLen = len; }
        }
        foreach (Transform ch in hub)
            if (bones.Contains(ch) && ch != tailRoot && !leftWingRoots.Contains(ch) && !rightWingRoots.Contains(ch)) extraRoots.Add(ch);
        Debug.Log($"{name}: hub {hub.name}, wings L{leftWingRoots.Count}/R{rightWingRoots.Count}, tail {(tailRoot ? tailRoot.name : "none")}, extras {extraRoots.Count}", this);
    }
}
