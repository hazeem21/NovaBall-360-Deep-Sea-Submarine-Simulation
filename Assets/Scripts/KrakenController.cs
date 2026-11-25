using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Procedural kraken / octopus movement. Put it on the model's root object.
///
/// Based on how big cephalopods move:
///  * Jet pulses. The arms flare open slowly (recovery, mantle fills), then snap
///    shut together (power stroke, mantle squeezes) and the body shoots off
///    mantle first, with the arms streaming behind. Then it glides and slows.
///  * Follow-through. Each arm bone lags the one before it, so the tips whip
///    in after the base on every stroke.
///  * Writhing. Every arm has its own slow travelling wave and its own timing,
///    so the arms never move in lock-step, and the tips curl.
///  * Hovering. At low speed the body stays upright and drifts, with gentle
///    pulses. As speed rises it tilts over until it travels mantle first.
/// </summary>
[DisallowMultipleComponent]
public class KrakenController : MonoBehaviour
{
    [Header("Controls")]
    [Tooltip("0 = hover and drift, 1 = fast jetting. Drives pulse rate, thrust and body tilt.")]
    [Range(0f, 1f)] public float speed = 0.3f;

    [Tooltip("-1 = turn left, 1 = turn right.")]
    [Range(-1f, 1f)] public float turn = 0f;

    [Tooltip("Read W/S (speed), A/D (turn) and Shift (burst) from the keyboard.")]
    public bool useKeyboard = false;

    [Header("Jet pulse")]
    [Tooltip("Seconds per pulse while hovering. Big cephalopods pulse slowly.")]
    [Range(0.5f, 8f)] public float hoverPulsePeriod = 3.2f;

    [Tooltip("Seconds per pulse at full speed.")]
    [Range(0.3f, 4f)] public float burstPulsePeriod = 1.3f;

    [Tooltip("Share of each pulse spent on the fast closing stroke.")]
    [Range(0.1f, 0.6f)] public float powerStrokeShare = 0.4f;

    [Tooltip("Arm angle below horizontal when fully open (degrees). Negative opens past flat, like an umbrella.")]
    [Range(-40f, 60f)] public float openAngle = 5f;

    [Tooltip("Arm angle below horizontal when fully closed (degrees).")]
    [Range(30f, 120f)] public float closedAngle = 80f;

    [Tooltip("How much the arms open while hovering compared with jetting (0 to 1).")]
    [Range(0f, 1f)] public float hoverPulseDepth = 0.55f;

    [Tooltip("Bone-to-bone lag along each arm, as a share of a pulse. Gives the whip at the tips.")]
    [Range(0f, 0.5f)] public float armLag = 0.2f;

    [Tooltip("Mantle squeeze on the power stroke (fraction of its size).")]
    [Range(0f, 0.3f)] public float mantleSqueeze = 0.1f;

    [Header("Writhing")]
    [Tooltip("Writhe strength at the arm tips, in degrees per bone.")]
    [Range(0f, 60f)] public float writheAmount = 42f;

    [Tooltip("Writhe waves per second.")]
    [Range(0.05f, 2f)] public float writheSpeed = 0.28f;

    [Tooltip("Extra upward curl at the arm tips, in degrees per bone.")]
    [Range(0f, 60f)] public float tipCurl = 45f;

    [Tooltip("Waves along each arm at once. Higher = more S-shaped, snakier arms.")]
    [Range(0.25f, 2f)] public float wavesPerArm = 0.9f;

    [Tooltip("How springy the arms are. Lower = looser, more lag and overshoot (boneless feel).")]
    [Range(1f, 60f)] public float armStiffness = 10f;

    [Tooltip("Spring damping. Lower = more wobble after each stroke.")]
    [Range(0.1f, 1.5f)] public float armDamping = 0.45f;

    [Header("Movement")]
    public bool driveMovement = true;

    [Tooltip("Roam on its own around where it started.")]
    public bool autoWander = true;

    public float wanderRadius = 25f;
    public float wanderDepthRange = 4f;

    [Tooltip("Speed kick from one power stroke at full speed, in metres per second.")]
    public float thrust = 4f;

    [Tooltip("Water drag. Higher means it slows down faster between pulses.")]
    [Range(0.1f, 5f)] public float drag = 1.1f;

    [Tooltip("Turn rate at full turn input, in degrees per second.")]
    public float turnRate = 25f;

    [Tooltip("How fast it sinks between pulses (metres per second squared).")]
    public float sinkRate = 0.3f;

    [Tooltip("Body tilt at full speed (degrees). 90 = mantle points straight along the travel direction.")]
    [Range(0f, 90f)] public float maxTilt = 75f;

