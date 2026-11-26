using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Green sonar screen on the LEFT cockpit panel (world space, so it works in VR).
///
/// Two ways of "seeing", like a real submarine:
///  PASSIVE (always on, silent): the sweep turns and LISTENS. Creatures that are moving make noise and show
///          as bearing lines (you know the direction, not the distance). Still things and terrain are invisible.
///  ACTIVE (a ping: Tab, or automatically every few seconds when the floodlights are off):
///          a ring goes out from the centre of the screen at the same speed as the green wave in the water.
///          Each echo only appears when it has had time to COME BACK (2 x distance / speed), so near things
///          appear first and far things later. Echoes then fade over a few seconds.
///   - Terrain / rocks: real raycasts at a few downward angles (needs colliders; the seabed has them).
///   - Creatures: everything under "Sea Creatures (by depth zone)" (they have no colliders).
/// Screen is heading-up: 0 = straight ahead, 90 = right, 180 = behind, 270 = left.
///
/// Put it on the submarine root (Orientation Cube). It builds the screen itself at Start.
/// </summary>
public class SonarDisplay : MonoBehaviour
{
    [Header("Screen")]
    [Tooltip("Material using Custom/SonarDisplay.")]
    public Material displayMaterial;
    [Tooltip("Name of the pilot's eye object (the panel is placed relative to it).")]
    public string eyeName = "Pilot_Eye_Position";
    [Tooltip("Panel position relative to the eye, in the submarine's axes (x = right, y = up, z = forward), metres. Must be further than the cockpit camera's near plane (0.4 m).")]
    public Vector3 panelOffset = new Vector3(-0.40f, -0.22f, 0.62f);
    [Tooltip("Panel size (m).")]
    public float panelSize = 0.40f;

    [Header("Range and sweep")]
    [Tooltip("Range shown at the outer ring (m).")]
    public float range = 150f;
    [Tooltip("Seconds for one full turn of the listening sweep.")]
    public float sweepPeriod = 4f;

    [Header("Active (ping)")]
    [Tooltip("Speed of the ping on screen (m/s). Taken from SonarScanEffect if it is on the sub, so the screen matches the wave in the water.")]
    public float pingSpeed = 60f;
    [Tooltip("How long an echo stays on screen after it comes back (s).")]
    public float echoFadeTime = 5f;
    [Tooltip("Downward angles (degrees) the ping listens at, to catch the seabed and rocks.")]
    public float[] depressionAngles = { 0f, 10f, 25f, 45f };
    [Tooltip("Layers the ping can hit.")]
    public LayerMask detectLayers = ~0;

    [Header("Passive (listening)")]
    [Tooltip("How far moving creatures can be heard (m).")]
    public float hearingRange = 400f;
    [Tooltip("A creature must move at least this fast (m/s) to be heard.")]
    public float minNoisySpeed = 0.15f;

    [Header("Contacts")]
    [Tooltip("Parent object(s) whose children are creatures.")]
    public string[] contactRoots = { "Sea Creatures (by depth zone)" };

    const int BearingBins = 360;
    const int RangeBins = 128;

    struct PendingEcho { public float returnTime; public int bearing; public float dist; public int spread; public float strength; }

    Texture2D echoTex;
    byte[] pixels;                  // RG8
    float[] active;                 // R: active echoes, faded over time
    float[] passive;                // G: passive bearing lines, refreshed by the sweep
    Material mat;
    float sweep;
    int lastBin = -1;
    Transform eye;
    readonly List<Transform> contacts = new List<Transform>();
    readonly List<float> contactSize = new List<float>();
    readonly List<Vector3> contactLastPos = new List<Vector3>();
    readonly List<float> contactSpeed = new List<float>();
    readonly List<PendingEcho> pending = new List<PendingEcho>();
    float pingStartTime = -1f;

    void OnEnable() { SubmarineLightAndSonar.OnPing += OnPing; }
    void OnDisable() { SubmarineLightAndSonar.OnPing -= OnPing; }

    void Start()
    {
        active = new float[BearingBins * RangeBins];
        passive = new float[BearingBins * RangeBins];
        pixels = new byte[BearingBins * RangeBins * 2];
        echoTex = new Texture2D(BearingBins, RangeBins, TextureFormat.RG16, false, true)
        {
            name = "SonarEchoes", filterMode = FilterMode.Bilinear, wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp
        };

        var scan = GetComponent<SonarScanEffect>();
        if (scan != null) pingSpeed = scan.speed;

        foreach (Transform t in GetComponentsInChildren<Transform>(true))
            if (t.name == eyeName) { eye = t; break; }

        BuildPanel();
        FindContacts();
    }

