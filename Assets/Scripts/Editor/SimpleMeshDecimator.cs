using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Very small "vertex clustering" mesh reducer for static, very high-poly models (scanned creatures).
/// It snaps vertices to a 3D grid and merges the ones that land in the same cell (and have similar UVs),
/// then throws away triangles that collapsed. The original .glb is never touched: the result is saved as a
/// new mesh asset and swapped onto the selected scene object's MeshFilter.
///
/// Use: select a scene object with a MeshFilter, then Tools > Shark Sim > Reduce Selected Mesh (to ~N verts).
/// Good enough for small creatures seen in the dark from a submarine; not for hero models.
/// </summary>
public static class SimpleMeshDecimator
{
    const string OutFolder = "Assets/Creatures/_Reduced";

    [MenuItem("Tools/Shark Sim/Reduce Selected Mesh (~25k verts)")]
    static void ReduceSelected25k() { ReduceSelection(25000); }

    [MenuItem("Tools/Shark Sim/Reduce Selected Mesh (~10k verts)")]
    static void ReduceSelected10k() { ReduceSelection(10000); }

    static void ReduceSelection(int targetVerts)
    {
        foreach (var go in Selection.gameObjects)
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                ReduceMeshFilter(mf, targetVerts);
    }

    /// <summary>Reduces mf.sharedMesh to roughly targetVerts and assigns the new mesh. Returns the new vertex count.</summary>
    public static int ReduceMeshFilter(MeshFilter mf, int targetVerts)
    {
        Mesh src = mf.sharedMesh;
        if (src == null || src.vertexCount <= targetVerts) return src ? src.vertexCount : 0;
        if (!AssetDatabase.IsValidFolder(OutFolder)) AssetDatabase.CreateFolder("Assets/Creatures", "_Reduced");

        // Search the grid size that lands closest to the target vertex count.
        float diag = src.bounds.size.magnitude;
        float lo = diag / 2000f, hi = diag / 20f;
        Mesh best = null;
        for (int iter = 0; iter < 12; iter++)
        {
            float cell = Mathf.Sqrt(lo * hi);
            Mesh m = Cluster(src, cell);
            if (best != null) Object.DestroyImmediate(best);
            best = m;
            if (m.vertexCount > targetVerts * 1.15f) lo = cell;
            else if (m.vertexCount < targetVerts * 0.85f) hi = cell;
            else break;
        }

        string safe = src.name.Replace("/", "_").Replace(":", "_");
        string path = AssetDatabase.GenerateUniqueAssetPath($"{OutFolder}/{safe}_reduced.asset");
        best.name = safe + "_reduced";
        AssetDatabase.CreateAsset(best, path);
        AssetDatabase.SaveAssets();
        Undo.RecordObject(mf, "Reduce mesh");
        mf.sharedMesh = best;
        Debug.Log($"[SimpleMeshDecimator] {src.name}: {src.vertexCount} -> {best.vertexCount} verts, saved {path}");
        return best.vertexCount;
    }

    static Mesh Cluster(Mesh src, float cell)
    {
        Vector3[] v = src.vertices;
        Vector3[] n = src.normals;
        Vector2[] uv = src.uv;
        bool hasN = n != null && n.Length == v.Length;
        bool hasUV = uv != null && uv.Length == v.Length;

        var map = new int[v.Length];
        var keyToNew = new Dictionary<CellKey, int>(v.Length / 4);
        var sumP = new List<Vector3>(); var sumN = new List<Vector3>(); var firstUV = new List<Vector2>(); var count = new List<int>();
        Vector3 o = src.bounds.min;
        for (int i = 0; i < v.Length; i++)
        {
            var key = new CellKey
            {
                x = (int)((v[i].x - o.x) / cell), y = (int)((v[i].y - o.y) / cell), z = (int)((v[i].z - o.z) / cell),
                u = hasUV ? Mathf.FloorToInt(uv[i].x * 32f) : 0, w = hasUV ? Mathf.FloorToInt(uv[i].y * 32f) : 0
            };
            int id;
            if (!keyToNew.TryGetValue(key, out id))
            {
                id = sumP.Count; keyToNew[key] = id;
                sumP.Add(Vector3.zero); sumN.Add(Vector3.zero); firstUV.Add(hasUV ? uv[i] : Vector2.zero); count.Add(0);
            }
            sumP[id] += v[i]; if (hasN) sumN[id] += n[i]; count[id]++;
            map[i] = id;
        }

        var nv = new Vector3[sumP.Count]; var nn = new Vector3[sumP.Count];
        for (int i = 0; i < nv.Length; i++) { nv[i] = sumP[i] / count[i]; nn[i] = sumN[i].normalized; }

        var mesh = new Mesh { indexFormat = nv.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
        mesh.vertices = nv;
        if (hasUV) mesh.uv = firstUV.ToArray();
        mesh.subMeshCount = src.subMeshCount;
        for (int s = 0; s < src.subMeshCount; s++)
        {
            int[] t = src.GetTriangles(s);
            var nt = new List<int>(t.Length / 2);
            var seen = new HashSet<long>();
            for (int k = 0; k < t.Length; k += 3)
            {
                int a = map[t[k]], b = map[t[k + 1]], c = map[t[k + 2]];
                if (a == b || b == c || a == c) continue;
                // drop exact duplicates
                int mn = Mathf.Min(a, Mathf.Min(b, c)), mx = Mathf.Max(a, Mathf.Max(b, c)), md = a + b + c - mn - mx;
                long h = ((long)mn * 73856093L) ^ ((long)md * 19349663L) ^ ((long)mx * 83492791L);
                if (!seen.Add(h)) continue;
                nt.Add(a); nt.Add(b); nt.Add(c);
            }
            mesh.SetTriangles(nt, s);
        }
        if (hasN) mesh.normals = nn; else mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    struct CellKey : System.IEquatable<CellKey>
    {
        public int x, y, z, u, w;
        public bool Equals(CellKey o) { return x == o.x && y == o.y && z == o.z && u == o.u && w == o.w; }
        public override bool Equals(object obj) { return obj is CellKey && Equals((CellKey)obj); }
        public override int GetHashCode() { unchecked { int h = x * 73856093 ^ y * 19349663 ^ z * 83492791; return h * 31 + u * 4099 + w; } }
    }
}
