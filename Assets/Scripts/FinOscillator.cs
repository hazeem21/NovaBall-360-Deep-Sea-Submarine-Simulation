using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Small rhythmic motion for secondary parts: pectoral fins, flippers, a jaw, gill flaps.
/// Each entry swings one bone around an axis given in the creature's own space.
/// Use it alongside a swim controller, on bones the swim controller does not drive.
/// </summary>
[DisallowMultipleComponent]
public class FinOscillator : MonoBehaviour
{
    [System.Serializable]
    public class Entry
    {
        public Transform bone;
        [Tooltip("Rotation axis in the creature's root space (x = right, y = up, z = forward).")]
        public Vector3 axis = Vector3.forward;
        [Tooltip("Swing in degrees each way.")]
        public float amplitude = 10f;
        [Tooltip("Constant offset in degrees (e.g. to hold a fin slightly spread).")]
        public float offset = 0f;
        [Tooltip("Phase offset in cycles (0..1). Use 0.5 to mirror the other side.")]
        public float phase = 0f;
        [Tooltip("Multiplier on the shared frequency.")]
        public float frequencyScale = 1f;
        [Tooltip("Adds slow random wander on top, so it doesn't look mechanical.")]
        public float noise = 0.3f;

        [System.NonSerialized] public Quaternion rest;
        [System.NonSerialized] public Vector3 axisInParent;
        [System.NonSerialized] public float seed;
    }

    [Tooltip("Cycles per second.")]
    public float frequency = 0.4f;
    public List<Entry> entries = new List<Entry>();

    void Start()
    {
        foreach (var e in entries)
        {
            if (e.bone == null) continue;
            e.rest = e.bone.localRotation;
            Quaternion pr = e.bone.parent ? e.bone.parent.rotation : Quaternion.identity;
            e.axisInParent = (Quaternion.Inverse(pr) * transform.TransformDirection(e.axis.normalized)).normalized;
            e.seed = Random.value * 50f;
        }
    }

    void LateUpdate()
    {
        float t = Time.time;
        foreach (var e in entries)
        {
            if (e.bone == null) continue;
            float ph = (t * frequency * e.frequencyScale + e.phase) * Mathf.PI * 2f;
            float n = e.noise * (Mathf.PerlinNoise(e.seed, t * 0.3f) - 0.5f) * 2f;
            float a = e.offset + e.amplitude * (Mathf.Sin(ph) + n);
            e.bone.localRotation = Quaternion.AngleAxis(a, e.axisInParent) * e.rest;
        }
    }

    /// Adds an entry from code (used by the setup tools).
    public void Add(Transform bone, Vector3 axis, float amplitude, float phase = 0f, float offset = 0f, float freqScale = 1f)
    {
        if (bone == null) return;
        entries.Add(new Entry { bone = bone, axis = axis, amplitude = amplitude, phase = phase, offset = offset, frequencyScale = freqScale });
    }
}
