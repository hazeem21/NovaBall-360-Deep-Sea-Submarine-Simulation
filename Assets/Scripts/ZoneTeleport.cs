using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// TESTING HELPER. The seabed is true scale (the abyssal plain is ~16 km away and ~5.9 km down),
/// so this lets you jump the submarine straight to each depth zone while testing.
/// Put it on the submarine (the object with the Rigidbody) and fill "zonePoints" with the
/// "Spawn" children of the Zone objects. Keys 1-5 jump; 0 returns to the start.
/// Turn it off (untick) for real sessions.
/// </summary>
public class ZoneTeleport : MonoBehaviour
{
    public Transform[] zonePoints;
    Vector3 startPos; Quaternion startRot; Rigidbody rb;

    void Start() { rb = GetComponent<Rigidbody>(); startPos = transform.position; startRot = transform.rotation; }

    void Update()
    {
        if (Pressed(0)) Jump(startPos, startRot);
        for (int i = 0; i < 5 && i < zonePoints.Length; i++)
            if (Pressed(i + 1) && zonePoints[i] != null) Jump(zonePoints[i].position, zonePoints[i].rotation);
    }

    // Works with either input backend (Project Settings > Player > Active Input Handling).
    static bool Pressed(int digit)
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null)
        {
            Key[] keys = { Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5 };
            if (kb[keys[digit]].wasPressedThisFrame) return true;
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.Alpha0 + digit)) return true;
#endif
        return false;
    }

    void Jump(Vector3 p, Quaternion r)
    {
        if (rb != null)
        {
            // The sub's Rigidbody is kinematic (moved by its locomotion script), and Unity
            // warns if you set velocity on a kinematic body, so only clear it when it is physical.
            if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.position = p; rb.rotation = r;
        }
        transform.SetPositionAndRotation(p, r);
        Debug.Log("[ZoneTeleport] jumped to " + p);
    }
}
