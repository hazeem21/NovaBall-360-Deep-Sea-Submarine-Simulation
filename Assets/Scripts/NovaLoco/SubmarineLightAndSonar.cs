using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;   // was UnityEngine.Rendering.Universal (URP) - ported to HDRP

public class SubmarineLightAndSonar : MonoBehaviour
{
    [Header("Controls")]
    [SerializeField] KeyCode toggleLightKey = KeyCode.F;
    [SerializeField] KeyCode manualPingKey = KeyCode.Tab;

    [Header("Submarine Floodlights")]
    [SerializeField] List<Light> externalFloodlights = new List<Light>();
    [SerializeField] List<Light> cockpitInteriorLights = new List<Light>();

    [Header("Sonar Ping Simulation")]
    [SerializeField] Transform sonarWaveSphere;         // Assign SonarWaveSphere child
    [SerializeField] float maxSonarRange = 45.0f;       // Acoustic detection radius in meters
    [SerializeField] float pingInterval = 3.5f;         // Seconds between automatic sonar sweeps
    [SerializeField] float waveExpandSpeed = 22.0f;     // Speed of sound wave expansion
    [SerializeField] LayerMask detectableLayers = ~0;   // Terrain, rocks, creatures

    [Header("Night Vision / Sonar Atmosphere (HDRP Global Volume)")]
    [SerializeField] Volume postProcessVolume;          // Assign your scene's Global Volume
    [SerializeField] Color sonarTintColor = new Color(0.25f, 1.0f, 0.65f, 1f);
    [SerializeField] Color sonarFogColor = new Color(0.02f, 0.18f, 0.14f, 1f);
    [SerializeField] Color normalOceanFogColor = new Color(0.02f, 0.08f, 0.15f, 1f);
    [SerializeField] float nightVisionExposureBoost = 2.2f;

    [Header("Sonar look (added): world-space ping instead of a screen tint")]
    [Tooltip("Old look: tint the WHOLE screen green in sonar mode. Off = only the ping itself glows (SonarScanEffect).")]
    [SerializeField] bool tintWholeScreen = false;
    [Tooltip("Old look: show the SonarWaveSphere around the sub. Off = use SonarScanEffect's world-space ping.")]
    [SerializeField] bool showWaveSphere = false;

    /// <summary>Raised on every ping: (origin, max range). SonarScanEffect listens to draw the ping in the world.</summary>
    public static event System.Action<Vector3, float> OnPing;

    [Header("Audio (Optional)")]
    [SerializeField] AudioSource sonarAudioSource;
    [SerializeField] AudioClip sonarPingClip;

    private bool lightsOn = true;
    private float pingTimer;
    private float currentWaveRadius;
    private bool isWaveExpanding;

    private ColorAdjustments colorAdjustments;
    private Material waveMaterial;
    private float baseWaveAlpha = 0.6f;
    // HDRP port: remember each cockpit light's own brightness (HDRP uses real units like lumens,
    // so a fixed 1.0 would make them almost invisible). Dimming now scales from that value.
    private readonly Dictionary<Light, float> interiorBaseIntensity = new Dictionary<Light, float>();

    void Start()
    {
        // Auto-find lights under NOVA_ABYSS_Submarine if list wasn't manually filled
        if (externalFloodlights.Count == 0)
        {
            Light[] allChildLights = GetComponentsInChildren<Light>(true);
            foreach (Light l in allChildLights)
            {
                if (l.type == LightType.Spot)
                    externalFloodlights.Add(l);
                else
                    cockpitInteriorLights.Add(l);
            }
        }

        // Grab ColorAdjustments from your Global Volume for realistic night-vision intensification
        if (postProcessVolume != null && postProcessVolume.profile != null)
        {
            if (!postProcessVolume.profile.TryGet(out colorAdjustments))
            {
                colorAdjustments = postProcessVolume.profile.Add<ColorAdjustments>(true);
            }
        }

        if (sonarWaveSphere != null)
        {
            Renderer r = sonarWaveSphere.GetComponent<Renderer>();
            if (r != null)
            {
                waveMaterial = r.material;
                if (waveMaterial.HasProperty("_Alpha"))
                    baseWaveAlpha = waveMaterial.GetFloat("_Alpha");
            }
            sonarWaveSphere.gameObject.SetActive(false);
        }

        ApplyLightingMode(true);
    }

