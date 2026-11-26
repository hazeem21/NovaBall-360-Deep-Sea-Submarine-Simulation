using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Simple ambient swimmer for depth zones.
/// Put this on an empty parent; the model goes in a child (the child carries any
/// scale and facing fix, so this object always swims along its own +Z).
/// Plays the model's built-in clip on loop (if one is assigned) and wanders
/// inside a cylinder: zoneCenter / zoneRadius horizontally, minY..maxY vertically.
/// </summary>
public class ZoneRoamer : MonoBehaviour
{
    [Header("Zone")]
    public Vector3 zoneCenter;
    public float zoneRadius = 40f;
    public float minY = -120f;
    public float maxY = -20f;

    [Header("Swimming")]
    public float speed = 1.5f;              // m/s
    public float speedVariation = 0.3f;     // fraction, slow drift in speed
    public float turnRate = 30f;            // deg/s
    public float maxPitch = 20f;            // deg
    public float retargetMin = 6f, retargetMax = 14f;
    public bool swim = true;                // false = only play the clip (static creatures)

    [Header("Built-in animation")]
    public AnimationClip clip;
    public float animSpeed = 1f;

    Vector3 target;
    float retargetAt, seed;
    PlayableGraph graph;
    AnimationClipPlayable clipPlayable;
    bool hasGraph;

    void Start()
    {
        seed = Random.value * 100f;
        PickTarget();
        if (clip != null)
        {
            var anim = GetComponentInChildren<Animator>();
            if (anim == null) anim = transform.GetChild(0).gameObject.AddComponent<Animator>();
            anim.applyRootMotion = false;
            graph = PlayableGraph.Create(name + "_clip");
            var output = AnimationPlayableOutput.Create(graph, "out", anim);
            clipPlayable = AnimationClipPlayable.Create(graph, clip);
            clipPlayable.SetApplyFootIK(false);
            output.SetSourcePlayable(clipPlayable);
            clipPlayable.SetTime(Random.value * clip.length); // desync identical creatures
            // We drive the graph ourselves every frame (Manual mode): reliable in the editor, in Play mode and in VR builds.
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            hasGraph = true;
        }
    }

    void OnDestroy() { if (hasGraph) graph.Destroy(); }

    void PickTarget()
    {
        Vector2 r = Random.insideUnitCircle * zoneRadius;
        target = new Vector3(zoneCenter.x + r.x, Random.Range(minY, maxY), zoneCenter.z + r.y);
        retargetAt = Time.time + Random.Range(retargetMin, retargetMax);
    }

    void Update()
    {
        // advance and loop the built-in clip
        if (hasGraph && clip.length > 0f)
        {
            double t = clipPlayable.GetTime() + Time.deltaTime * animSpeed;
            if (t > clip.length) t %= clip.length;
            clipPlayable.SetTime(t);
            graph.Evaluate(0f);
        }
        if (!swim) return;

        Vector3 to = target - transform.position;
        if (to.magnitude < Mathf.Max(2f, speed * 2f) || Time.time > retargetAt) { PickTarget(); to = target - transform.position; }

        Quaternion want = Quaternion.LookRotation(to.normalized, Vector3.up);
        Vector3 e = want.eulerAngles;
        float pitch = Mathf.DeltaAngle(0f, e.x);
        want = Quaternion.Euler(Mathf.Clamp(pitch, -maxPitch, maxPitch), e.y, 0f);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnRate * Time.deltaTime);

        float s = speed * (1f + speedVariation * (Mathf.PerlinNoise(seed, Time.time * 0.2f) - 0.5f) * 2f);
        Vector3 p = transform.position + transform.forward * s * Time.deltaTime;
        p.y = Mathf.Clamp(p.y, minY, maxY);
        transform.position = p;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0f, 0.8f, 1f, 0.5f);
        Gizmos.DrawWireCube(new Vector3(zoneCenter.x, (minY + maxY) * 0.5f, zoneCenter.z),
                            new Vector3(zoneRadius * 2f, maxY - minY, zoneRadius * 2f));
    }
}
