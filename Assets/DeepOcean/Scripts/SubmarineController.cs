using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Drives the submarine. Two flight modes (choose in the Inspector, or press M while playing):
///
/// FREE 360°  (default – made for the Eight360 NOVA ball, which can turn the rider in every direction)
///   The submarine can pitch, roll and turn all the way round: loops, barrel rolls, upside down.
///   It moves along ITS OWN axes (forward is wherever the nose points).
///
///                         Keyboard        Gamepad             Meta Quest Touch
///   Forward / back ...... W / S           Left stick ↑ / ↓    LEFT thumbstick ↑ / ↓
///   Turn (yaw) .......... A / D           Left stick ← / →    LEFT thumbstick ← / →
///   Nose down / up ...... ↑ / ↓ arrows    Right stick ↑ / ↓   RIGHT thumbstick ↑ / ↓
///   Roll left / right ... ← / → arrows    Right stick ← / →   RIGHT thumbstick ← / →
///   Thrusters up / down . E / Q           RB / LB             Right grip / Left grip
///   Level out (hold) .... Space           X (west)            Y (left controller)
///   Boost (hold) ........ Left Shift      Right trigger       Right index trigger
///   Headlights .......... L               A (south)           A (right controller)
///   Switch flight mode .. M
///
/// GENTLE  (stable, can't flip: small automatic tilt only)
///   Same as above, but the right stick / right thumbstick / arrows ↑↓ move straight up and down.
///
/// The Rigidbody's origin is the pilot's HIP and its axes are x = right, y = up, z = forward,
/// as the NOVA SDK README requires. The "Build Submarine Rig" tool sets this up for you.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class SubmarineController : MonoBehaviour
{
    public enum FlightMode { Free360, Gentle }

    [Header("Flight mode")]
    [Tooltip("Free 360: full rotation in every direction (for the NOVA ball).\nGentle: always upright, only a small tilt.")]
    public FlightMode flightMode = FlightMode.Free360;

    [Header("Speeds (metres per second, degrees per second)")]
    public float maxForwardSpeed = 12f;
    public float maxReverseSpeed = 4f;
    public float maxVerticalSpeed = 6f;
    public float maxTurnRate = 30f;      // yaw
    public float maxPitchRate = 35f;     // Free 360 only
    public float maxRollRate = 45f;      // Free 360 only

    [Header("Smoothness (how quickly speed changes)")]
    public float acceleration = 3f;          // m/s per second, forward/back
    public float verticalAcceleration = 2f;  // m/s per second, up/down
    public float turnAcceleration = 40f;     // deg/s per second (yaw, pitch and roll)

    [Header("Dive boost (hold Left Shift / Right Trigger)")]
    [Tooltip("Movement speeds are multiplied by this while boosting, so the 11 km dive takes minutes, not hours.\n" +
             "x8: max ~48 m/s -> surface to Challenger Deep in about 4 minutes.")]
    public float boostMultiplier = 8f;

    [Header("Free 360 options")]
    [Tooltip("Ticked: pushing the stick / up arrow FORWARD tips the nose DOWN (like a plane). Unticked: the opposite.")]
    public bool stickForwardNoseDown = true;
    [Tooltip("How fast 'Level out' turns the submarine back upright (degrees per second).")]
    public float levelOutSpeed = 45f;
    [Tooltip("Slowly return upright by itself whenever you are not steering pitch/roll.")]
    public bool autoLevelWhenIdle = false;

    [Header("Gentle mode tilt")]
    [Tooltip("Max nose-down / nose-up angle while descending / ascending.")]
    [Range(0f, 30f)] public float maxPitch = 12f;
    [Tooltip("Max lean while turning.")]
    [Range(0f, 15f)] public float maxRoll = 5f;
    [Tooltip("How quickly the tilt follows the movement (higher = quicker).")]
    public float tiltResponse = 1.5f;

    [Header("Surface limit")]
    [Tooltip("The shared depth scale. Used to stop the submarine flying out of the water.")]
    public OceanScaleSettings scaleSettings;
    [Tooltip("The pilot's hip cannot rise higher than this many metres below the surface.\n" +
             "NEGATIVE = the hip may be ABOVE the surface, so the submarine floats high.\n" +
             "-0.95 keeps the waterline below the cockpit floor, so no water is seen inside the cabin at the surface.")]
    public float minDepthBelowSurface = -0.95f;

    [Header("Lights  (L key / gamepad A / Quest A to switch on and off)")]
    [Tooltip("Are the headlights on right now? (Starts off: it's daylight at the surface.)")]
    public bool headlightsOn = false;
    [Tooltip("Headlights switch ON automatically the first time you dive deeper than this (metres). 0 = never automatically.")]
    public float autoHeadlightsDepth = 150f;
    [Tooltip("Range of the two main headlights (m).")]
    public float headlightRange = 250f;
    [Tooltip("Range of the 16 auxiliary flood lights (m).")]
    public float auxLightRange = 80f;
    [Tooltip("Range of the cockpit interior glow lights (m).")]
    public float interiorLightRange = 4f;
    [Tooltip("Multiplies the imported headlight brightness.")]
    [Min(0f)] public float headlightBrightness = 1f;

    [Header("Read-only (for checking)")]
    [SerializeField] float forwardSpeed;
    [SerializeField] float verticalSpeed;
    [SerializeField] Vector3 rotationRates;     // pitch, yaw, roll in degrees per second
    [SerializeField] Vector3 currentAngles;     // pitch, yaw, roll as Unity shows them
    [SerializeField] float depthMetres;
    [SerializeField] bool boosting;

    Rigidbody rb;
    // Gentle mode state
    float yaw, gentlePitch, gentleRoll;
    // Inputs (-1..1)
    float throttleInput, turnInput, verticalInput, pitchInput, rollInput;
    bool levelInput, autoLightsDone;

    readonly List<Light> headlights = new List<Light>();
    readonly List<Light> auxLights = new List<Light>();
    readonly List<float> headlightBaseIntensity = new List<float>();

    // ---------------------------------------------------------------------------------
    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;                                     // neutral buoyancy
        rb.freezeRotation = true;                                  // collisions can't spin us; we steer rotation ourselves
        rb.interpolation = RigidbodyInterpolation.Interpolate;     // smooth camera
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        yaw = transform.eulerAngles.y;
        SortLights();
        ApplyLights();
    }

    /// <summary>Finds the lights inside the imported model and gives them sensible ranges.</summary>
    void SortLights()
    {
        foreach (Light light in GetComponentsInChildren<Light>(true))
        {
            if (light.type == LightType.Directional) continue;
            if (light.name.Contains("Headlight"))
            {
                light.range = headlightRange;
                headlights.Add(light);
                headlightBaseIntensity.Add(light.intensity);
            }
            else if (light.name.StartsWith("Aux_"))
            {
                light.range = auxLightRange;
                auxLights.Add(light);
            }
            else
            {
                light.range = interiorLightRange;   // cockpit glows stay on
            }
        }
        if (headlights.Count == 0)
            Debug.LogWarning("[SubmarineController] No headlight lights found in the submarine model.");
    }

    void ApplyLights()
    {
        for (int i = 0; i < headlights.Count; i++)
        {
            headlights[i].enabled = headlightsOn;
            headlights[i].intensity = headlightBaseIntensity[i] * headlightBrightness;
        }
        foreach (Light light in auxLights) light.enabled = headlightsOn;
    }

    /// <summary>Switch the headlights and flood lights on or off (can also be called by UI buttons later).</summary>
    public void SetHeadlights(bool on)
    {
        headlightsOn = on;
        ApplyLights();
        Debug.Log($"[SubmarineController] Headlights {(on ? "ON" : "OFF")}");
    }

    /// <summary>Flip the headlights (for UI buttons / NOVA controls later).</summary>
    public void ToggleHeadlights() => SetHeadlights(!headlightsOn);

    // ---------------------------------------------------------------------------------
    // Meta Quest Touch controller actions (work through OpenXR when a headset is connected)
    InputAction xrLeftStick, xrRightStick, xrBoost, xrLights, xrGripUp, xrGripDown, xrLevel;

    void OnEnable()
    {
        xrLeftStick  = new InputAction("XR Left Stick",  InputActionType.Value, "<XRController>{LeftHand}/{Primary2DAxis}",  processors: "stickDeadzone");
        xrRightStick = new InputAction("XR Right Stick", InputActionType.Value, "<XRController>{RightHand}/{Primary2DAxis}", processors: "stickDeadzone");
        xrBoost      = new InputAction("XR Boost",       InputActionType.Value, "<XRController>{RightHand}/{Trigger}");
        xrLights     = new InputAction("XR Lights",      InputActionType.Button, "<XRController>{RightHand}/{PrimaryButton}");
        xrGripUp     = new InputAction("XR Thrust Up",   InputActionType.Button, "<XRController>{RightHand}/{GripButton}");
        xrGripDown   = new InputAction("XR Thrust Down", InputActionType.Button, "<XRController>{LeftHand}/{GripButton}");
        xrLevel      = new InputAction("XR Level Out",   InputActionType.Button, "<XRController>{LeftHand}/{SecondaryButton}");
        foreach (var a in XrActions()) a.Enable();
    }

    void OnDisable()
    {
        foreach (var a in XrActions()) a?.Dispose();
        xrLeftStick = xrRightStick = xrBoost = xrLights = xrGripUp = xrGripDown = xrLevel = null;
    }

    InputAction[] XrActions() => new[] { xrLeftStick, xrRightStick, xrBoost, xrLights, xrGripUp, xrGripDown, xrLevel };

    void Update()
    {
        ReadInput();
    }

    void ReadInput()
    {
        throttleInput = turnInput = verticalInput = pitchInput = rollInput = 0f;
        boosting = false; levelInput = false;
        bool toggleLights = false;
        bool free = flightMode == FlightMode.Free360;

        // Right stick / arrows ↑↓: pitch in Free 360, straight up/down in Gentle.
        float rightY = 0f, rightX = 0f, thrust = 0f;

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed) throttleInput += 1f;
            if (kb.sKey.isPressed) throttleInput -= 1f;
            if (kb.dKey.isPressed) turnInput += 1f;
            if (kb.aKey.isPressed) turnInput -= 1f;
            if (kb.eKey.isPressed) thrust += 1f;
            if (kb.qKey.isPressed) thrust -= 1f;
            if (kb.upArrowKey.isPressed) rightY += 1f;
            if (kb.downArrowKey.isPressed) rightY -= 1f;
            if (kb.rightArrowKey.isPressed) rightX += 1f;
            if (kb.leftArrowKey.isPressed) rightX -= 1f;
            boosting |= kb.leftShiftKey.isPressed;
            levelInput |= kb.spaceKey.isPressed;
            toggleLights |= kb.lKey.wasPressedThisFrame;
            if (kb.mKey.wasPressedThisFrame) SwitchFlightMode();
        }

        if (xrLeftStick != null)
        {
            Vector2 l = xrLeftStick.ReadValue<Vector2>();
            Vector2 r = xrRightStick.ReadValue<Vector2>();
            throttleInput += l.y;
            turnInput += l.x;
            rightY += r.y;
            rightX += r.x;
            if (xrGripUp.IsPressed()) thrust += 1f;
            if (xrGripDown.IsPressed()) thrust -= 1f;
            boosting |= xrBoost.ReadValue<float>() > 0.5f;
            levelInput |= xrLevel.IsPressed();
            toggleLights |= xrLights.WasPressedThisFrame();
        }

        Gamepad pad = Gamepad.current;
        if (pad != null)
        {
            Vector2 l = pad.leftStick.ReadValue();
            Vector2 r = pad.rightStick.ReadValue();
            throttleInput += l.y;
            turnInput += l.x;
            rightY += r.y;
            rightX += r.x;
            if (pad.rightShoulder.isPressed) thrust += 1f;
            if (pad.leftShoulder.isPressed) thrust -= 1f;
            boosting |= pad.rightTrigger.ReadValue() > 0.5f;
            levelInput |= pad.buttonWest.isPressed;
            toggleLights |= pad.buttonSouth.wasPressedThisFrame;
        }

        if (free)
        {
            pitchInput = stickForwardNoseDown ? rightY : -rightY;
            rollInput = rightX;
            verticalInput = thrust;
        }
        else
        {
            verticalInput = thrust + rightY;   // Gentle: right stick moves straight up/down
        }

        throttleInput = Mathf.Clamp(throttleInput, -1f, 1f);
        turnInput = Mathf.Clamp(turnInput, -1f, 1f);
        verticalInput = Mathf.Clamp(verticalInput, -1f, 1f);
        pitchInput = Mathf.Clamp(pitchInput, -1f, 1f);
        rollInput = Mathf.Clamp(rollInput, -1f, 1f);

        if (toggleLights)
        {
            autoLightsDone = true;   // the pilot is in control now
            SetHeadlights(!headlightsOn);
        }

        // Switch the lights on by themselves when it gets dark (only once, and only if the pilot hasn't chosen).
        if (!autoLightsDone && autoHeadlightsDepth > 0f && depthMetres >= autoHeadlightsDepth)
        {
            autoLightsDone = true;
            if (!headlightsOn) SetHeadlights(true);
        }
    }

    /// <summary>Switch between Free 360 and Gentle while playing (M key).</summary>
    public void SwitchFlightMode()
    {
        flightMode = flightMode == FlightMode.Free360 ? FlightMode.Gentle : FlightMode.Free360;
        // Continue smoothly: take the current heading as the Gentle heading.
        Vector3 flatForward = Vector3.ProjectOnPlane(rb.rotation * Vector3.forward, Vector3.up);
        if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.ProjectOnPlane(rb.rotation * Vector3.up, Vector3.up);
        yaw = Quaternion.LookRotation(flatForward.normalized, Vector3.up).eulerAngles.y;
        gentlePitch = gentleRoll = 0f;
        Debug.Log($"[SubmarineController] Flight mode: {flightMode}");
    }

    // ---------------------------------------------------------------------------------
    void FixedUpdate()
    {
        if (flightMode == FlightMode.Free360) FlyFree360(Time.fixedDeltaTime);
        else FlyGentle(Time.fixedDeltaTime);

        if (scaleSettings != null)
            depthMetres = scaleSettings.WorldYToDepthMetres(rb.position.y);
        currentAngles = rb.rotation.eulerAngles;
    }

    /// <summary>Full 6-direction flight: rotate around the submarine's own axes, move along its own axes.</summary>
    void FlyFree360(float dt)
    {
        float boost = boosting ? boostMultiplier : 1f;

        // ---- Rotation rates (smooth start/stop) ----
        // Unity: +X = nose down, +Y = turn right, +Z = roll LEFT  (so roll right = negative Z).
        Vector3 targetRates = new Vector3(pitchInput * maxPitchRate, turnInput * maxTurnRate, -rollInput * maxRollRate);
        rotationRates = Vector3.MoveTowards(rotationRates, targetRates, turnAcceleration * dt);

        Quaternion rotation = rb.rotation * Quaternion.Euler(rotationRates * dt);   // around its OWN axes

        // ---- Level out (hold Space / X / Y), or automatically when idle ----
        bool steeringTilt = Mathf.Abs(pitchInput) > 0.05f || Mathf.Abs(rollInput) > 0.05f;
        if (levelInput || (autoLevelWhenIdle && !steeringTilt))
        {
            Vector3 flatForward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 0.001f)
            {
                // Pointing straight down: pulling the nose up makes the ROOF the new forward.
                // Pointing straight up: pushing the nose down makes the FLOOR the new forward.
                float noseDown = Mathf.Sign(Vector3.Dot(rotation * Vector3.forward, Vector3.down));
                flatForward = Vector3.ProjectOnPlane(rotation * Vector3.up, Vector3.up) * noseDown;
            }
            if (flatForward.sqrMagnitude > 0.001f)
            {
                Quaternion upright = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
                float speed = levelInput ? levelOutSpeed : levelOutSpeed * 0.3f;
                rotation = Quaternion.RotateTowards(rotation, upright, speed * dt);
            }
        }

        rb.MoveRotation(rotation);

        // ---- Movement along the submarine's own axes ----
        Vector3 local = Quaternion.Inverse(rotation) * rb.linearVelocity;
        float targetForward = throttleInput >= 0f ? throttleInput * maxForwardSpeed : throttleInput * maxReverseSpeed;
        local.z = Mathf.MoveTowards(local.z, targetForward * boost, acceleration * boost * dt);
        local.x = Mathf.MoveTowards(local.x, 0f, acceleration * dt);                                    // no side drift
        local.y = Mathf.MoveTowards(local.y, verticalInput * maxVerticalSpeed * boost, verticalAcceleration * boost * dt);

        Vector3 world = rotation * local;
        world = ApplySurfaceLimit(world);
        rb.linearVelocity = world;

        forwardSpeed = local.z;
        verticalSpeed = world.y;
    }

    /// <summary>Stable flight: always upright, small automatic tilt only.</summary>
    void FlyGentle(float dt)
    {
        float boost = boosting ? boostMultiplier : 1f;

        // Work in a "flat" frame that only follows the heading (yaw), so tilting never changes the direction of travel.
        Quaternion heading = Quaternion.Euler(0f, yaw, 0f);
        Vector3 local = Quaternion.Inverse(heading) * rb.linearVelocity;

        float targetForward = throttleInput >= 0f ? throttleInput * maxForwardSpeed : throttleInput * maxReverseSpeed;
        local.z = Mathf.MoveTowards(local.z, targetForward * boost, acceleration * boost * dt);
        local.x = Mathf.MoveTowards(local.x, 0f, acceleration * dt);
        local.y = Mathf.MoveTowards(local.y, verticalInput * maxVerticalSpeed * boost, verticalAcceleration * boost * dt);

        Vector3 world = ApplySurfaceLimit(heading * local);
        rb.linearVelocity = world;

        // Turning
        rotationRates.y = Mathf.MoveTowards(rotationRates.y, turnInput * maxTurnRate, turnAcceleration * dt);
        rotationRates.x = rotationRates.z = 0f;
        yaw += rotationRates.y * dt;

        // Gentle tilt: nose down when diving, small lean in turns
        float verticalFraction = Mathf.Clamp(-world.y / Mathf.Max(0.01f, maxVerticalSpeed), -1f, 1f);
        float k = 1f - Mathf.Exp(-tiltResponse * dt);
        gentlePitch = Mathf.Lerp(gentlePitch, verticalFraction * maxPitch, k);
        gentleRoll = Mathf.Lerp(gentleRoll, -(rotationRates.y / Mathf.Max(0.01f, maxTurnRate)) * maxRoll, k);

        // Slerp so that switching from Free 360 back to Gentle rights the submarine smoothly.
        Quaternion target = Quaternion.Euler(gentlePitch, yaw, gentleRoll);
        rb.MoveRotation(Quaternion.Slerp(rb.rotation, target, 1f - Mathf.Exp(-3f * dt)));

        forwardSpeed = local.z;
        verticalSpeed = world.y;
    }

    /// <summary>Stops the submarine rising out of the water.</summary>
    Vector3 ApplySurfaceLimit(Vector3 worldVelocity)
    {
        if (scaleSettings == null) return worldVelocity;
        float ceilingY = scaleSettings.surfaceY - minDepthBelowSurface;
        if (rb.position.y >= ceilingY && worldVelocity.y > 0f) worldVelocity.y = 0f;
        if (rb.position.y > ceilingY + 0.5f) worldVelocity.y = Mathf.Min(worldVelocity.y, -1f);   // gently push back down
        return worldVelocity;
    }
}
