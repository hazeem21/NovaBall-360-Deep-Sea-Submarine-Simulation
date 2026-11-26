using UnityEngine;

/// <summary>The five ocean depth zones, from the surface down.</summary>
public enum OceanZoneId
{
    Epipelagic,
    Mesopelagic,
    Bathypelagic,
    Abyssopelagic,
    Hadal
}

/// <summary>
/// ONE shared definition of the five ocean zones (depths in real METRES),
/// following the ocean-depth infographic:
///
///   Epipelagic     0 –    200 m   (sunlight zone)
///   Mesopelagic  200 –  1,000 m   (twilight zone)
///   Bathypelagic 1,000 – 4,000 m  (midnight zone)
///   Abyssopelagic 4,000 – 6,000 m (abyss)
///   Hadal        6,000 – 11,000 m (trenches)
///
/// Every script (visual zones, depth manager, UI, future creature spawners)
/// uses this, so they always agree on where each zone starts and ends.
/// </summary>
public static class OceanZones
{
    public struct ZoneInfo
    {
        public OceanZoneId id;
        public string displayName;
        public float minDepth;   // metres
        public float maxDepth;   // metres

        public ZoneInfo(OceanZoneId id, string displayName, float minDepth, float maxDepth)
        {
            this.id = id;
            this.displayName = displayName;
            this.minDepth = minDepth;
            this.maxDepth = maxDepth;
        }
    }

    public static readonly ZoneInfo[] All =
    {
        new ZoneInfo(OceanZoneId.Epipelagic,    "EPIPELAGIC ZONE",        0f,   200f),
        new ZoneInfo(OceanZoneId.Mesopelagic,   "MESOPELAGIC ZONE",     200f,  1000f),
        new ZoneInfo(OceanZoneId.Bathypelagic,  "BATHYPELAGIC ZONE",   1000f,  4000f),
        new ZoneInfo(OceanZoneId.Abyssopelagic, "ABYSSOPELAGIC ZONE",  4000f,  6000f),
        new ZoneInfo(OceanZoneId.Hadal,         "HADAL ZONE",          6000f, 11000f),
    };

    /// <summary>Which zone a depth (in metres) belongs to.</summary>
    public static OceanZoneId FromDepth(float depthMetres)
    {
        for (int i = 0; i < All.Length - 1; i++)
            if (depthMetres < All[i].maxDepth)
                return All[i].id;
        return OceanZoneId.Hadal;   // anything deeper than 6,000 m
    }

    /// <summary>Full information about a zone.</summary>
    public static ZoneInfo Get(OceanZoneId id) => All[(int)id];

    /// <summary>How far through its zone a depth is: 0 = top of the zone, 1 = bottom.</summary>
    public static float ProgressThroughZone(float depthMetres)
    {
        ZoneInfo zone = Get(FromDepth(depthMetres));
        return Mathf.Clamp01(Mathf.InverseLerp(zone.minDepth, zone.maxDepth, depthMetres));
    }
}
