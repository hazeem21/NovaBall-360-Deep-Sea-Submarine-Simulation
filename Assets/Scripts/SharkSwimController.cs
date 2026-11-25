using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Procedural swimming for a rigged shark. Put it on the model's root object
/// (the one whose forward axis points from tail to snout).
///
/// The body is driven as a travelling sine wave: the front of the body
/// (snout to pectorals) stays nearly rigid, the bend grows behind the dorsal
/// fin towards the tail, each bone lags the one in front of it, and the head
/// yaws slightly against the tail beat. Fins are left alone, so they ride along
/// stiffly with their parent bones. Turning bends the whole body into a C.
/// </summary>
[DisallowMultipleComponent]
public class SharkSwimController : MonoBehaviour
{
    // ------------------------------------------------------------------ inputs
    [Header("Controls")]
    [Tooltip("0 = slow cruise, 1 = full burst. Drives beat frequency, amplitude and forward speed.")]
    [Range(0f, 1f)] public float speed = 0.2f;

    [Tooltip("-1 = hard left, 0 = straight, 1 = hard right.")]
    [Range(-1f, 1f)] public float turn = 0f;

    [Tooltip("Read W/S (speed), A/D (turn) and Shift (burst) from the keyboard. Handy for testing.")]
    public bool useKeyboard = false;

    // ------------------------------------------------------------- tail beat
    [Header("Tail beat")]
    [Tooltip("Tail beats per second at cruise. A megalodon cruise beat is about 1.5 to 2 s per cycle (0.5 to 0.67 Hz).")]
    [Range(0.1f, 3f)] public float cruiseFrequency = 0.57f;

    [Tooltip("Tail beats per second at full burst.")]
    [Range(0.1f, 4f)] public float burstFrequency = 1.4f;

    [Tooltip("Approximate sideways swing of the tail tip, each side, as a fraction of body length (0.15 to 0.2 looks right).")]
    [Range(0f, 0.4f)] public float amplitude = 0.17f;

    [Tooltip("Amplitude multiplier at full burst.")]
    [Range(1f, 2f)] public float burstAmplitudeMultiplier = 1.35f;

    [Tooltip("Length of the body wave in body lengths. Smaller values mean more lag from bone to bone.")]
    [Range(0.4f, 3f)] public float waveLength = 1.1f;

    [Tooltip("How fast speed changes are blended in, in seconds.")]
    [Range(0.01f, 2f)] public float responseTime = 0.4f;

    // ------------------------------------------------------------ body shape
    public enum SwimPlane { Horizontal, Vertical }

    [Tooltip("Horizontal = side-to-side tail (sharks, fish). Vertical = up-and-down fluke stroke (whales, dolphins).")]
    public SwimPlane swimPlane = SwimPlane.Horizontal;

    [Header("Body shape")]
    [Tooltip("Fraction of body length, from the snout, that stays nearly rigid.")]
    [Range(0f, 0.8f)] public float rigidFront = 0.4f;

    [Tooltip("Bend falloff. Higher values push more of the bend towards the tail.")]
    [Range(0.5f, 4f)] public float bendExponent = 1.6f;

    [Tooltip("Head yaw against the tail beat, in degrees.")]
    [Range(0f, 15f)] public float headYaw = 3f;

    // ---------------------------------------------------------------- turning
    [Header("Turning")]
    [Tooltip("Body bend at full turn input, in degrees at each end of the body (C shape).")]
    [Range(0f, 60f)] public float maxTurnBend = 25f;

    [Tooltip("How much the rigid front bends in a turn compared with the tail (0 to 1).")]
    [Range(0f, 1f)] public float frontTurnFlex = 0.35f;

    // ------------------------------------------------------------ root motion
    [Header("Root motion (optional)")]
    [Tooltip("Move and rotate this object as the shark swims.")]
    public bool driveMovement = true;

    [Tooltip("Let the shark roam on its own: it picks new headings, turns towards them, " +
             "bursts now and then, and stays inside Wander Radius of where it started.")]
    public bool autoWander = true;

    [Tooltip("How far from its start point the shark may roam, in metres.")]
    public float wanderRadius = 20f;