    void BuildPanel()
    {
        if (displayMaterial == null) { Debug.LogWarning("[SonarDisplay] No display material."); return; }
        Transform parent = eye != null ? eye.parent : transform;
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "Sonar Display Panel";
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        Vector3 eyeLocal = eye != null ? eye.localPosition : Vector3.zero;
        go.transform.localPosition = eyeLocal + panelOffset;
        // face the pilot's eye (a Quad shows its face towards -Z)
        go.transform.localRotation = Quaternion.LookRotation(go.transform.localPosition - eyeLocal, Vector3.up);
        go.transform.localScale = Vector3.one * panelSize;
        var r = go.GetComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        mat = new Material(displayMaterial);
        mat.SetTexture("_EchoTex", echoTex);
        r.sharedMaterial = mat;
    }

    void FindContacts()
    {
        foreach (string rootName in contactRoots)
        {
            GameObject root = GameObject.Find(rootName);
            if (root == null) continue;
            foreach (Transform zone in root.transform)
                foreach (Transform c in zone)
                {
                    if (c.name.StartsWith("Spawn")) continue;
                    var rends = c.GetComponentsInChildren<Renderer>();
                    if (rends.Length == 0) continue;
                    Bounds b = rends[0].bounds;
                    foreach (var rr in rends) b.Encapsulate(rr.bounds);
                    contacts.Add(c);
                    contactSize.Add(Mathf.Max(b.extents.x, b.extents.z));
                    contactLastPos.Add(c.position);
                    contactSpeed.Add(0f);
                }
        }
    }

    Vector3 FlatForward()
    {
        Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(transform.up, Vector3.up);
        return fwd.normalized;
    }

    float BearingOf(Vector3 flatDir, Vector3 fwd)
    {
        float b = Vector3.SignedAngle(fwd, flatDir, Vector3.up);
        return b < 0 ? b + 360f : b;
    }

    // ---------------------------------------------------------------- ACTIVE
    /// <summary>A ping went out: work out every echo now, and when each one will be back.</summary>
    void OnPing(Vector3 origin, float maxRange)
    {
        if (active == null) return;
        pingStartTime = Time.time;
        pending.Clear();
        Vector3 up = Vector3.up;
        Vector3 fwd = FlatForward();
        float r = Mathf.Min(range, maxRange > 0 ? Mathf.Max(maxRange, range) : range);

        // terrain and rocks, every 2 degrees all around
        for (int b = 0; b < BearingBins; b += 2)
        {
            Vector3 flat = Quaternion.AngleAxis(b, up) * fwd;
            Vector3 right = Vector3.Cross(up, flat);
            foreach (float dep in depressionAngles)
            {
                Vector3 dir = Quaternion.AngleAxis(dep, right) * flat;
                float maxDist = r / Mathf.Max(0.2f, Mathf.Cos(dep * Mathf.Deg2Rad));
                RaycastHit hit;
                if (!Physics.Raycast(origin + dir * 3f, dir, out hit, maxDist, detectLayers, QueryTriggerInteraction.Ignore)) continue;
                if (hit.transform.IsChildOf(transform)) continue;
                float slant = hit.distance + 3f;
                float h = Vector3.ProjectOnPlane(hit.point - origin, up).magnitude;
                Queue(slant, b, h, 0, 0.7f);
                Queue(slant, (b + 1) % BearingBins, h, 0, 0.7f);
            }
        }

        // creatures
        for (int i = 0; i < contacts.Count; i++)
        {
            Transform c = contacts[i];
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            Vector3 v = Vector3.ProjectOnPlane(c.position - origin, up);
            float h = v.magnitude;
            if (h > r || h < 1f) continue;
            float slant = Vector3.Distance(c.position, origin);
            int bearing = Mathf.RoundToInt(BearingOf(v, fwd)) % BearingBins;
            int halfW = Mathf.Max(1, Mathf.RoundToInt(Mathf.Rad2Deg * Mathf.Atan2(contactSize[i], h)));
            int spread = Mathf.Clamp(Mathf.RoundToInt(contactSize[i] / range * RangeBins), 1, 6);
            for (int k = -halfW; k <= halfW; k++)
                Queue(slant, (bearing + k + BearingBins) % BearingBins, h, spread, 1f);
        }
    }

