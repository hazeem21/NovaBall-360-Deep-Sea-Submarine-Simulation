using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// EDITOR-ONLY tool that builds the deep-ocean seabed, including a true-scale Mariana Trench.
///
/// Open it from:  Tools > Deep Ocean > Seabed Generator
///
/// The world is made of a GRID of Unity Terrain tiles (default 2 x 4 tiles = 20 km x 32 km),
/// because one single terrain cannot stay detailed over such a huge area.
///
/// What it creates (all inside Assets/DeepOcean, nothing else is touched):
///   - Environment/OceanTerrain/OceanTerrain_Tile_X_Z.asset   (height data per tile)
///   - Materials/Terrain/*.png, *.terrainlayer, OceanTerrain_HDRP.mat
///   - Scene objects: "Ocean_Seabed" (parent with the terrain tiles) and "Submarine_SpawnPoint"
///
/// The journey runs along +Z (forward from the spawn point):
///   0 - 3 km     sunlit continental shelf        (Epipelagic, down to ~200 m)
///   3 - 6 km     continental slope, big cliffs   (Mesopelagic, to ~1,200 m)
///   6 - 12 km    bathyal ridges and hills        (Bathypelagic, to ~4,300 m)
///   12 - 17 km   abyssal plain with seamounts    (Abyssopelagic, ~5,800 m)
///   17 - 29 km   THE MARIANA TRENCH, ~12 km wide (Hadal, Challenger Deep 10,994 m)
///   29 - 32 km   abyssal plain on the far side
/// </summary>
public class SeabedGeneratorWindow : EditorWindow
{
    // ---------- Output locations (only new DeepOcean folders) ----------
    const string TerrainFolder  = "Assets/DeepOcean/Environment/OceanTerrain";
    const string MaterialFolder = "Assets/DeepOcean/Materials/Terrain";
    const string SettingsFolder = "Assets/DeepOcean/Settings";
    const string SeabedName     = "Ocean_Seabed";
    const string TilePrefix     = "Seabed_Tile_";
    const string SpawnName      = "Submarine_SpawnPoint";

    // ---------- Defaults ----------
    const float DefaultWidth  = 20000f;
    const float DefaultLength = 32000f;
    const int   DefaultTilesX = 2;
    const int   DefaultTilesZ = 4;

    // ---------- Settings shown in the window ----------
    OceanScaleSettings scaleSettings;
    float mapWidthX  = DefaultWidth;   // Unity units (= metres), left-right
    float mapLengthZ = DefaultLength;  // Unity units (= metres), along the journey
    int   tilesX = DefaultTilesX;
    int   tilesZ = DefaultTilesZ;
    int   resolutionChoice = 1;        // index into ResolutionValues
    int   seed = 360;
    float spawnDepthMetres = 30f;

    static readonly int[] ResolutionValues = { 1025, 2049, 4097 };
    static readonly string[] ResolutionLabels = { "1025 per tile (fast preview)", "2049 per tile (recommended)", "4097 per tile (very slow, huge files)" };

    // All horizontal layout numbers below are designed for a 32,000-unit-long map.
    // If you change "Map length Z", they are stretched/squashed to fit.
    const float DesignLength = 32000f;

    /// <summary>
    /// The seabed DEPTH PROFILE from the start of the map up to the trench.
    /// x = distance along Z from the start of the terrain (units, for a 32,000 long map)
    /// y = seabed depth in METRES at that distance.
    /// </summary>
    static readonly Vector2[] DepthProfile =
    {
        new Vector2(    0,   25), // shallow back edge, close to the surface
        new Vector2(  800,   80), // spawn area
        new Vector2( 3000,  180), // edge of the continental shelf (Epipelagic floor)
        new Vector2( 6000, 1200), // continental slope: big cliffs (Mesopelagic)
        new Vector2(11000, 3900), // bathyal ridges and hills (Bathypelagic)
        new Vector2(12000, 4300),
        new Vector2(17000, 5800), // abyssal plain with seamounts (Abyssopelagic)
        new Vector2(32000, 5900), // the abyssal plain continues on the far side of the trench
    };