    [Header("Bones (found automatically if empty)")]
    public Transform hubBone;
    public Transform mantleBone;
    [Tooltip("Filled by Auto-Find. Each arm is listed by its base bone.")]
    public List<Transform> armRoots = new List<Transform>();

    // ------------------------------------------------------------------ state
    class ArmBone
    {
        public Transform t;
        public Quaternion restLocal;
        public Vector3 sideAxis;   // fold axis in parent rest space (+angle folds the arm down)
        public Vector3 upAxis;     // sideways writhe axis in parent rest space
        public float v;            // 0 at base, 1 at tip
        public float fold, foldVel, sway, swayVel;  // spring state (degrees)
    }
    class Arm { public List<ArmBone> bones = new List<ArmBone>(); public float seed; }

    readonly List<Arm> _arms = new List<Arm>();
    Vector3 _mantleRestScale;
    Quaternion _mantleRestRot;
    Vector3 _mantleSideAxis;
    float _pulse;         // 0..1
    float _prevPulse;
    float _speedS, _speedVel, _turnS, _turnVel;
    float _heading, _pitch;
    Vector3 _velocity;
    Vector3 _home, _target;
    float _nextPick, _burstUntil;
    bool _ready;

    void Awake()
    {
        if (hubBone == null || armRoots.Count == 0) AutoFindBones();
        Initialise();
    }

    void OnDisable()
    {
        foreach (var a in _arms) foreach (var b in a.bones) if (b.t) b.t.localRotation = b.restLocal;
        if (mantleBone) { mantleBone.localScale = _mantleRestScale; mantleBone.localRotation = _mantleRestRot; }
    }

    void Initialise()
    {
        _arms.Clear();
        _ready = false;
        if (hubBone == null || armRoots.Count == 0)
        {
            Debug.LogWarning($"{name}: KrakenController found no arms. Assign Hub Bone and Arm Roots.", this);
            return;
        }

        Vector3 up = transform.up;
        var weighted = WeightedBones();
        foreach (var root in armRoots)
        {
            if (root == null) continue;
            var chain = new List<Transform>();
            for (var t = root; t != null; t = t.childCount > 0 ? t.GetChild(0) : null) chain.Add(t);
            // Drop end bones that move no vertices, so the bend goes where the mesh can show it.
            if (weighted != null)
                while (chain.Count > 2 && !weighted.Contains(chain[chain.Count - 1])) chain.RemoveAt(chain.Count - 1);
            Vector3 tip = chain[chain.Count - 1].position;
            Vector3 radial = Vector3.ProjectOnPlane(tip - hubBone.position, up).normalized;
            if (radial.sqrMagnitude < 1e-6f) continue;
            Vector3 side = Vector3.Cross(up, radial).normalized;

            var arm = new Arm { seed = Random.value * 100f };
            for (int j = 0; j < chain.Count; j++)
            {
                var t = chain[j];
                Quaternion pr = t.parent ? t.parent.rotation : Quaternion.identity;
                arm.bones.Add(new ArmBone
                {
                    t = t,
                    restLocal = t.localRotation,
                    sideAxis = Quaternion.Inverse(pr) * side,
                    upAxis = Quaternion.Inverse(pr) * up,
                    v = chain.Count > 1 ? (float)j / (chain.Count - 1) : 0f,
                });
            }
            _arms.Add(arm);
        }

        if (mantleBone)
        {
            _mantleRestScale = mantleBone.localScale;
            _mantleRestRot = mantleBone.localRotation;
            Quaternion pr = mantleBone.parent ? mantleBone.parent.rotation : Quaternion.identity;
            _mantleSideAxis = Quaternion.Inverse(pr) * transform.right;
        }

        _home = transform.position;
        _heading = transform.eulerAngles.y;
        _speedS = speed;
        PickTarget();
        _ready = _arms.Count > 0;
    }