    void Queue(float slant, int bearing, float horizontal, int spread, float strength)
    {
        pending.Add(new PendingEcho
        {
            returnTime = Time.time + 2f * slant / Mathf.Max(1f, pingSpeed),   // there and back again
            bearing = bearing, dist = horizontal, spread = spread, strength = strength
        });
    }

    // ---------------------------------------------------------------- PASSIVE
    /// <summary>The sweep listens along one bearing: moving creatures show as a bearing line.</summary>
    void ListenBearing(int b, Vector3 fwd)
    {
        for (int r = 0; r < RangeBins; r++) passive[r * BearingBins + b] = 0f;
        Vector3 origin = transform.position;
        for (int i = 0; i < contacts.Count; i++)
        {
            if (contactSpeed[i] < minNoisySpeed) continue;
            Transform c = contacts[i];
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            Vector3 v = Vector3.ProjectOnPlane(c.position - origin, Vector3.up);
            float d = Vector3.Distance(c.position, origin);
            if (d > hearingRange || v.magnitude < 1f) continue;
            float bearing = BearingOf(v, fwd);
            if (Mathf.Abs(Mathf.DeltaAngle(bearing, b)) > 1.5f) continue;
            // louder = bigger and faster; quieter with distance
            float loud = Mathf.Clamp01((0.35f + contactSize[i] * 0.15f) * Mathf.Clamp01(contactSpeed[i] / 1.5f + 0.3f) * (1f - d / hearingRange) * 1.6f);
            for (int r = 8; r < RangeBins; r++)
            {
                int idx = r * BearingBins + b;
                float along = 0.55f + 0.45f * Mathf.PerlinNoise(r * 0.15f, Time.time * 2f + i);  // flickering line
                passive[idx] = Mathf.Max(passive[idx], loud * along);
            }
        }
    }

    // ---------------------------------------------------------------- UPDATE
    void Update()
    {
        if (active == null) return;
        float dt = Time.deltaTime;

        // how fast each creature is moving (for passive listening)
        for (int i = 0; i < contacts.Count; i++)
        {
            if (contacts[i] == null) continue;
            Vector3 p = contacts[i].position;
            float s = dt > 0f ? (p - contactLastPos[i]).magnitude / dt : 0f;
            contactSpeed[i] = Mathf.Lerp(contactSpeed[i], s, 0.1f);
            contactLastPos[i] = p;
        }

        // sweep listens
        Vector3 fwd = FlatForward();
        sweep = Mathf.Repeat(sweep + 360f * dt / Mathf.Max(0.5f, sweepPeriod), 360f);
        int bin = Mathf.FloorToInt(sweep) % BearingBins;
        if (lastBin < 0) lastBin = bin - 1;
        int steps = (bin - lastBin + BearingBins) % BearingBins;
        if (steps > 90) steps = 90;
        for (int i = 1; i <= steps; i++) ListenBearing((lastBin + i) % BearingBins, fwd);
        lastBin = bin;

        // active echoes fade, then the ones that have come back are painted
        float decay = Mathf.Exp(-dt / Mathf.Max(0.1f, echoFadeTime * 0.4f));
        for (int i = 0; i < active.Length; i++) active[i] *= decay;
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (pending[i].returnTime > Time.time) continue;
            Paint(pending[i]);
            pending.RemoveAt(i);
        }

        // ping ring on screen (outgoing wave)
        float ring = -1f;
        if (pingStartTime >= 0f)
        {
            float rr = (Time.time - pingStartTime) * pingSpeed / range;
            if (rr <= 1f) ring = rr; else pingStartTime = -1f;
        }

        for (int i = 0; i < active.Length; i++)
        {
            pixels[i * 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(active[i] * 255f), 0, 255);
            pixels[i * 2 + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(passive[i] * 255f), 0, 255);
        }
        echoTex.SetPixelData(pixels, 0);
        echoTex.Apply(false);
        if (mat != null)
        {
            mat.SetFloat("_Sweep", sweep);
            mat.SetFloat("_PingRing", ring);
        }
    }

    void Paint(PendingEcho e)
    {
        int rb = Mathf.RoundToInt(e.dist / range * (RangeBins - 1));
        for (int s = -e.spread - 1; s <= e.spread + 1; s++)
        {
            int r = rb + s;
            if (r < 0 || r >= RangeBins) continue;
            float f = e.strength * (Mathf.Abs(s) <= e.spread ? 1f : 0.4f);
            int idx = r * BearingBins + e.bearing;
            if (f > active[idx]) active[idx] = f;
        }
    }

    void OnDestroy()
    {
        if (echoTex != null) Destroy(echoTex);
        if (mat != null) Destroy(mat);
    }
}