    // ---------- MARIANA TRENCH (depths in metres, distances in units for a 32,000 map) ----------
    const float TrenchAxisZ      = 23000f; // where the trench centre line crosses the middle of the map
    const float TrenchCurve      = 1800f;  // crescent shape: the ends bend this much towards the start
    const float TrenchFloorHalf  = 300f;   // the flat sediment floor is about 600 m wide
    const float InnerWallWidth   = 5000f;  // near (player) side: steeper wall, like the Mariana's arc side
    const float OuterWallWidth   = 6500f;  // far (Pacific plate) side: gentler wall
    const float TrenchRimDepth   = 5900f;  // depth at the top of the trench walls
    const float TrenchFloorDepth = 10650f; // normal trench floor depth
    const float ChallengerDeepAt = 0.62f;  // position across the map (0 = left edge, 1 = right edge)
    const float SirenaDeepAt     = 0.30f;  // the second deep pocket (real Sirena Deep is ~10,809 m)
    const float SirenaDeepDepth  = 10809f;

    // ---------- Noise state prepared once per generation ----------
    float ox, oz;
    Vector4[] seamounts;   // x, z, height(m), radius(units)

    [MenuItem("Tools/Deep Ocean/Seabed Generator")]
    static void Open()
    {
        var window = GetWindow<SeabedGeneratorWindow>("Seabed Generator");
        window.minSize = new Vector2(400, 420);
    }

    void OnEnable()
    {
        // Automatically find an existing scale settings asset, if there is one.
        if (scaleSettings == null)
        {
            string[] guids = AssetDatabase.FindAssets("t:OceanScaleSettings");
            if (guids.Length > 0)
                scaleSettings = AssetDatabase.LoadAssetAtPath<OceanScaleSettings>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Deep Ocean Seabed Generator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Builds a 20 km x 32 km seabed from the sunlit shelf down to a 12 km wide Mariana Trench " +
                                "(Challenger Deep, 10,994 m). The journey runs forward along +Z from the spawn point.", MessageType.Info);

        scaleSettings = (OceanScaleSettings)EditorGUILayout.ObjectField("Ocean Scale Settings", scaleSettings, typeof(OceanScaleSettings), false);
        if (scaleSettings == null)
        {
            EditorGUILayout.HelpBox("No Ocean Scale Settings asset yet. Click the button below to create one.", MessageType.Warning);
            if (GUILayout.Button("Create Ocean Scale Settings asset"))
                scaleSettings = CreateScaleSettingsAsset();
            return;
        }

        EditorGUILayout.LabelField($"Scale: 1 unit = {scaleSettings.metresPerUnit} m of depth.  Challenger Deep Y ≈ {scaleSettings.DeepestWorldY:0}");

        bool isMariana = Mathf.Approximately(scaleSettings.metresPerUnit, 1f) && Mathf.Approximately(scaleSettings.maxDepthMetres, 10994f);
        if (!isMariana)
        {
            EditorGUILayout.HelpBox("Not at true Mariana Trench scale yet.", MessageType.Warning);
            if (GUILayout.Button("Apply Mariana Trench scale (1 unit = 1 m, 10,994 m)"))
            {
                Undo.RecordObject(scaleSettings, "Mariana scale");
                scaleSettings.metresPerUnit = 1f;
                scaleSettings.maxDepthMetres = 10994f;
                EditorUtility.SetDirty(scaleSettings);
                AssetDatabase.SaveAssets();
            }
        }

        EditorGUILayout.Space();
        mapWidthX  = EditorGUILayout.FloatField("Map width X (units)", mapWidthX);
        mapLengthZ = EditorGUILayout.FloatField("Map length Z (units)", mapLengthZ);
        tilesX = Mathf.Clamp(EditorGUILayout.IntField("Terrain tiles across X", tilesX), 1, 8);
        tilesZ = Mathf.Clamp(EditorGUILayout.IntField("Terrain tiles along Z", tilesZ), 1, 8);
        resolutionChoice = EditorGUILayout.Popup("Heightmap resolution", resolutionChoice, ResolutionLabels);
        seed = EditorGUILayout.IntField("Random seed", seed);
        spawnDepthMetres = EditorGUILayout.FloatField("Spawn depth (m)", spawnDepthMetres);

        float spacingX = mapWidthX / tilesX / (ResolutionValues[resolutionChoice] - 1);
        float spacingZ = mapLengthZ / tilesZ / (ResolutionValues[resolutionChoice] - 1);
        EditorGUILayout.LabelField($"Detail: one height sample every {spacingX:0.0} m x {spacingZ:0.0} m");

        if (GUILayout.Button("Reset to defaults (20 km x 32 km, 2 x 4 tiles, 2049)"))
        {
            mapWidthX = DefaultWidth;
            mapLengthZ = DefaultLength;
            tilesX = DefaultTilesX;
            tilesZ = DefaultTilesZ;
            resolutionChoice = 1;
            seed = 360;
            spawnDepthMetres = 30f;
            GUI.FocusControl(null);   // so the number boxes show the new values straight away
        }

        if (mapLengthZ < 24000f)
            EditorGUILayout.HelpBox("The map is short for a Mariana-scale trench: everything will be squeezed and the dive will feel short. " +
                                    "Recommended: 20000 x 32000.", MessageType.Warning);

        EditorGUILayout.Space();
        if (GUILayout.Button("Generate Seabed", GUILayout.Height(34)))
            Generate();
    }