    HashSet<Transform> WeightedBones()
    {
        var smr = GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr == null || smr.sharedMesh == null) return null;
        var bones = smr.bones;
        var weights = smr.sharedMesh.GetAllBoneWeights();
        if (weights.Length == 0) return null;
        // A bone counts if it clearly drives a meaningful number of vertices.
        var counts = new int[bones.Length];
        foreach (var w in weights)
            if (w.weight > 0.2f && w.boneIndex < bones.Length) counts[w.boneIndex]++;
        int minVerts = Mathf.Max(10, smr.sharedMesh.vertexCount / 400);
        var used = new HashSet<Transform>();
        for (int i = 0; i < bones.Length; i++)
            if (bones[i] != null && counts[i] >= minVerts) used.Add(bones[i]);
        return used;
    }

    // ------------------------------------------------------------------ update
    void Update()
    {
        if (!_ready) return;
        if (useKeyboard) ReadKeyboard();
        else if (autoWander && driveMovement) Wander();

        float dt = Time.deltaTime;
        _speedS = Mathf.SmoothDamp(_speedS, speed, ref _speedVel, 0.8f);
        _turnS = Mathf.SmoothDamp(_turnS, turn, ref _turnVel, 0.6f);

        float period = Mathf.Lerp(hoverPulsePeriod, burstPulsePeriod, _speedS);
        _prevPulse = _pulse;
        _pulse = Mathf.Repeat(_pulse + dt / period, 1f);

        if (!driveMovement) return;

        // Orientation: heading turns with input; body tilts over as speed rises.
        _heading += _turnS * turnRate * dt;
        float targetPitch = maxTilt * Mathf.SmoothStep(0f, 1f, _speedS);
        _pitch = Mathf.MoveTowards(_pitch, targetPitch, 30f * dt);
        transform.rotation = Quaternion.Euler(0f, _heading, 0f) * Quaternion.Euler(_pitch, 0f, 0f);

        // Thrust during the power stroke, travelling mantle first (along body up).
        float ps = powerStrokeShare;
        if (_pulse < ps)
        {
            float k = Mathf.Sin(_pulse / ps * Mathf.PI);         // peaks mid-stroke
            float kick = thrust * Mathf.Lerp(0.25f, 1f, _speedS);
            // Tilted over, the jet drives it along its heading; upright, it pushes it up (a hover bob).
            float tilt = maxTilt > 0f ? Mathf.Clamp01(_pitch / maxTilt) : 0f;
            Vector3 dir = (Quaternion.Euler(0f, _heading, 0f) * Vector3.forward * tilt + Vector3.up * (1f - tilt) * (1f - tilt)).normalized;
            _velocity += dir * (kick * k * Mathf.PI / 2f / (ps * period) * dt);
        }
        // Slightly heavier than water, so it sinks a little between pulses when hovering.
        _velocity.y -= sinkRate * dt;
        // Depth keeping while wandering.
        if (autoWander && !useKeyboard)
            _velocity.y += Mathf.Clamp(_target.y - transform.position.y, -1f, 1f) * 0.8f * dt;

        _velocity *= Mathf.Exp(-drag * dt);
        transform.position += _velocity * dt;
    }

    void LateUpdate()
    {
        if (!_ready) return;
        float t = Time.time;
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        float depth = Mathf.Lerp(hoverPulseDepth, 1f, _speedS);
        float closedEff = Mathf.Lerp(openAngle, closedAngle, depth);
        float slow = 1f - _speedS;

        foreach (var arm in _arms)
        {
            int n = arm.bones.Count;
            int joints = Mathf.Max(1, n - 1);
            for (int j = 0; j < n; j++)
            {
                var b = arm.bones[j];

                // Pulse: the fold is spread evenly along the arm so it bends in a smooth arc,
                // not at a hinge. Bones further out see the pulse later, so a ripple runs to the tip.
                float o = Openness(Mathf.Repeat(_pulse - armLag * b.v, 1f));
                float share = j == 0 ? 0.3f : 0.7f / joints;
                float fold = Mathf.Lerp(closedEff, openAngle, o) * share;

                // Writhing: two slow waves travelling base to tip, different per arm, in both
                // directions, so each arm snakes and twists on its own. Strongest towards the tip.
                float w = Mathf.Lerp(0.25f, 1f, b.v);
                float k = wavesPerArm * Mathf.PI * 2f;
                float p1 = t * writheSpeed * Mathf.PI * 2f - b.v * k + arm.seed;
                float p2 = t * writheSpeed * 1.37f * Mathf.PI * 2f - b.v * k * 0.7f + arm.seed * 2.3f;
                float writhe = writheAmount * w * Mathf.Lerp(1f, 0.75f, _speedS);
                fold += writhe * (0.6f * Mathf.Sin(p1) + 0.4f * Mathf.Sin(p2 * 0.8f)) / joints * 3f;
                float sway = writhe * (0.6f * Mathf.Sin(p2) + 0.4f * Mathf.Sin(p1 * 0.9f + 1.3f)) / joints * 3f;

                // Tip curl that slowly coils and uncoils, more when hovering.
                float coil = 0.6f + 0.4f * Mathf.Sin(t * 0.21f + arm.seed * 3.1f);
                fold -= tipCurl * b.v * b.v * coil * Mathf.Lerp(0.4f, 1f, slow) / joints * 5f;

                // Springy follow-through: each bone chases its target, looser towards the tip,
                // so the arms lag, overshoot and settle like soft tissue.
                float stiff = armStiffness * Mathf.Lerp(1.6f, 0.5f, b.v);
                float c = 2f * armDamping * Mathf.Sqrt(stiff);
                b.foldVel += (stiff * (fold - b.fold) - c * b.foldVel) * dt;
                b.fold += b.foldVel * dt;
                b.swayVel += (stiff * (sway - b.sway) - c * b.swayVel) * dt;
                b.sway += b.swayVel * dt;

                b.t.localRotation =
                    Quaternion.AngleAxis(b.sway, b.upAxis) *
                    Quaternion.AngleAxis(b.fold, b.sideAxis) *
                    b.restLocal;
            }
        }

        if (mantleBone)
        {
            // Squeeze on the power stroke, swell on recovery, and a slow loose sway.
            float o = Openness(_pulse);
            float s = 1f + mantleSqueeze * (o - 0.5f) * Mathf.Lerp(0.5f, 1f, _speedS);
            mantleBone.localScale = _mantleRestScale * s;
            float sway = (6f * Mathf.Sin(t * 0.4f) + 3f * Mathf.Sin(t * 0.67f + 1f)) * Mathf.Lerp(1f, 0.3f, _speedS);
            mantleBone.localRotation = Quaternion.AngleAxis(sway, _mantleSideAxis) * _mantleRestRot;
        }
    }

    /// 1 = arms open, 0 = closed. Quick close, slow re-open, short hold.
    float Openness(float p)
    {
        float ps = powerStrokeShare;
        if (p < ps) return 0.5f + 0.5f * Mathf.Cos(p / ps * Mathf.PI);             // smooth close
        float r = Mathf.InverseLerp(ps, 1f, p);
        return 0.5f - 0.5f * Mathf.Cos(r * Mathf.PI);                               // slow, soft re-open
    }

    // ----------------------------------------------------------------- wander
    void PickTarget()
    {
        Vector2 c = Random.insideUnitCircle * wanderRadius;
        _target = _home + new Vector3(c.x, Random.Range(-wanderDepthRange, wanderDepthRange), c.y);
        _nextPick = Time.time + Random.Range(10f, 20f);
        if (Random.value < 0.3f) _burstUntil = Time.time + Random.Range(3f, 6f);
    }

    void Wander()
    {
        Vector3 to = _target - transform.position; to.y = 0f;
        if (to.magnitude < 3f || Time.time > _nextPick) { PickTarget(); return; }
        float desired = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
        float angle = Mathf.DeltaAngle(_heading, desired);
        turn = Mathf.Clamp(angle / 50f, -1f, 1f);
        // Hover while lining up, then jet.
        float aligned = 1f - Mathf.Clamp01(Mathf.Abs(angle) / 90f);
        speed = Time.time < _burstUntil ? 1f : Mathf.Lerp(0.1f, 0.55f, aligned);
    }

    void ReadKeyboard()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return;
        speed = kb.leftShiftKey.isPressed ? 1f : kb.wKey.isPressed ? 0.6f : kb.sKey.isPressed ? 0f : 0.2f;
        turn = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
