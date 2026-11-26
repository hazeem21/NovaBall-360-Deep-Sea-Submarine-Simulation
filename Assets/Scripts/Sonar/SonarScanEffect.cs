using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Makes each sonar ping VISIBLE in the world: a green shell travels outward from the submarine and
/// lights up the seabed, rocks and creatures as it passes, leaving a short fading echo.
///
/// It listens to SubmarineLightAndSonar.OnPing (your teammate's script decides WHEN to ping:
/// automatically every few seconds while the floodlights are off, or Tab). Drawing is done by an HDRP
/// full-screen Custom Pass (FullScreen/SonarScan shader), created here at runtime, so it also works in VR.
///
/// Put it on the submarine root (Orientation Cube).
/// </summary>
public class SonarScanEffect : MonoBehaviour
{
    [Tooltip("Material using the FullScreen/SonarScan shader.")]
    public Material scanMaterial;
    [Tooltip("How fast the visible ping travels (m/s). Real sound is ~1,500 m/s, which would be instant; this is slowed down so you can see it.")]
    public float speed = 60f;
    [Tooltip("Width of the bright leading edge (m).")]
    public float frontWidth = 2.5f;
    [Tooltip("Length of the fading echo behind the front (m).")]
    public float echoTrail = 25f;
    [ColorUsage(false, true)] public Color colour = new Color(0.25f, 1f, 0.45f);
    [Tooltip("Brightness of the ping (screen units, the same at every depth).")]
    public float brightness = 1.5f;

    [Header("Ping visible in open water")]
    [Tooltip("Material using Custom/SonarSpeck: specks in the water that flash as the wave passes.")]
    public Material speckMaterial;
    [Tooltip("Size of the box of specks around the sub (m).")]
    public float speckArea = 120f;
    public int speckCount = 14000;

    static readonly int OriginId = Shader.PropertyToID("_SonarPingOrigin");
    static readonly int ParamsId = Shader.PropertyToID("_SonarPingParams");
    static readonly int ColorId = Shader.PropertyToID("_SonarPingColor");

    Vector3 origin;
    float radius, range;
    bool alive;
    CustomPassVolume volume;
    ParticleSystem specks;
    Vector3 lastFillPos;

    void OnEnable()
    {
        SubmarineLightAndSonar.OnPing += StartPing;
        EnsurePass();
    }

    void Start()
    {
        BuildSpecks();
    }

    /// <summary>World-space particles spread around the sub. They are invisible until the ping passes them.</summary>
    void BuildSpecks()
    {
        if (speckMaterial == null) return;
        var go = new GameObject("Sonar Specks");
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        specks = ps;
        lastFillPos = transform.position;
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(20f, 30f);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.2f, 0.5f);
        main.maxParticles = speckCount;
        main.prewarm = true;
        main.loop = true;
        var em = ps.emission;
        em.rateOverTime = speckCount / 25f;
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = Vector3.one * speckArea;
        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sharedMaterial = speckMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.maxParticleSize = 0.3f;
        ps.Play();
    }

    void OnDisable()
    {
        SubmarineLightAndSonar.OnPing -= StartPing;
        Shader.SetGlobalVector(OriginId, Vector4.zero);
    }

    void EnsurePass()
    {
        if (volume != null || scanMaterial == null) return;
        volume = gameObject.AddComponent<CustomPassVolume>();
        volume.isGlobal = true;
        volume.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;
        var pass = new FullScreenCustomPass
        {
            name = "Sonar Scan",
            fullscreenPassMaterial = scanMaterial,
            materialPassName = "Sonar Scan",
            fetchColorBuffer = false,
            targetColorBuffer = CustomPass.TargetBuffer.Camera,
            targetDepthBuffer = CustomPass.TargetBuffer.Camera,
            clearFlags = UnityEngine.Rendering.ClearFlag.None,
        };
        volume.customPasses.Add(pass);
    }

    /// <summary>Start a visible ping from a point. Called by SubmarineLightAndSonar.</summary>
    public void StartPing(Vector3 from, float maxRange)
    {
        origin = from;
        range = Mathf.Max(1f, maxRange);
        radius = 0f;
        alive = true;
    }

    void Update()
    {
        // after a big jump (e.g. the zone teleport keys) refill the specks around the new position at once
        if (specks != null && (transform.position - lastFillPos).sqrMagnitude > speckArea * speckArea * 0.25f)
        {
            specks.Clear(true);
            specks.Simulate(25f, true, true);
            specks.Play(true);
            lastFillPos = transform.position;
        }
        if (specks != null && (transform.position - lastFillPos).sqrMagnitude > 1f && Time.frameCount % 30 == 0)
            lastFillPos = Vector3.Lerp(lastFillPos, transform.position, 0.5f);

        if (alive)
        {
            radius += speed * Time.deltaTime;
            if (radius > range + echoTrail) alive = false;
        }
        Shader.SetGlobalVector(OriginId, new Vector4(origin.x, origin.y, origin.z, alive ? 1f : 0f));
        Shader.SetGlobalVector(ParamsId, new Vector4(radius, range, frontWidth, echoTrail));
        Shader.SetGlobalVector(ColorId, new Vector4(colour.r, colour.g, colour.b, brightness));
    }
}