    // =====================================================================================
    // MAIN GENERATION
    // =====================================================================================
    void Generate()
    {
        // Replace an earlier generated seabed (only OUR object: named Ocean_Seabed and holding terrains).
        GameObject existing = GameObject.Find(SeabedName);
        if (existing != null && existing.GetComponentInChildren<Terrain>(true) != null)
        {
            if (!EditorUtility.DisplayDialog("Replace seabed?",
                    $"'{SeabedName}' already exists in the scene. Replace it with a newly generated one?", "Replace", "Cancel"))
                return;
            Undo.DestroyObjectImmediate(existing);
        }

        EnsureFolder(TerrainFolder);
        EnsureFolder(MaterialFolder);
        DeleteOldTerrainData();

        int res = ResolutionValues[resolutionChoice];
        float bottomY = scaleSettings.DeepestWorldY - 20f;   // a little margin under the deepest point
        float topY = scaleSettings.surfaceY;                 // terrain may never reach above the surface
        float heightRange = topY - bottomY;
        float tileW = mapWidthX / tilesX;
        float tileL = mapLengthZ / tilesZ;
        Vector3 origin = new Vector3(-mapWidthX * 0.5f, bottomY, -800f * (mapLengthZ / DesignLength));

        try
        {
            PrepareNoise();

            EditorUtility.DisplayProgressBar("Seabed Generator", "Creating seabed textures...", 0f);
            TerrainLayer[] layers = CreateTerrainLayers();
            Material terrainMaterial = GetOrCreateTerrainMaterial();

            var root = new GameObject(SeabedName);
            root.transform.position = Vector3.zero;
            Undo.RegisterCreatedObjectUndo(root, "Generate Ocean Seabed");

            var terrains = new Terrain[tilesX, tilesZ];
            int tileCount = tilesX * tilesZ, done = 0;

            for (int tz = 0; tz < tilesZ; tz++)
            for (int tx = 0; tx < tilesX; tx++)
            {
                float progressStart = done / (float)tileCount, progressSize = 1f / tileCount;
                string label = $"Tile {done + 1} of {tileCount}";

                // World-space (relative to origin) corner of this tile.
                float startX = tx * tileW, startZ = tz * tileL;

                // ---------- Heights ----------
                float[,] heights = BuildTileHeights(res, startX, startZ, tileW, tileL, bottomY, heightRange,
                                                    label, progressStart, progressSize * 0.8f);

                // ---------- Terrain data asset ----------
                var data = new TerrainData();
                data.heightmapResolution = res;                   // set resolution BEFORE size
                data.size = new Vector3(tileW, heightRange, tileL);
                data.SetHeights(0, 0, heights);
                data.terrainLayers = layers;
                AssetDatabase.CreateAsset(data, $"{TerrainFolder}/OceanTerrain_Tile_{tx}_{tz}.asset");

                // ---------- Painting ----------
                EditorUtility.DisplayProgressBar("Seabed Generator", label + ": painting sand, rock and sediment...", progressStart + progressSize * 0.85f);
                PaintSplatmap(data, heights, res, tileW, tileL, bottomY, heightRange);
                EditorUtility.SetDirty(data);

                // ---------- Scene object ----------
                GameObject tileGO = Terrain.CreateTerrainGameObject(data);
                tileGO.name = $"{TilePrefix}{tx}_{tz}";
                tileGO.transform.SetParent(root.transform, false);
                tileGO.transform.position = origin + new Vector3(startX, 0f, startZ);

                Terrain terrain = tileGO.GetComponent<Terrain>();
                terrain.materialTemplate = terrainMaterial;
                terrain.drawInstanced = true;          // better GPU performance
                terrain.heightmapPixelError = 4f;      // detail level (lower = more detail, more cost)
                terrain.basemapDistance = 3000f;
                terrain.groupingID = 360;              // tiles in the same group connect seamlessly
                terrain.allowAutoConnect = true;
                terrains[tx, tz] = terrain;
                done++;
            }

            // Tell each tile who its neighbours are (left, top(+Z), right, bottom(-Z)).
            for (int tz = 0; tz < tilesZ; tz++)
            for (int tx = 0; tx < tilesX; tx++)
            {
                terrains[tx, tz].SetNeighbors(
                    tx > 0 ? terrains[tx - 1, tz] : null,
                    tz < tilesZ - 1 ? terrains[tx, tz + 1] : null,
                    tx < tilesX - 1 ? terrains[tx + 1, tz] : null,
                    tz > 0 ? terrains[tx, tz - 1] : null);
            }

            CreateOrMoveSpawnPoint();

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(root.scene);
            Selection.activeGameObject = root;
            Debug.Log($"[Seabed Generator] Done. {tilesX}x{tilesZ} tiles, seabed from Y {bottomY:0} to {topY:0}, " +
                      $"size {mapWidthX} x {mapLengthZ} units, 1 unit = {scaleSettings.metresPerUnit} m depth.");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>Deletes ONLY terrain data files this tool created earlier.</summary>
    static void DeleteOldTerrainData()
    {
        var toDelete = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:TerrainData", new[] { TerrainFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string file = Path.GetFileName(path);
            if (file.StartsWith("OceanTerrain")) toDelete.Add(path);
        }
        var failed = new List<string>();
        if (toDelete.Count > 0) AssetDatabase.DeleteAssets(toDelete.ToArray(), failed);
    }

    // =====================================================================================
    // HEIGHTS
    // =====================================================================================
    void PrepareNoise()
    {
        var rng = new System.Random(seed);
        ox = (float)rng.NextDouble() * 1000f;   // random noise offsets from the seed
        oz = (float)rng.NextDouble() * 1000f;

        float ls = mapLengthZ / DesignLength;
        // Seamounts (underwater mountains): six on the abyssal plain before the trench, three beyond it.
        seamounts = new Vector4[9];
        for (int i = 0; i < seamounts.Length; i++)
        {
            bool farSide = i >= 6;
            seamounts[i] = new Vector4(
                Mathf.Lerp(1200f, mapWidthX - 1200f, (float)rng.NextDouble()),
                (farSide ? Mathf.Lerp(30200f, 31500f, (float)rng.NextDouble())
                         : Mathf.Lerp(8500f, 16000f, (float)rng.NextDouble())) * ls,
                Mathf.Lerp(900f, 2800f, (float)rng.NextDouble()),
                Mathf.Lerp(300f, 800f, (float)rng.NextDouble()));
        }
    }

    float[,] BuildTileHeights(int res, float startX, float startZ, float tileW, float tileL,
                              float bottomY, float heightRange, string label, float progressStart, float progressSize)
    {
        var heights = new float[res, res];
        for (int zi = 0; zi < res; zi++)
        {
            if (zi % 64 == 0)
                EditorUtility.DisplayProgressBar("Seabed Generator", label + ": shaping the ocean floor and the Mariana Trench...",
                                                 progressStart + progressSize * zi / res);

            float z = startZ + zi / (float)(res - 1) * tileL;
            for (int xi = 0; xi < res; xi++)
            {
                float x = startX + xi / (float)(res - 1) * tileW;
                float depth = SampleDepth(x, z);

                // Convert depth (m) -> world Y -> 0..1 terrain height.
                float worldY = scaleSettings.DepthMetresToWorldY(depth);
                heights[zi, xi] = Mathf.Clamp01((worldY - bottomY) / heightRange);
            }
        }
        return heights;
    }

    /// <summary>
    /// Seabed depth in metres at map position (x, z). (0,0) = the back-left corner of the whole map.
    /// Because every tile asks this same function, the tiles match up perfectly at their edges.
    /// </summary>
    float SampleDepth(float x, float z)
    {
        float ls = mapLengthZ / DesignLength;     // stretches the layout if you change the map length
        float maxDepth = scaleSettings.maxDepthMetres;

        // ================= PART A: the ocean floor leading up to the trench =================

        // Bend the zone borders so they are not perfectly straight lines.
        float warp = (Mathf.PerlinNoise(x * 0.0002f + ox, 3.7f) - 0.5f) * 2f * 1000f;
        float pz = Mathf.Clamp((z + warp) / ls, 0f, DesignLength);

        // 1) Base depth from the profile.
        float depth = ProfileDepth(pz);

        // 2) Rolling hills that grow with depth (gentle near the surface, dramatic deeper down).
        float rolling = Fbm(x * 0.0008f + ox, z * 0.0008f + oz, 6) - 0.5f;
        depth += rolling * 2f * (6f + depth * 0.04f);

        // 3) Sharp ridges and rock formations below ~500 m.
        float ridgeAmount = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(500f, 3000f, depth));
        float ridges = RidgedFbm(x * 0.0012f + oz, z * 0.0012f + ox, 5);
        depth -= ridges * ridgeAmount * depth * 0.08f;

        // 4) Rocky outcrops on the shallow shelf.
        float shelf = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(150f, 400f, depth));
        float outcrop = Mathf.Max(0f, RidgedFbm(x * 0.012f + ox, z * 0.012f + oz, 3) - 0.55f) / 0.45f;
        depth -= outcrop * outcrop * 30f * shelf;

