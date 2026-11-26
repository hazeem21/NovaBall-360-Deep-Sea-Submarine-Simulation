using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// PHASE 5 – Makes the ocean look different in each of the five depth zones,
/// with smooth (not sudden) changes as you go deeper.
///
/// Every frame it:
///   1. Reads the depth of the "depth source" (the camera now, the submarine later).
///   2. Works out the current zone (Epipelagic ... Hadal).
///   3. Blends between the "Depth Looks" in the list below and applies them to:
///        - the Sun            (sunlight fades away by ~1,000 m)
///        - the Ocean_Surface  (underwater fog colour + how far you can see + caustics)
///        - the Underwater Volume's Gradient Sky (the colour "behind" everything underwater)
///
/// Attach it to an empty GameObject called "Ocean_Zone_Manager" and fill the slots.
///
/// EDIT MODE PREVIEW: when "Preview In Edit Mode" is ticked, the look also updates while you are
/// NOT playing, following the Scene view camera (or Main Camera if "Follow Scene View Camera" is off).
/// </summary>
[ExecuteAlways]
public class OceanZoneManager : MonoBehaviour
{
    /// <summary>One "look" at a specific depth. The manager blends smoothly between these.</summary>
    [Serializable]
    public class DepthLook
    {
        public string label;
        [Tooltip("Depth in real metres where this look is exactly reached.")]
        public float depthMetres;
        [Tooltip("How much natural sunlight is left (1 = full sun, 0 = none).")]
        [Range(0f, 1f)] public float sunlight = 1f;
        [Tooltip("Colour of the water/fog around you.")]
        public Color waterColour = new Color(0.05f, 0.35f, 0.6f);
        [Tooltip("Underwater visibility multiplier. Visibility ≈ Absorption Distance (10) x this value, in metres.")]
        [Min(0.1f)] public float visibility = 8f;
        [Tooltip("Brightness of the underwater 'sky' gradient (1 = as set on the Underwater Volume).")]
        [Range(0f, 1f)] public float skyBrightness = 1f;

        public DepthLook() { }

        public DepthLook(string label, float depth, float sun, Color water, float visibility, float sky)
        {
            this.label = label; depthMetres = depth; sunlight = sun; waterColour = water;
            this.visibility = visibility; skyBrightness = sky;
        }
    }

    [Header("Depth")]
    [Tooltip("The shared depth scale (Assets/DeepOcean/Settings/OceanScaleSettings).")]
    public OceanScaleSettings scaleSettings;
    [Tooltip("Whose depth controls the look. Leave empty to use the Main Camera. In Phase 6 this becomes the submarine.")]
    public Transform depthSource;

    [Header("Scene objects to control")]
    [Tooltip("The Sun (Directional Light).")]
    public Light sun;
    [Tooltip("Sun intensity at the surface, in lux. Your scene's Sun was 130000.")]
    public float surfaceSunIntensity = 130000f;
    [Tooltip("The Ocean_Surface (HDRP Water Surface).")]
    public WaterSurface oceanSurface;
    [Tooltip("Caustics intensity at the surface. Fades out as the sunlight disappears.")]
    public float surfaceCausticsIntensity = 0.5f;
    [Tooltip("The 'Underwater Volume' box volume that has the Gradient Sky.")]
    public Volume underwaterVolume;

    [Header("Underwater sky colours at the surface (scaled down by Sky Brightness)")]
    [ColorUsage(false, true)] public Color skyTop    = new Color(0.039f, 0.310f, 0.478f);
    [ColorUsage(false, true)] public Color skyMiddle = new Color(0.039f, 0.243f, 0.388f);
    [ColorUsage(false, true)] public Color skyBottom = new Color(0.016f, 0.102f, 0.180f);

    [Header("Sub lights at real depth (added for HDRP)")]
    [Tooltip("HDRP's underwater fog darkens EVERY pixel by its depth below the water surface (it assumes all light is sunlight). " +
             "At real depths (hundreds of metres) that turns the submarine's own headlights pure black. " +
             "When ticked, visibility is raised just enough with depth so local lights (headlights, glowing creatures) still show.")]
    public bool keepLocalLightsVisible = true;
    [Tooltip("How much the depth term may darken local lights (optical depth). 2 = about 13% of red, 27% of blue light gets through. Higher = darker.")]
    [Min(0.1f)] public float maxDepthDarkening = 2f;

    [Header("How the ocean looks at each depth (blended smoothly)")]
    public List<DepthLook> depthLooks = DefaultLooks();

    [Header("Editor")]
    [Tooltip("Update the look while NOT in Play mode, so you can preview the zones.")]
    public bool previewInEditMode = true;
    [Tooltip("In Edit mode, follow the Scene view camera (fly down in the Scene view to preview).")]
    public bool followSceneViewCamera = true;

    [Header("Read-only (for checking)")]
    [SerializeField] float currentDepthMetres;
    [SerializeField] OceanZoneId currentZone;
    [SerializeField] string currentZoneName;

    /// <summary>Current depth in metres of the depth source.</summary>
    public float CurrentDepthMetres => currentDepthMetres;
    /// <summary>Current ocean zone.</summary>
    public OceanZoneId CurrentZone => currentZone;
    /// <summary>Called once whenever the depth source moves into a different zone.</summary>
    public event Action<OceanZoneId> ZoneChanged;

    bool hasZone;