    [Tooltip("Height band around the start point the shark may roam in, in metres (0 = stay level).")]
    public float wanderDepthRange = 3f;

    [Tooltip("Forward speed at cruise, in body lengths per second.")]
    public float cruiseBodyLengthsPerSecond = 0.35f;

    [Tooltip("Forward speed at full burst, in body lengths per second.")]
    public float burstBodyLengthsPerSecond = 1.5f;

    [Tooltip("Turn rate at full turn input, in degrees per second.")]
    public float turnRate = 45f;

    // ---------------------------------------------------------------- bones
    [Header("Bones")]
    [Tooltip("Spine bones from snout to tail. Leave empty to find them automatically from the rig " +
             "(or right-click the component header and choose Auto-Find Spine Bones).")]
    public List<Transform> spineBones = new List<Transform>();

    [Tooltip("Bone the bend is measured from. It is held still while the bones in front of and behind it bend. " +
             "Found automatically if empty.")]
    public Transform pivotBone;

    [Tooltip("Set when the model's snout points along -forward. Auto-find sets it for you.")]
    public bool headIsBackward = false;

    // ---------------------------------------------------------------- state
    class BoneState
    {
        public Transform bone;
        public Quaternion restLocal;
        public Vector3 axisInParent;  // body "up" in the parent's rest space
        public float u;               // 0 = snout, 1 = tail tip
        public int parentIndex;       // nearest ancestor in the chain, -1 = pivot
        public float angle;           // absolute yaw this frame (degrees)
        public Vector3 pitchAxisInParent; // body "right" in the parent's rest space
        public float pitch;           // absolute pitch this frame (degrees), used by vertical swimmers
    }

    readonly List<BoneState> _bones = new List<BoneState>();
    float _phase;
    float _speedSmoothed, _speedVel;
    Vector3 _home, _wanderTarget;
    float _nextWanderPick, _burstUntil;
    float _turnSmoothed, _turnVel;
    float _bodyLength = 1f;
    float _uPivot = 0.5f;
    float _tailAngleScale = 1f;   // radians of bend per unit "amplitude"
    bool _ready;

    // =====================================================================
    void Awake()
    {
        if (spineBones.Count < 2) AutoFindSpineBones();
        Initialise();
    }

    void OnDisable() { RestorePose(); }

    void Initialise()
    {
        _bones.Clear();
        _ready = false;
        spineBones.RemoveAll(b => b == null);
        if (spineBones.Count < 2)
        {
            Debug.LogWarning($"{name}: SharkSwimController found no spine bones. Assign them in the inspector.", this);
            return;
        }

        Vector3 fwd = BodyForward();
        Vector3 up = transform.up;

        // Body extent along forward, using every bone under the rig so the fin tips count.
        float headProj = float.MinValue, tailProj = float.MaxValue;
        foreach (var t in GetComponentsInChildren<Transform>())
        {
            if (t == transform) continue;
            float p = Vector3.Dot(t.position - transform.position, fwd);
            headProj = Mathf.Max(headProj, p);
            tailProj = Mathf.Min(tailProj, p);
        }
        foreach (var b in spineBones)
        {
            float p = Vector3.Dot(b.position - transform.position, fwd);
            headProj = Mathf.Max(headProj, p);
            tailProj = Mathf.Min(tailProj, p);
        }
        _bodyLength = Mathf.Max(0.001f, headProj - tailProj);

        var set = new HashSet<Transform>(spineBones);
        foreach (var b in spineBones)
        {
            var s = new BoneState
            {
                bone = b,
                restLocal = b.localRotation,
                u = Mathf.InverseLerp(headProj, tailProj, Vector3.Dot(b.position - transform.position, fwd)),
                parentIndex = -1,
            };
            Quaternion parentRot = b.parent ? b.parent.rotation : Quaternion.identity;
            s.axisInParent = (Quaternion.Inverse(parentRot) * up).normalized;
            s.pitchAxisInParent = (Quaternion.Inverse(parentRot) * transform.right).normalized;
            _bones.Add(s);
        }

        // Link every bone to its nearest ancestor in the chain.
        for (int i = 0; i < _bones.Count; i++)
        {
            for (var p = _bones[i].bone.parent; p != null && p != transform; p = p.parent)
            {
                if (!set.Contains(p)) continue;
                _bones[i].parentIndex = spineBones.IndexOf(p);
                break;
            }
        }

        if (pivotBone == null || !set.Contains(pivotBone))
        {
            // Default pivot: the highest bone of the chain in the hierarchy.
            foreach (var s in _bones) if (s.parentIndex < 0) { pivotBone = s.bone; break; }
        }
        _uPivot = _bones[spineBones.IndexOf(pivotBone)].u;

        // Work out how much bend gives the requested tail tip swing:
        // tip offset ~ L * integral of bend envelope from pivot to tail.
        float integral = 0f;
        const int steps = 64;
        for (int i = 0; i < steps; i++)
        {
            float u = Mathf.Lerp(_uPivot, 1f, (i + 0.5f) / steps);
            integral += Envelope(u) * (1f - _uPivot) / steps;
        }
        _tailAngleScale = integral > 1e-4f ? 1f / integral : 1f;

        _speedSmoothed = speed;
        _turnSmoothed = turn;
        _home = transform.position;
        PickWanderTarget();
        _ready = true;
    }

