using UnityEngine;

/// <summary>
/// "Marine snow": tiny drifting specks that fill real seawater. They only show up where light hits them
/// (your headlights), and they stay put in the water while the sub moves, which is what makes the
/// headlight beams feel like they are really out in the ocean instead of painted on the screen.
///
/// Put it on the submarine root (Orientation Cube). It creates its own world-space particle system.
/// </summary>
public class MarineSnow : MonoBehaviour
{
    [Tooltip("HDRP/Lit material for the specks (lit by the headlights).")]
    public Material snowMaterial;
    [Tooltip("Size of the box of specks around the sub (m).")]
    public float areaSize = 40f;
    public int maxParticles = 4000;
    public float particlesPerSecond = 350f;
    public Vector2 speckSize = new Vector2(0.015f, 0.05f);
    [Tooltip("How fast specks drift around (m/s).")]
    public float drift = 0.08f;

    ParticleSystem ps;
    Vector3 lastFillPos;

    void Start()
    {
        if (snowMaterial == null) { Debug.LogWarning("[MarineSnow] No material."); return; }
        var go = new GameObject("Marine Snow");
        go.transform.SetParent(transform, false);
        ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;   // specks stay in the water
        main.startLifetime = new ParticleSystem.MinMaxCurve(10f, 16f);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(speckSize.x, speckSize.y);
        main.maxParticles = maxParticles;
        main.prewarm = true;
        main.loop = true;
        main.playOnAwake = true;
        main.startColor = Color.white;

        var emission = ps.emission;
        emission.rateOverTime = particlesPerSecond;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = Vector3.one * areaSize;

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = drift;
        noise.frequency = 0.15f;
        noise.scrollSpeed = 0.05f;

        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;
        vel.y = new ParticleSystem.MinMaxCurve(-0.03f, 0.0f);   // slowly sinking, like real marine snow
        vel.x = new ParticleSystem.MinMaxCurve(-0.01f, 0.01f);
        vel.z = new ParticleSystem.MinMaxCurve(-0.01f, 0.01f);

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(1f, 0.85f), new GradientAlphaKey(0f, 1f) });
        col.color = g;

        var rend = ps.GetComponent<ParticleSystemRenderer>();
        rend.renderMode = ParticleSystemRenderMode.Billboard;
        rend.sharedMaterial = snowMaterial;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;
        rend.maxParticleSize = 0.05f;

        ps.Play();
        lastFillPos = transform.position;
    }

    void Update()
    {
        if (ps == null) return;
        // after a big jump (zone teleport keys) refill around the new position at once
        if ((transform.position - lastFillPos).sqrMagnitude > areaSize * areaSize * 0.25f)
        {
            ps.Clear(true);
            ps.Simulate(16f, true, true);
            ps.Play(true);
            lastFillPos = transform.position;
        }
        else if (Time.frameCount % 30 == 0)
            lastFillPos = Vector3.Lerp(lastFillPos, transform.position, 0.5f);
    }
}
