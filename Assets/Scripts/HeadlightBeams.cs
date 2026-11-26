using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Makes the submarine's headlights VISIBLE as beams of light in the water, plus a glow on each lamp.
///
/// Why: in HDRP a spotlight only shows where it hits something (seabed, creatures). In open water
/// there is nothing to hit, so you see no light at all, and HDRP's underwater fog cannot show light
/// shafts. This adds a soft additive cone (Custom/HeadlightBeam shader) in front of every light whose
/// name contains "Headlight". The beams switch on/off together with the lights (F key in
/// SubmarineLightAndSonar), so no other script needs to know about them.
///
/// Put it on the submarine root (Orientation Cube).
/// </summary>
public class HeadlightBeams : MonoBehaviour
{
    [Tooltip("Lights whose name contains this text get a beam.")]
    public string lightNameContains = "Headlight";
    [Tooltip("Material using Custom/HeadlightBeamVolume for the cones. Left empty = one is made at runtime.")]
    public Material beamMaterial;
    [Tooltip("Material using Custom/HeadlightBeam (Fade Along Length = 0) for the lamp glow. Left empty = made at runtime.")]
    public Material glowMaterial;

    [Header("Beam shape")]
    [Tooltip("How far the visible beam reaches (m). The real light reaches further (its Range).")]
    public float beamLength = 30f;
    [Tooltip("Beam width as a fraction of the spotlight's outer cone.")]
    [Range(0.1f, 1f)] public float widthOfCone = 0.7f;
    [Tooltip("Radius of the beam where it leaves the lamp (m).")]
    public float startRadius = 0.18f;
    [Tooltip("Radius of the glow ball on each lamp (m).")]
    public float glowRadius = 0.35f;
    [Range(8, 64)] public int segments = 32;

    readonly List<Light> lights = new List<Light>();
    readonly List<GameObject> visuals = new List<GameObject>();
    readonly List<int> visualLight = new List<int>();
    Mesh coneMesh, sphereMesh;

    void Start()
    {
        foreach (Light l in GetComponentsInChildren<Light>(true))
            if (l.type == LightType.Spot && l.name.Contains(lightNameContains)) lights.Add(l);
        if (lights.Count == 0) { Debug.LogWarning("[HeadlightBeams] No spot lights named '" + lightNameContains + "' found."); enabled = false; return; }

        Shader sh = Shader.Find("Custom/HeadlightBeam");
        Shader volSh = Shader.Find("Custom/HeadlightBeamVolume");
        if (beamMaterial == null && volSh != null) beamMaterial = new Material(volSh);
        if (glowMaterial == null && sh != null)
        {
            glowMaterial = new Material(sh);
            glowMaterial.SetFloat("_UseLength", 0f);
            glowMaterial.SetFloat("_EdgePower", 3f);
            glowMaterial.SetFloat("_Intensity", 900f);
            glowMaterial.SetFloat("_CameraFade", 0.5f);
        }
        if (beamMaterial == null) { Debug.LogError("[HeadlightBeams] Shader Custom/HeadlightBeam not found."); enabled = false; return; }

        GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);

        for (int i = 0; i < lights.Count; i++)
        {
            Light l = lights[i];
            float halfAngle = l.spotAngle * 0.5f * widthOfCone * Mathf.Deg2Rad;
            float endRadius = startRadius + Mathf.Tan(halfAngle) * beamLength;
            Mesh cone = BuildCone(startRadius, endRadius, beamLength, segments);

            GameObject beam = new GameObject(l.name + "_Beam");
            beam.transform.SetParent(l.transform, false);
            beam.AddComponent<MeshFilter>().sharedMesh = cone;
            MeshRenderer br = beam.AddComponent<MeshRenderer>();
            br.sharedMaterial = beamMaterial;
            br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            br.receiveShadows = false;
            // Tell the ray-marching shader the exact cone shape (object space = the light's space)
            var mpb = new MaterialPropertyBlock();
            mpb.SetFloat("_ConeR0", startRadius);
            mpb.SetFloat("_ConeSlope", (endRadius - startRadius) / beamLength);
            mpb.SetFloat("_ConeLength", beamLength);
            br.SetPropertyBlock(mpb);
            visuals.Add(beam); visualLight.Add(i);

            if (glowMaterial != null)
            {
                GameObject glow = new GameObject(l.name + "_Glow");
                glow.transform.SetParent(l.transform, false);
                glow.transform.localScale = Vector3.one * glowRadius * 2f;
                glow.AddComponent<MeshFilter>().sharedMesh = sphereMesh;
                MeshRenderer gr = glow.AddComponent<MeshRenderer>();
                gr.sharedMaterial = glowMaterial;
                gr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                gr.receiveShadows = false;
                visuals.Add(glow); visualLight.Add(i);
            }
        }
    }

    void LateUpdate()
    {
        // Follow the real light: off when the headlights are switched off (sonar mode)
        for (int i = 0; i < visuals.Count; i++)
        {
            Light l = lights[visualLight[i]];
            bool on = l != null && l.isActiveAndEnabled && l.intensity > 0f;
            if (visuals[i].activeSelf != on) visuals[i].SetActive(on);
        }
    }

    /// <summary>Closed cone along +Z from radius r0 at z=0 to r1 at z=length (side + both end caps).
    /// All faces point outward and the shader culls front faces, so each pixel is drawn once, at the
    /// point where the view ray LEAVES the cone (looking from behind, that is the far cap).</summary>
    static Mesh BuildCone(float r0, float r1, float length, int seg)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var tris = new List<int>();
        float slope = (r1 - r0) / length;
        for (int i = 0; i <= seg; i++)
        {
            float a = (float)i / seg * Mathf.PI * 2f;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            Vector3 n = new Vector3(c, s, -slope).normalized;
            verts.Add(new Vector3(c * r0, s * r0, 0f)); norms.Add(n);
            verts.Add(new Vector3(c * r1, s * r1, length)); norms.Add(n);
        }
        for (int i = 0; i < seg; i++)
        {
            int v = i * 2;
            tris.Add(v); tris.Add(v + 2); tris.Add(v + 1);      // outward-facing
            tris.Add(v + 2); tris.Add(v + 3); tris.Add(v + 1);
        }
        // caps, also outward-facing (the shader draws only the inside/back faces = where the view ray leaves)
        int c0 = verts.Count; verts.Add(Vector3.zero); norms.Add(Vector3.back);
        int c1 = verts.Count; verts.Add(new Vector3(0f, 0f, length)); norms.Add(Vector3.forward);
        for (int i = 0; i < seg; i++)
        {
            int v = i * 2;
            tris.Add(c0); tris.Add(v + 2); tris.Add(v);
            tris.Add(c1); tris.Add(v + 1); tris.Add(v + 3);
        }
        var m = new Mesh { name = "HeadlightBeamCone" };
        m.SetVertices(verts); m.SetNormals(norms); m.SetTriangles(tris, 0);
        m.bounds = new Bounds(new Vector3(0, 0, length * 0.5f), new Vector3(r1 * 2f, r1 * 2f, length));
        return m;
    }
}
