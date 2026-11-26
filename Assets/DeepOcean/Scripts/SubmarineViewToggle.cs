using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Press C (gamepad Y / north button, or B on the right Quest controller) to switch between:
///   COCKPIT view  - the pilot's eyes (used in the NOVA ball)
///   OUTSIDE view  - behind and above the submarine, so you can SEE it moving (for testing)
///
/// Attach to the Main Camera that sits inside Submarine_Rig.
/// Works in VR too: then it moves the "VR_Eye_Offset" parent (the headset still controls the camera itself).
/// </summary>
public class SubmarineViewToggle : MonoBehaviour
{
    [Tooltip("Outside camera position, relative to the submarine (x right, y up, z forward). Negative Z = behind.")]
    public Vector3 outsideOffset = new Vector3(0f, 4f, -14f);
    [Tooltip("How much the outside camera looks down (degrees).")]
    public float outsidePitch = 12f;
    [Tooltip("Start in the outside view.")]
    public bool startOutside = false;

    Transform target;          // what we move: the camera, or its VR_Eye_Offset parent in VR
    Vector3 cockpitPosition;
    Quaternion cockpitRotation;
    bool outside;
    InputAction toggleAction;

    void OnEnable()
    {
        toggleAction = new InputAction("Toggle View", InputActionType.Button);
        toggleAction.AddBinding("<Keyboard>/c");
        toggleAction.AddBinding("<Gamepad>/buttonNorth");
        toggleAction.AddBinding("<XRController>{RightHand}/{SecondaryButton}");   // B on the right Quest controller
        toggleAction.Enable();
    }

    void OnDisable()
    {
        toggleAction?.Dispose();
        toggleAction = null;
    }

    void Start()
    {
        if (startOutside) SetView(true);
    }

    void Update()
    {
        if (toggleAction != null && toggleAction.WasPressedThisFrame())
            SetView(!outside);
    }

    /// <summary>
    /// In VR the headset controls the camera itself, so move its holder instead:
    ///   our own set-up:  "VR_Eye_Offset"
    ///   Unity XR Origin: the XR Origin object (parent of "Camera Offset")
    /// </summary>
    Transform CurrentTarget()
    {
        Transform parent = transform.parent;
        if (parent == null) return transform;
        if (parent.name == "VR_Eye_Offset") return parent;
        if (parent.name == "Camera Offset" && parent.parent != null) return parent.parent;
        return transform;
    }

    void SetView(bool showOutside)
    {
        Transform t = CurrentTarget();
        if (t != target)
        {
            // Remember the cockpit position of whatever we are about to move.
            target = t;
            cockpitPosition = target.localPosition;
            cockpitRotation = target.localRotation;
        }

        outside = showOutside;
        if (outside)
        {
            target.localPosition = outsideOffset;
            target.localRotation = Quaternion.Euler(outsidePitch, 0f, 0f);
        }
        else
        {
            target.localPosition = cockpitPosition;
            target.localRotation = cockpitRotation;
        }
    }
}
