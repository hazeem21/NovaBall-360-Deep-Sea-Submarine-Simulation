using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

/// <summary>
/// Makes the cockpit camera follow a VR headset (Meta Quest through Quest Link / Air Link, OpenXR on Windows).
/// Attach to the camera inside Submarine_Rig.
///
/// Works in two set-ups:
///   A) Plain camera (no XR Origin): it creates "VR_Eye_Offset" at the pilot's eyes, puts the camera
///      inside it and adds a Tracked Pose Driver, so your head moves the camera (look left/right/up/down, lean).
///   B) Unity's XR Origin (XR Rig): the XR Origin already tracks the head, so this script only adds re-centring.
///
/// Head tracking is ALWAYS connected (the headset often finishes starting a moment after Play begins).
/// Without a headset nothing moves, so the desktop Game view still works.
///
/// Re-centre any time: R key, or X button on the left Quest controller.
/// </summary>
public class VRCockpitCamera : MonoBehaviour
{
    [Tooltip("Seated experience: the headset's start position = the pilot's eyes. Recommended for a submarine / NOVA ball.")]
    public bool seatedTracking = true;

    [Header("Read-only (for checking)")]
    [SerializeField] bool headsetActive;
    [SerializeField] bool usingXROrigin;

    InputAction recenterAction, hmdPosition, hmdRotation;

    void Start()
    {
        // Is this camera already inside Unity's XR Origin (XR Rig)? Then the XR Origin does the head tracking.
        usingXROrigin = transform.parent != null && transform.parent.name == "Camera Offset";

        if (!usingXROrigin)
            SetUpOwnHeadTracking();

        recenterAction = new InputAction("Recenter VR", InputActionType.Button);
        recenterAction.AddBinding("<Keyboard>/r");
        recenterAction.AddBinding("<XRController>{LeftHand}/{PrimaryButton}");
        recenterAction.Enable();

        StartCoroutine(RecenterWhenHeadsetReady());
    }

    void SetUpOwnHeadTracking()
    {
        // 1. Eye offset parent at the current (pilot eye) position.
        Transform offset = new GameObject("VR_Eye_Offset").transform;
        offset.SetParent(transform.parent, false);
        offset.localPosition = transform.localPosition;
        offset.localRotation = transform.localRotation;
        transform.SetParent(offset, false);
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;

        // 2. Camera follows the headset (position + rotation).
        var driver = GetComponent<TrackedPoseDriver>();
        if (driver == null) driver = gameObject.AddComponent<TrackedPoseDriver>();
        hmdPosition = new InputAction("HMD Position", InputActionType.Value, "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
        hmdRotation = new InputAction("HMD Rotation", InputActionType.Value, "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
        hmdPosition.Enable();
        hmdRotation.Enable();
        driver.positionInput = new InputActionProperty(hmdPosition);
        driver.rotationInput = new InputActionProperty(hmdRotation);
        driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
        driver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
    }

    IEnumerator RecenterWhenHeadsetReady()
    {
        // Wait up to ~10 seconds for the headset to finish starting.
        for (float t = 0f; t < 10f && !XRSettings.isDeviceActive; t += Time.unscaledDeltaTime)
            yield return null;

        headsetActive = XRSettings.isDeviceActive;
        if (!headsetActive)
        {
            Debug.Log("[VRCockpitCamera] No VR headset found - desktop mode. (Head tracking is still connected if one starts later.)");
            yield break;
        }

        yield return null;   // one more frame so tracking has a first pose
        Recenter();
        Debug.Log($"[VRCockpitCamera] VR headset active ({(usingXROrigin ? "XR Origin" : "cockpit camera")}). Press R / left X to re-centre.");
    }

    void Update()
    {
        if (recenterAction != null && recenterAction.WasPressedThisFrame())
            Recenter();
    }

    /// <summary>Puts your current head position/direction back onto the pilot's eyes.</summary>
    public void Recenter()
    {
        var subsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        foreach (XRInputSubsystem subsystem in subsystems)
        {
            if (seatedTracking)
                subsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
            subsystem.TryRecenter();
        }
    }

    void OnDestroy()
    {
        recenterAction?.Dispose();
        hmdPosition?.Dispose();
        hmdRotation?.Dispose();
    }
}
