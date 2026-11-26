using UnityEngine;

/// <summary>
/// ONE shared place that defines how real-world ocean depth (metres) maps to Unity's Y axis.
///
/// Everything that cares about depth (the terrain generator, OceanDepthManager, the depth UI,
/// future creature spawners) should read THIS asset, so they always agree with each other.
///
///   Ocean surface      = surfaceY (normally 0)
///   Depth increases    = going down (negative Y)
///   depth in metres    = (surfaceY - y) * metresPerUnit
///
/// With metresPerUnit = 1 (TRUE Mariana Trench scale, the default):
///   Y =    -200  ->    200 m (bottom of Epipelagic)
///   Y =   -1000  ->  1,000 m (bottom of Mesopelagic)
///   Y =   -4000  ->  4,000 m (bottom of Bathypelagic)
///   Y =   -6000  ->  6,000 m (bottom of Abyssopelagic)
///   Y =  -10994  -> 10,994 m (Challenger Deep, Hadal trench floor)
///
/// Create it with: Project window > right-click > Create > Deep Ocean > Ocean Scale Settings
/// </summary>
[CreateAssetMenu(fileName = "OceanScaleSettings", menuName = "Deep Ocean/Ocean Scale Settings")]
public class OceanScaleSettings : ScriptableObject
{
    [Tooltip("World Y position of the ocean surface. Keep this at 0 to match the Ocean_Surface object.")]
    public float surfaceY = 0f;

    [Tooltip("How many real metres of DEPTH one Unity unit of vertical distance represents.\n" +
             "1 = realistic but a very long dive (11 km). 5 = recommended. 10 = fast dive.\n" +
             "IMPORTANT: if you change this after generating the terrain, regenerate the terrain.")]
    [Min(0.1f)]
    public float metresPerUnit = 1f;

    [Tooltip("Deepest point of the experience in metres. 10,994 m = Challenger Deep, Mariana Trench.")]
    public float maxDepthMetres = 10994f;

    /// <summary>Converts a world Y position into depth in metres (never negative).</summary>
    public float WorldYToDepthMetres(float worldY)
    {
        return Mathf.Max(0f, (surfaceY - worldY) * metresPerUnit);
    }

    /// <summary>Converts a depth in metres into the matching world Y position.</summary>
    public float DepthMetresToWorldY(float depthMetres)
    {
        return surfaceY - depthMetres / metresPerUnit;
    }

    /// <summary>The world Y of the deepest point (e.g. -2200 when metresPerUnit = 5).</summary>
    public float DeepestWorldY => DepthMetresToWorldY(maxDepthMetres);
}