        // 5) Seamounts.
        for (int m = 0; m < seamounts.Length; m++)
        {
            float dx = x - seamounts[m].x, dz = z - seamounts[m].y;
            float r = seamounts[m].w;
            float distSq = dx * dx + dz * dz;
            if (distSq > 9f * r * r) continue;
            depth -= seamounts[m].z * Mathf.Exp(-distSq / (2f * r * r));
        }

        // 6) Cliff terraces on the continental slope.
        float cliffMask = Band(pz, 3000f, 6000f, 300f);
        if (cliffMask > 0f)
            depth = Mathf.Lerp(depth, Terrace(depth, 120f), cliffMask);

        // 7) Raise the far left/right edges so the world feels enclosed (fog hides the edge).
        //    The trench below is NOT raised, so it keeps going past the edge like a real trench.
        float edgeDist = Mathf.Min(x, mapWidthX - x);
        float edge = 1f - Mathf.SmoothStep(0f, 1f, edgeDist / 1400f);
        depth = Mathf.Lerp(depth, depth * 0.6f, edge);

        // ================= PART B: the MARIANA TRENCH =================
        // A crescent-shaped, V-shaped canyon about 12 km wide, crossing the whole map from left to right.

        float u = (x - mapWidthX * 0.5f) / (mapWidthX * 0.5f);                     // -1 (left) .. +1 (right)
        float axisZ = (TrenchAxisZ - TrenchCurve * u * u) * ls                      // crescent
                    + (Mathf.PerlinNoise(x * 0.0003f + oz, 9.1f) - 0.5f) * 800f * ls; // gentle wiggle
        float s = z - axisZ;                                                        // below 0 = near side, above 0 = far side
        float dist = Mathf.Abs(s);