    Vector3 BodyForward() => headIsBackward ? -transform.forward : transform.forward;

    float Envelope(float u)
    {
        float t = Mathf.InverseLerp(rigidFront, 1f, u);
        t = t * t * (3f - 2f * t);           // smoothstep
        return Mathf.Pow(t, bendExponent);
    }

    // =====================================================================
    void Update()
    {
        if (useKeyboard) ReadKeyboard();
        else if (autoWander && driveMovement) Wander();

        _speedSmoothed = Mathf.SmoothDamp(_speedSmoothed, speed, ref _speedVel, responseTime);
        _turnSmoothed = Mathf.SmoothDamp(_turnSmoothed, turn, ref _turnVel, responseTime);

        float freq = Mathf.Lerp(cruiseFrequency, burstFrequency, _speedSmoothed);
        // Tight turns quicken the beat a little.
        freq *= 1f + 0.25f * Mathf.Abs(_turnSmoothed);
        _phase = Mathf.Repeat(_phase + freq * Time.deltaTime * Mathf.PI * 2f, Mathf.PI * 2f);

        if (driveMovement)
        {
            float bl = Mathf.Lerp(cruiseBodyLengthsPerSecond, burstBodyLengthsPerSecond, _speedSmoothed);
            transform.Rotate(0f, _turnSmoothed * turnRate * Time.deltaTime, 0f, Space.World);
            if (autoWander && !useKeyboard)
            {
                // Gentle pitch towards the target depth, never more than 20 degrees.
                Vector3 to = _wanderTarget - transform.position;
                float wantPitch = -Mathf.Clamp(Mathf.Atan2(to.y, new Vector2(to.x, to.z).magnitude) * Mathf.Rad2Deg, -20f, 20f);
                Vector3 e = transform.eulerAngles;
                float pitch = Mathf.MoveTowardsAngle(e.x, wantPitch, 10f * Time.deltaTime);
                transform.rotation = Quaternion.Euler(pitch, e.y, 0f);
            }
            transform.position += BodyForward() * (bl * _bodyLength * Time.deltaTime);
        }
    }