    void Update()
    {
        // Toggle Floodlights ON / Sonar Night-Vision OFF with 'F'
        if (Input.GetKeyDown(toggleLightKey))
        {
            lightsOn = !lightsOn;
            ApplyLightingMode(lightsOn);
        }

        // When Floodlights are OFF, run periodic Active Sonar Pings
        if (!lightsOn)
        {
            pingTimer += Time.deltaTime;
            if (pingTimer >= pingInterval)
            {
                TriggerSonarPing();
            }
        }
        // Manual ping (Tab) now works with the lights on too (was: sonar mode only)
        if (Input.GetKeyDown(manualPingKey))
        {
            TriggerSonarPing();
        }

        AnimateSonarWave();
    }

    private void ApplyLightingMode(bool enableFloodlights)
    {
        // 1. Toggle external spotlights
        foreach (Light spot in externalFloodlights)
        {
            if (spot != null) spot.enabled = enableFloodlights;
        }

        // Dim cockpit lights in sonar mode so glass glare doesn't blind the pilot
        foreach (Light interior in cockpitInteriorLights)
        {
            if (interior == null) continue;
            if (!interiorBaseIntensity.ContainsKey(interior)) interiorBaseIntensity[interior] = interior.intensity;
            interior.intensity = interiorBaseIntensity[interior] * (enableFloodlights ? 1.0f : 0.25f);   // was 1.0f : 0.25f (URP units)
        }

        // 2. Configure underwater Fog & Low-Light Night Vision
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;

        if (enableFloodlights)
        {
            // Optical Mode: Dark deep ocean, visibility relies on your floodlights
            RenderSettings.fogColor = normalOceanFogColor;
            RenderSettings.fogDensity = 0.035f;
            RenderSettings.ambientLight = new Color(0.03f, 0.07f, 0.12f);

            if (colorAdjustments != null)
            {
                colorAdjustments.colorFilter.Override(Color.white);
                colorAdjustments.postExposure.Override(0f);
                colorAdjustments.saturation.Override(0f);
            }

            isWaveExpanding = false;
            if (sonarWaveSphere != null) sonarWaveSphere.gameObject.SetActive(false);
        }
        else
        {
            // Sonar Night-Vision Mode: Amplified green/cyan phosphor contrast, extended fog visibility
            RenderSettings.fogColor = sonarFogColor;
            RenderSettings.fogDensity = 0.018f;
            RenderSettings.ambientLight = new Color(0.12f, 0.35f, 0.28f);

            if (colorAdjustments != null && tintWholeScreen)
            {
                colorAdjustments.colorFilter.Override(sonarTintColor);
                colorAdjustments.postExposure.Override(nightVisionExposureBoost);
                colorAdjustments.saturation.Override(-45f); // Desaturate toward monochrome sonar display
            }

            // Fire an immediate sonar ping the moment lights go out
            TriggerSonarPing();
        }
    }

    public void TriggerSonarPing()
    {
        pingTimer = 0f;
        currentWaveRadius = 0.5f;
        isWaveExpanding = true;

        if (sonarWaveSphere != null && showWaveSphere)
        {
            sonarWaveSphere.gameObject.SetActive(true);
            sonarWaveSphere.localScale = Vector3.one * currentWaveRadius;
        }

        if (OnPing != null) OnPing(transform.position, maxSonarRange);

        if (sonarAudioSource != null && sonarPingClip != null)
        {
            sonarAudioSource.PlayOneShot(sonarPingClip);
        }

        // Acoustic Echo Detection: Scan surrounding obstacles/creatures
        Collider[] hits = Physics.OverlapSphere(transform.position, maxSonarRange, detectableLayers, QueryTriggerInteraction.Ignore);
        foreach (Collider col in hits)
        {
            if (col.transform.IsChildOf(transform)) continue;

            // Optional: If an obstacle or creature has a SonarTarget script, notify it to glow briefly
            col.SendMessage("OnSonarPingHit", SendMessageOptions.DontRequireReceiver);
        }
    }

    private void AnimateSonarWave()
    {
        if (!isWaveExpanding || sonarWaveSphere == null || !showWaveSphere) return;

        currentWaveRadius += waveExpandSpeed * Time.deltaTime;
        // Diameter is radius * 2
        sonarWaveSphere.localScale = Vector3.one * (currentWaveRadius * 2f);

        // Fade out acoustic wave as it reaches max range (inverse-square dissipation)
        float progress = Mathf.Clamp01(currentWaveRadius / maxSonarRange);
        if (waveMaterial != null && waveMaterial.HasProperty("_Alpha"))
        {
            waveMaterial.SetFloat("_Alpha", Mathf.Lerp(baseWaveAlpha, 0f, progress));
        }

        if (currentWaveRadius >= maxSonarRange)
        {
            isWaveExpanding = false;
            sonarWaveSphere.gameObject.SetActive(false);
        }
    }
}