        // Floor depth along the trench, with two deep pockets like the real Mariana Trench.
        float challengerX = mapWidthX * ChallengerDeepAt;
        float sirenaX = mapWidthX * SirenaDeepAt;
        float floorDepth = TrenchFloorDepth;
        floorDepth = Mathf.Max(floorDepth, Mathf.Lerp(TrenchFloorDepth, maxDepth,
                          Mathf.Exp(-Sq(x - challengerX) / (2f * Sq(1300f)))));       // Challenger Deep
        floorDepth = Mathf.Max(floorDepth, Mathf.Lerp(TrenchFloorDepth, SirenaDeepDepth,
                          Mathf.Exp(-Sq(x - sirenaX) / (2f * Sq(1000f)))));           // Sirena Deep

        float floorHalf = TrenchFloorHalf * ls;
        float wallWidth = (s < 0f ? InnerWallWidth : OuterWallWidth) * ls;
        float trenchDepth;
        if (dist <= floorHalf)
        {
            // Flat sediment floor. Noise only makes it SHALLOWER, so Challenger Deep stays the deepest point.
            trenchDepth = floorDepth - Fbm(x * 0.005f + ox, z * 0.005f + oz, 3) * 40f;
        }
        else
        {
            float t = Mathf.Clamp01((dist - floorHalf) / wallWidth);   // 0 at the floor, 1 at the rim
            // Steeper near the bottom, easing out at the rim (like a real V-shaped trench).
            float shape = 1f - Mathf.Pow(1f - t, 1.6f);
            trenchDepth = Mathf.Lerp(floorDepth, TrenchRimDepth, shape);
            float wallMask = t < 1f ? Mathf.Sin(t * Mathf.PI) : 0f;  // strongest half-way up the wall

            // Fault scarps and gullies running ALONG the trench walls.
            float gullies = RidgedFbm(x * 0.0008f + ox, s * 0.004f + oz, 5);
            trenchDepth -= gullies * 450f * wallMask;
            // Big stepped cliffs (terraces) on the walls.
            trenchDepth = Mathf.Lerp(trenchDepth, Terrace(trenchDepth, 420f), 0.75f * wallMask);
        }