    // ---------------------------------------------------------------------------------
    static List<DepthLook> DefaultLooks()
    {
        return new List<DepthLook>
        {
            //               label                       depth   sun    water colour                          visibility sky
            new DepthLook("Surface (Epipelagic)",            0f, 1.00f, new Color(0.050f, 0.350f, 0.600f), 8.0f, 1.000f),
            new DepthLook("50 m (Epipelagic)",              50f, 0.45f, new Color(0.040f, 0.280f, 0.500f), 7.0f, 0.800f),
            new DepthLook("200 m (start Mesopelagic)",     200f, 0.06f, new Color(0.020f, 0.160f, 0.330f), 5.0f, 0.450f),
            new DepthLook("1,000 m (start Bathypelagic)", 1000f, 0.00f, new Color(0.008f, 0.050f, 0.120f), 3.5f, 0.120f),
            new DepthLook("4,000 m (start Abyssopelagic)",4000f, 0.00f, new Color(0.003f, 0.015f, 0.045f), 2.5f, 0.040f),
            new DepthLook("6,000 m (start Hadal)",        6000f, 0.00f, new Color(0.0015f,0.007f, 0.020f), 2.0f, 0.015f),
            new DepthLook("11,000 m (trench floor)",     11000f, 0.00f, new Color(0.001f, 0.004f, 0.012f), 1.6f, 0.008f),
        };
    }

    void Reset()
    {
        depthLooks = DefaultLooks();
    }

    // ---------------------------------------------------------------------------------
    void OnEnable()
    {
#if UNITY_EDITOR
        EditorApplication.update += EditorTick;
#endif
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
#endif
        // Put everything back to the surface look so the scene is never saved "stuck" dark.
        ApplyLook(0f);
    }

#if UNITY_EDITOR
    void EditorTick()
    {
        if (!Application.isPlaying && previewInEditMode && this != null && isActiveAndEnabled)
            Refresh();
    }
#endif

    void Update()
    {
        if (Application.isPlaying)
            Refresh();
    }

    // ---------------------------------------------------------------------------------
    void Refresh()
    {
        if (scaleSettings == null) return;

        Vector3 position = GetSourcePosition();
        float depth = scaleSettings.WorldYToDepthMetres(position.y);
        currentDepthMetres = depth;

        OceanZoneId zone = OceanZones.FromDepth(depth);
        if (!hasZone || zone != currentZone)
        {
            currentZone = zone;
            currentZoneName = OceanZones.Get(zone).displayName;
            if (hasZone && Application.isPlaying)
                Debug.Log($"[OceanZoneManager] Entered {currentZoneName} at {depth:N0} m");
            hasZone = true;
            if (Application.isPlaying) ZoneChanged?.Invoke(zone);
        }

        ApplyLook(depth);
    }

    Vector3 GetSourcePosition()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && followSceneViewCamera && SceneView.lastActiveSceneView != null
            && SceneView.lastActiveSceneView.camera != null)
            return SceneView.lastActiveSceneView.camera.transform.position;
#endif
        if (depthSource != null) return depthSource.position;
        if (Camera.main != null) return Camera.main.transform.position;
        return Vector3.zero;
    }

    /// <summary>Blends the depth looks for this depth and applies them to the scene.</summary>
    void ApplyLook(float depth)
    {
        if (depthLooks == null || depthLooks.Count == 0) return;
        DepthLook look = Evaluate(depth);

        if (sun != null)
            sun.intensity = surfaceSunIntensity * look.sunlight;   // HDRP 17: set Light.intensity directly

        if (oceanSurface != null)
        {
            oceanSurface.scatteringColor = look.waterColour;
            float visibility = look.visibility;
            if (keepLocalLightsVisible && scaleSettings != null)
            {
                // HDRP extinction per Unity unit at multiplier 1 (strongest colour channel), see WaterSurface.extinction.
                Color rc = oceanSurface.refractionColor;
                float strongest = 1f - Mathf.Min(rc.r, Mathf.Min(rc.g, rc.b));
                float extinction = -Mathf.Log(0.02f) / Mathf.Max(0.01f, oceanSurface.absorptionDistance) * Mathf.Max(0.01f, strongest);
                float unitsBelowSurface = depth / Mathf.Max(0.0001f, scaleSettings.metresPerUnit);
                visibility = Mathf.Max(visibility, extinction * unitsBelowSurface / maxDepthDarkening);
            }
            oceanSurface.absorptionDistanceMultiplier = visibility;
            oceanSurface.causticsIntensity = surfaceCausticsIntensity * look.sunlight;
        }

        if (underwaterVolume != null)
        {
            // In Play mode use a private copy (.profile) so the saved asset is not changed.
            VolumeProfile profile = Application.isPlaying ? underwaterVolume.profile : underwaterVolume.sharedProfile;
            if (profile != null && profile.TryGet(out GradientSky gradient))
            {
                gradient.top.value    = skyTop    * look.skyBrightness;
                gradient.middle.value = skyMiddle * look.skyBrightness;
                gradient.bottom.value = skyBottom * look.skyBrightness;
            }
        }
    }

    /// <summary>Finds the two looks either side of this depth and blends them smoothly.</summary>
    DepthLook Evaluate(float depth)
    {
        DepthLook first = depthLooks[0];
        if (depth <= first.depthMetres) return first;

        for (int i = 0; i < depthLooks.Count - 1; i++)
        {
            DepthLook a = depthLooks[i], b = depthLooks[i + 1];
            if (depth <= b.depthMetres)
            {
                float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a.depthMetres, b.depthMetres, depth));
                return new DepthLook("blend", depth,
                    Mathf.Lerp(a.sunlight, b.sunlight, t),
                    Color.Lerp(a.waterColour, b.waterColour, t),
                    Mathf.Lerp(a.visibility, b.visibility, t),
                    Mathf.Lerp(a.skyBrightness, b.skyBrightness, t));
            }
        }
        return depthLooks[depthLooks.Count - 1];
    }
}