    void LateUpdate()
    {
        if (!_ready) return;

        float ampFrac = amplitude * Mathf.Lerp(1f, burstAmplitudeMultiplier, _speedSmoothed);
        float swimDeg = Mathf.Min(ampFrac * _tailAngleScale, 1.2f) * Mathf.Rad2Deg;
        float k = Mathf.PI * 2f / Mathf.Max(0.05f, waveLength);
        float tailPhase = _phase - k * 1f;
        float span = Mathf.Max(_uPivot, 1f - _uPivot);

        // 1. Absolute yaw of each segment relative to the body.
        //    Positive = clockwise seen from above. A rigid rotation that swings the
        //    tail right turns the head left, so the head recoil uses the tail phase.
        for (int i = 0; i < _bones.Count; i++)
        {
            var s = _bones[i];
            float frontMask = 1f - Mathf.InverseLerp(rigidFront - 0.1f, rigidFront + 0.1f, s.u);

            float wave = swimDeg * Envelope(s.u) * Mathf.Sin(_phase - k * s.u);
            wave += frontMask * headYaw * Mathf.Sin(tailPhase) * Mathf.Lerp(1f, burstAmplitudeMultiplier, _speedSmoothed);

            float flex = Mathf.Lerp(frontTurnFlex, 1f, 1f - frontMask);
            float bend = maxTurnBend * _turnSmoothed * flex * (_uPivot - s.u) / span;

            bool vertical = swimPlane == SwimPlane.Vertical;
            s.angle = s.bone == pivotBone ? 0f : (vertical ? bend : wave + bend);
            s.pitch = s.bone == pivotBone || !vertical ? 0f : wave;
        }

        // 2. Each bone rotates by the difference from its chain parent, so bends add up along the chain.
        for (int i = 0; i < _bones.Count; i++)
        {
            var s = _bones[i];
            if (s.bone == pivotBone) { s.bone.localRotation = s.restLocal; continue; }
            float parentAngle = s.parentIndex >= 0 ? _bones[s.parentIndex].angle : 0f;
            float parentPitch = s.parentIndex >= 0 ? _bones[s.parentIndex].pitch : 0f;
            s.bone.localRotation =
                Quaternion.AngleAxis(s.angle - parentAngle, s.axisInParent) *
                Quaternion.AngleAxis(s.pitch - parentPitch, s.pitchAxisInParent) *
                s.restLocal;
        }
    }

    void PickWanderTarget()
    {
        Vector2 c = Random.insideUnitCircle * wanderRadius;
        _wanderTarget = _home + new Vector3(c.x, Random.Range(-wanderDepthRange, wanderDepthRange), c.y);
        _nextWanderPick = Time.time + Random.Range(6f, 14f);
        if (Random.value < 0.25f) _burstUntil = Time.time + Random.Range(1.5f, 3f);
    }

    void Wander()
    {
        Vector3 to = _wanderTarget - transform.position;
        Vector3 flat = new Vector3(to.x, 0f, to.z);
        if (flat.magnitude < _bodyLength * 1.5f || Time.time > _nextWanderPick) { PickWanderTarget(); return; }

        Vector3 fwd = BodyForward(); fwd.y = 0f;
        float angle = Vector3.SignedAngle(fwd, flat, Vector3.up);   // + = target is to the right
        turn = Mathf.Clamp(angle / 60f, -1f, 1f);
        speed = Time.time < _burstUntil ? 1f : Mathf.Lerp(0.15f, 0.35f, Mathf.Abs(turn));
    }

    void RestorePose()
    {
        foreach (var s in _bones) if (s.bone) s.bone.localRotation = s.restLocal;
    }