        // Outer rise: the Pacific plate bulges up a little before it bends down into the trench.
        if (s > 0f)
        {
            float riseCentre = floorHalf + wallWidth + 2500f * ls;
            depth -= 350f * Mathf.Exp(-Sq(s - riseCentre) / (2f * Sq(1500f * ls)));
        }

        // The deeper of the two wins: this carves the trench into the ocean floor.
        depth = Mathf.Max(depth, trenchDepth);

        // Keep inside the allowed range: never above the surface, never deeper than Challenger Deep.
        return Mathf.Clamp(depth, 10f, maxDepth);
    }

    static float Sq(float v) => v * v;

    /// <summary>Smoothly interpolates the depth profile at distance pz.</summary>
    static float ProfileDepth(float pz)
    {
        if (pz <= DepthProfile[0].x) return DepthProfile[0].y;
        for (int i = 0; i < DepthProfile.Length - 1; i++)
        {
            Vector2 a = DepthProfile[i], b = DepthProfile[i + 1];
            if (pz <= b.x)
            {
                float t = (pz - a.x) / (b.x - a.x);
                return Mathf.Lerp(a.y, b.y, Mathf.SmoothStep(0f, 1f, t));
            }
        }
        return DepthProfile[DepthProfile.Length - 1].y;
    }

    /// <summary>1 inside [start,end], fading to 0 over 'soft' units outside it.</summary>
    static float Band(float v, float start, float end, float soft)
    {
        return Mathf.Clamp01((v - (start - soft)) / soft) * Mathf.Clamp01(((end + soft) - v) / soft);
    }

    /// <summary>Turns a smooth slope into flat ledges with steep cliff faces.</summary>
    static float Terrace(float depth, float step)
    {
        float t = depth / step;
        float f = Mathf.Floor(t);
        float frac = t - f;
        frac = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((frac - 0.3f) / 0.4f));
        return (f + frac) * step;
    }

    static float Fbm(float x, float y, int octaves)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += Mathf.PerlinNoise(x, y) * amp;
            norm += amp;
            x *= 2.03f; y *= 2.03f; amp *= 0.5f;
        }
        return sum / norm;
    }

    static float RidgedFbm(float x, float y, int octaves)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            float n = 1f - Mathf.Abs(Mathf.PerlinNoise(x, y) * 2f - 1f);
            sum += n * n * amp;
            norm += amp;
            x *= 2.1f; y *= 2.1f; amp *= 0.5f;
        }
        return sum / norm;
    }

    // =====================================================================================
    // PAINTING (which texture goes where)
    // =====================================================================================
    void PaintSplatmap(TerrainData data, float[,] heights, int res, float tileW, float tileL, float bottomY, float heightRange)
    {
        int aRes = 1024;   // painting detail per tile
        data.alphamapResolution = aRes;
        var map = new float[aRes, aRes, 4];

        float cellX = tileW / (res - 1);
        float cellZ = tileL / (res - 1);

        for (int ay = 0; ay < aRes; ay++)
        {
            int zi = Mathf.Clamp(Mathf.RoundToInt(ay / (float)(aRes - 1) * (res - 1)), 1, res - 2);
            for (int ax = 0; ax < aRes; ax++)
            {
                int xi = Mathf.Clamp(Mathf.RoundToInt(ax / (float)(aRes - 1) * (res - 1)), 1, res - 2);

                float worldY = bottomY + heights[zi, xi] * heightRange;
                float depth = scaleSettings.WorldYToDepthMetres(worldY);

                // Steepness in degrees, measured in world units (what you actually see).
                float gx = (heights[zi, xi + 1] - heights[zi, xi - 1]) * heightRange / (2f * cellX);
                float gz = (heights[zi + 1, xi] - heights[zi - 1, xi]) * heightRange / (2f * cellZ);
                float angle = Mathf.Atan(Mathf.Sqrt(gx * gx + gz * gz)) * Mathf.Rad2Deg;

                float wobble = (Mathf.PerlinNoise(ax * 0.02f + ox, ay * 0.02f + oz) - 0.5f) * 400f;
                float deep  = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(200f, 1500f, depth + wobble));
                float steep = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(22f, 40f, angle));

                map[ay, ax, 0] = (1f - deep) * (1f - steep); // Sand      - shallow & flat
                map[ay, ax, 1] = (1f - deep) * steep;        // Rock      - shallow & steep
                map[ay, ax, 2] = deep * (1f - steep);        // Sediment  - deep & flat
                map[ay, ax, 3] = deep * steep;               // Basalt    - deep & steep
            }
        }
        data.SetAlphamaps(0, 0, map);
    }

    // =====================================================================================
    // TEXTURES, LAYERS AND MATERIAL
    // =====================================================================================
    TerrainLayer[] CreateTerrainLayers()
    {
        return new[]
        {
            CreateLayer("Seabed_Sand",     new Color(0.74f, 0.68f, 0.54f), new Color(0.58f, 0.52f, 0.40f), 6f,  0.25f, ripples: true,  cracks: false, seedOffset: 1),
            CreateLayer("Seabed_Rock",     new Color(0.44f, 0.42f, 0.38f), new Color(0.26f, 0.25f, 0.24f), 14f, 0.30f, ripples: false, cracks: true,  seedOffset: 2),
            CreateLayer("Seabed_Sediment", new Color(0.31f, 0.32f, 0.31f), new Color(0.21f, 0.22f, 0.22f), 10f, 0.15f, ripples: false, cracks: false, seedOffset: 3),
            CreateLayer("Seabed_Basalt",   new Color(0.13f, 0.13f, 0.14f), new Color(0.05f, 0.05f, 0.06f), 18f, 0.35f, ripples: false, cracks: true,  seedOffset: 4),
        };
    }

    TerrainLayer CreateLayer(string name, Color light, Color dark, float tileSize, float smoothness,
                             bool ripples, bool cracks, int seedOffset)
    {
        const int size = 512;
        var heightField = new float[size * size];
        var albedo = new Color32[size * size];

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = x / (float)size, v = y / (float)size;
            float h = TileFbm(u, v, 8, 5, seed + seedOffset);
            if (ripples)   // sand ripples (whole number of waves = still tiles seamlessly)
                h = h * 0.6f + 0.4f * (0.5f + 0.5f * Mathf.Sin((u + 0.08f * TileFbm(u, v, 4, 2, seedOffset)) * Mathf.PI * 2f * 10f));
            if (cracks)    // ridged cracks for rock
                h = h * 0.55f + 0.45f * (1f - Mathf.Abs(TileFbm(u, v, 6, 4, seed + seedOffset * 7) * 2f - 1f));

            heightField[y * size + x] = h;
            float speckle = TileValueNoise(u * 128f, v * 128f, 128, seed + seedOffset * 13) * 0.12f;
            albedo[y * size + x] = Color.Lerp(dark, light, Mathf.Clamp01(h + speckle - 0.06f));
        }

        // Normal map from the height field (wrap-around so it tiles).
        var normals = new Color32[size * size];
        const float strength = 6f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float hl = heightField[y * size + (x + size - 1) % size];
            float hr = heightField[y * size + (x + 1) % size];
            float hd = heightField[((y + size - 1) % size) * size + x];
            float hu = heightField[((y + 1) % size) * size + x];
            Vector3 n = new Vector3((hl - hr) * strength, (hd - hu) * strength, 1f).normalized;
            normals[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
        }

        Texture2D albedoTex = SavePng(MaterialFolder + "/" + name + "_Albedo.png", albedo, size, isNormal: false);
        Texture2D normalTex = SavePng(MaterialFolder + "/" + name + "_Normal.png", normals, size, isNormal: true);

        string layerPath = MaterialFolder + "/" + name + ".terrainlayer";
        TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
        bool isNew = layer == null;
        if (isNew) layer = new TerrainLayer();

        layer.diffuseTexture = albedoTex;
        layer.normalMapTexture = normalTex;
        layer.normalScale = 1f;
        layer.tileSize = new Vector2(tileSize, tileSize);
        layer.smoothness = smoothness;
        layer.metallic = 0f;

        if (isNew) AssetDatabase.CreateAsset(layer, layerPath);
        else EditorUtility.SetDirty(layer);
        return layer;
    }

    static Texture2D SavePng(string assetPath, Color32[] pixels, int size, bool isNormal)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, isNormal);
        tex.SetPixels32(pixels);
        tex.Apply();
        File.WriteAllBytes(assetPath, tex.EncodeToPNG());
        DestroyImmediate(tex);

        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
        importer.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        importer.sRGBTexture = !isNormal;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.mipmapEnabled = true;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    static Material GetOrCreateTerrainMaterial()
    {
        string path = MaterialFolder + "/OceanTerrain_HDRP.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        Shader shader = Shader.Find("HDRP/TerrainLit");
        if (shader == null)
        {
            Debug.LogWarning("[Seabed Generator] HDRP/TerrainLit shader not found. Is HDRP the active pipeline? Using the default terrain material.");
            return null;
        }
        mat = new Material(shader) { name = "OceanTerrain_HDRP" };
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // Seamless (tileable) value noise: the lattice wraps every 'period' cells.
    static float TileValueNoise(float x, float y, int period, int s)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        float a = Hash(x0, y0, period, s), b = Hash(x0 + 1, y0, period, s);
        float c = Hash(x0, y0 + 1, period, s), d = Hash(x0 + 1, y0 + 1, period, s);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    static float TileFbm(float u, float v, int basePeriod, int octaves, int s)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        int period = basePeriod;
        for (int o = 0; o < octaves; o++)
        {
            sum += TileValueNoise(u * period, v * period, period, s + o * 31) * amp;
            norm += amp;
            period *= 2; amp *= 0.5f;
        }
        return sum / norm;
    }

    static float Hash(int x, int y, int period, int s)
    {
        x = ((x % period) + period) % period;
        y = ((y % period) + period) % period;
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + s * 1442695041;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0x00FFFFFF) / 16777215f;
        }
    }

    // =====================================================================================
    // HELPERS
    // =====================================================================================
    void CreateOrMoveSpawnPoint()
    {
        GameObject spawn = GameObject.Find(SpawnName);
        if (spawn == null)
        {
            spawn = new GameObject(SpawnName);
            Undo.RegisterCreatedObjectUndo(spawn, "Create Spawn Point");
        }
        else
        {
            Undo.RecordObject(spawn.transform, "Move Spawn Point");
        }
        // Facing +Z (towards the deep ocean), just under the waves.
        spawn.transform.SetPositionAndRotation(new Vector3(0f, scaleSettings.DepthMetresToWorldY(spawnDepthMetres), 0f), Quaternion.identity);
    }

    static OceanScaleSettings CreateScaleSettingsAsset()
    {
        EnsureFolder(SettingsFolder);
        var asset = CreateInstance<OceanScaleSettings>();
        string path = SettingsFolder + "/OceanScaleSettings.asset";
        AssetDatabase.CreateAsset(asset, AssetDatabase.GenerateUniqueAssetPath(path));
        AssetDatabase.SaveAssets();
        Selection.activeObject = asset;
        return asset;
    }

    /// <summary>Creates a folder path like Assets/A/B/C one level at a time if it does not exist.</summary>
    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