#endif
    }

    // -------------------------------------------------------------- auto-find
    /// The hub is the bone with the most child bones. Its children that run
    /// sideways are arms; the one that runs most upwards is the mantle.
    [ContextMenu("Auto-Find Bones")]
    public void AutoFindBones()
    {
        var smr = GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr == null || smr.bones == null || smr.bones.Length == 0) { Debug.LogWarning($"{name}: no skinned mesh found.", this); return; }
        var bones = new HashSet<Transform>(smr.bones);

        Transform hub = null; int most = 0;
        foreach (var b in bones)
        {
            if (b == null) continue;
            int c = 0; foreach (Transform ch in b) if (bones.Contains(ch)) c++;
            if (c > most) { most = c; hub = b; }
        }
        if (hub == null || most < 3) { Debug.LogWarning($"{name}: could not find the arm hub.", this); return; }

        hubBone = hub;
        armRoots.Clear();
        mantleBone = null;
        float bestUp = -2f;
        Vector3 up = transform.up;
        foreach (Transform ch in hub)
        {
            if (!bones.Contains(ch)) continue;
            var tip = ch; int len = 1;
            while (tip.childCount > 0 && bones.Contains(tip.GetChild(0))) { tip = tip.GetChild(0); len++; }
            Vector3 d = (tip.position - hub.position).normalized;
            float u = Vector3.Dot(d, up);
            if (u > bestUp) { bestUp = u; mantleBone = ch; }
            if (Mathf.Abs(u) < 0.6f && len >= 3) armRoots.Add(ch);
        }
        armRoots.Remove(mantleBone);
        Debug.Log($"{name}: hub {hub.name}, mantle {(mantleBone ? mantleBone.name : "none")}, {armRoots.Count} arms.", this);
    }
}