    void ReadKeyboard()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return;
        float target = kb.leftShiftKey.isPressed ? 1f : kb.wKey.isPressed ? 0.5f : kb.sKey.isPressed ? 0f : 0.2f;
        speed = target;
        turn = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
#elif ENABLE_LEGACY_INPUT_MANAGER
        speed = Input.GetKey(KeyCode.LeftShift) ? 1f : Input.GetKey(KeyCode.W) ? 0.5f : Input.GetKey(KeyCode.S) ? 0f : 0.2f;
        turn = Input.GetAxisRaw("Horizontal");
#endif
    }

    // ===================================================================== auto-find
    /// <summary>
    /// Finds the spine from the skinned mesh's bones: the bone furthest forward on the
    /// centre line is the snout, the one furthest back is the tail. The spine is the
    /// path between them through their common ancestor (the pivot). The tail chain
    /// stops at the fork into the tail lobes so the lobes stay stiff.
    /// </summary>
    [ContextMenu("Auto-Find Spine Bones")]
    public void AutoFindSpineBones()
    {
        var smr = GetComponentInChildren<SkinnedMeshRenderer>();
        var candidates = new List<Transform>();
        if (smr != null && smr.bones != null && smr.bones.Length > 0)
        {
            foreach (var b in smr.bones) if (b != null) candidates.Add(b);
        }
        else
        {
            foreach (var t in GetComponentsInChildren<Transform>())
                if (t != transform && t.GetComponent<Renderer>() == null) candidates.Add(t);
        }
        if (candidates.Count < 2) { Debug.LogWarning($"{name}: no bones found to search.", this); return; }

        Vector3 fwd = BodyForward(), right = transform.right, origin = transform.position;

        float minF = float.MaxValue, maxF = float.MinValue, maxSide = 0f;
        foreach (var b in candidates)
        {
            float f = Vector3.Dot(b.position - origin, fwd);
            minF = Mathf.Min(minF, f); maxF = Mathf.Max(maxF, f);
        }
        float length = Mathf.Max(0.001f, maxF - minF);
        float centreTol = 0.06f * length;

        // Guess which end is the head: pectoral fins stick out furthest sideways and sit forward.
        Transform widest = null;
        foreach (var b in candidates)
        {
            float side = Mathf.Abs(Vector3.Dot(b.position - origin, right));
            if (side > maxSide) { maxSide = side; widest = b; }
        }

        Transform head = null, tail = null;
        float best = float.MinValue, worst = float.MaxValue;
        foreach (var b in candidates)
        {
            if (Mathf.Abs(Vector3.Dot(b.position - origin, right)) > centreTol) continue;
            float f = Vector3.Dot(b.position - origin, fwd);
            if (f > best) { best = f; head = b; }
            if (f < worst) { worst = f; tail = b; }
        }
        if (head == null || tail == null || head == tail) { Debug.LogWarning($"{name}: could not find head and tail bones.", this); return; }

        if (widest != null)
        {
            float wf = Vector3.Dot(widest.position - origin, fwd);
            if (wf < (minF + maxF) * 0.5f)
            {
                // Pectorals are behind the middle, so this object's forward points at the tail.
                headIsBackward = !headIsBackward;
                fwd = -fwd;
                var tmp = head; head = tail; tail = tmp;
                Debug.Log($"{name}: the rig faces backwards, so 'Head Is Backward' was set to {headIsBackward}.", this);
            }
        }

        var headPath = PathFromRoot(head);
        var tailPath = PathFromRoot(tail);
        int common = 0;
        while (common < headPath.Count && common < tailPath.Count && headPath[common] == tailPath[common]) common++;
        if (common == 0) { Debug.LogWarning($"{name}: head and tail bones share no parent.", this); return; }
        Transform pivot = headPath[common - 1];

        var headChain = headPath.GetRange(common, headPath.Count - common); // pivot -> snout
        // Pivot -> tail, stopping at the last fork (where the tail fin lobes split off),
        // so the lobes ride along stiffly. Earlier forks (dorsal/anal fins) are passed through.
        int lastFork = -1;
        for (int i = common; i < tailPath.Count; i++)
            if (CountBoneChildren(tailPath[i], candidates) > 1) lastFork = i;
        int end = lastFork >= 0 ? lastFork : tailPath.Count - 1;
        var tailChain = tailPath.GetRange(common, end - common + 1);

        spineBones.Clear();
        for (int i = headChain.Count - 1; i >= 0; i--) spineBones.Add(headChain[i]);
        spineBones.Add(pivot);
        spineBones.AddRange(tailChain);
        pivotBone = pivot;

        Debug.Log($"{name}: spine = {string.Join(" > ", spineBones.ConvertAll(b => b.name))} (pivot {pivot.name})", this);
    }

    List<Transform> PathFromRoot(Transform t)
    {
        var path = new List<Transform>();
        for (var p = t; p != null && p != transform; p = p.parent) path.Add(p);
        path.Reverse();
        return path;
    }

    static int CountBoneChildren(Transform t, List<Transform> bones)
    {
        int n = 0;
        foreach (Transform c in t) if (bones.Contains(c)) n++;
        return n;
    }

    void OnDrawGizmosSelected()
    {
        for (int i = 0; i < spineBones.Count; i++)
        {
            var b = spineBones[i];
            if (b == null) continue;
            Gizmos.color = b == pivotBone ? Color.yellow : Color.cyan;
            Gizmos.DrawWireSphere(b.position, 0.03f * Mathf.Max(0.1f, _bodyLength));
            if (b.parent != null && spineBones.Contains(b.parent))
                Gizmos.DrawLine(b.position, b.parent.position);
        }
    }
}
