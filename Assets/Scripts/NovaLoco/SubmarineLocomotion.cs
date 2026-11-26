using System.Reflection;
using UnityEngine;

[RequireComponent(typeof(Rotator))]
public class SubmarineLocomotion : MonoBehaviour
{
    [Header("Thruster & Speed Settings")]
    [SerializeField] float maxForwardSpeed = 10.0f;
    [SerializeField] float maxReverseSpeed = 4.5f;
    [SerializeField] float surgeAcceleration = 4.5f;
    [SerializeField] float heaveSpeed = 4.0f;
    [SerializeField] float swaySpeed = 3.5f;

    [Header("Hydrodynamic Drag")]
    [SerializeField] float linearWaterDrag = 1.4f;
    [SerializeField] float angularWaterDrag = 3.2f;

    [Header("Full 360° Maneuverability (Deg/Sec)")]
    [SerializeField] float maxYawRate = 55.0f;         // Rudder turn (A / D)
    [SerializeField] float maxPitchRate = 65.0f;       // Full 360° loop pitch (Up / Down Arrows)
    [SerializeField] float maxRollRate = 75.0f;        // Full 360° barrel roll (Z / C)
    [SerializeField] float turnBankFactor = 0.25f;     // Subtle natural bank during rudder turns

    [Header("Water Resistance Vibration & Ocean Current")]
    [SerializeField] float idleCurrentIntensity = 1.8f;    // Gentle underwater drift when stopped
    [SerializeField] float speedJitterIntensity = 6.5f;    // Hull vibration at high speed
    [SerializeField] float jitterFrequency = 14.0f;        // Speed of water turbulence vibration

    [Header("Hull Collision & Impact Simulation")]
    [SerializeField] float hullRadius = 0.6f;              // Match your Orientation Cube / Sphere size
    [SerializeField] float bounceForce = 5.0f;             // Knockback speed on impact
    [SerializeField] float impactSpinMultiplier = 35.0f;   // How hard an impact twists the sub
    [SerializeField] LayerMask collisionLayers = ~0;       // Collide with everything by default

    private Rotator rotator;
    private FieldInfo rollField;
    private FieldInfo pitchField;
    private FieldInfo yawField;

    // Linear & Angular Velocities
    private Vector3 currentLocalVel;     // x = Sway, y = Local Vertical, z = Surge
    private float currentWorldHeave;     // Ballast vertical movement
    private Vector3 impactKnockbackVel;  // World-space bounce velocity from hits

    private float currentPitchVel;
    private float currentYawVel;
    private float currentRollVel;

    // Impact shockwave timer
    private float impactShockTimer;
    private float noiseSeed;

    void Awake()
    {
        rotator = GetComponent<Rotator>();
        noiseSeed = Random.Range(0f, 100f);

        // Access private [SerializeField] variables in Rotator without modifying Rotator.cs
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        rollField = typeof(Rotator).GetField("rollVelocity", flags);
        pitchField = typeof(Rotator).GetField("pitchVelocity", flags);
        yawField = typeof(Rotator).GetField("yawVelocity", flags);
    }

    void Update()
    {
        // --- 1. PILOT CONTROLS ---
        float throttleInput = Input.GetAxisRaw("Vertical");     // W / S (Forward / Reverse)
        float rudderInput = Input.GetAxisRaw("Horizontal");     // A / D (Yaw Left / Right)

        float pitchInput = 0f;                                  // Up / Down Arrows (Full 360 Pitch)
        if (Input.GetKey(KeyCode.UpArrow)) pitchInput -= 1f;
        if (Input.GetKey(KeyCode.DownArrow)) pitchInput += 1f;

        float rollInput = 0f;                                   // Z / C (Full 360 Barrel Roll)
        if (Input.GetKey(KeyCode.Z)) rollInput += 1f;
        if (Input.GetKey(KeyCode.C)) rollInput -= 1f;

        float heaveInput = 0f;                                  // Space / Left Ctrl (Ballast Up / Down)
        if (Input.GetKey(KeyCode.Space)) heaveInput += 1f;
        if (Input.GetKey(KeyCode.LeftControl)) heaveInput -= 1f;

        float swayInput = 0f;                                   // Q / E (Lateral Thrusters)
        if (Input.GetKey(KeyCode.E)) swayInput += 1f;
        if (Input.GetKey(KeyCode.Q)) swayInput -= 1f;

        // Emergency All-Stop (R)
        if (Input.GetKeyDown(KeyCode.R))
        {
            currentLocalVel = Vector3.zero;
            currentWorldHeave = 0f;
            currentPitchVel = currentYawVel = currentRollVel = 0f;
        }

        // --- 2. LINEAR THRUST + WATER DRAG ---
        float targetSurge = throttleInput >= 0f
            ? throttleInput * maxForwardSpeed
            : throttleInput * maxReverseSpeed;

        float surgeRate = Mathf.Abs(throttleInput) > 0.01f ? surgeAcceleration : linearWaterDrag;
        currentLocalVel.z = Mathf.MoveTowards(currentLocalVel.z, targetSurge, surgeRate * Time.deltaTime);
        currentLocalVel.x = Mathf.Lerp(currentLocalVel.x, swayInput * swaySpeed, linearWaterDrag * Time.deltaTime);
        currentWorldHeave = Mathf.Lerp(currentWorldHeave, heaveInput * heaveSpeed, linearWaterDrag * Time.deltaTime);

        // --- 3. UNRESTRICTED 360° ANGULAR VELOCITIES ---
        float targetYaw = rudderInput * maxYawRate;
        float targetPitch = pitchInput * maxPitchRate;

        // Combine manual barrel roll (Z/C) with subtle natural rudder banking
        float targetRoll = (rollInput * maxRollRate) - (rudderInput * maxYawRate * turnBankFactor);

        // Optional: Hold 'L' if the pilot wants the gyro-stabilizer to level the sub right-side up
        if (Input.GetKey(KeyCode.L))
        {
            Vector3 euler = transform.localEulerAngles;
            float pitchErr = NormalizeAngle(euler.x);
            float rollErr = NormalizeAngle(euler.z);
            targetPitch = -pitchErr * 3.0f;
            targetRoll = -rollErr * 3.0f;
        }

        currentYawVel = Mathf.Lerp(currentYawVel, targetYaw, angularWaterDrag * Time.deltaTime);
        currentPitchVel = Mathf.Lerp(currentPitchVel, targetPitch, angularWaterDrag * Time.deltaTime);
        currentRollVel = Mathf.Lerp(currentRollVel, targetRoll, angularWaterDrag * Time.deltaTime);

        // --- 4. PROCEDURAL HYDRODYNAMIC JITTER & IMPACT TREMOR ---
        float speedRatio = Mathf.Clamp01(currentLocalVel.magnitude / maxForwardSpeed);
        float t = Time.time * jitterFrequency;

        // Multi-frequency Perlin noise creates realistic hull vibration under water pressure
        float nPitch = (Mathf.PerlinNoise(noiseSeed, t) - 0.5f) * 2f;
        float nYaw = (Mathf.PerlinNoise(noiseSeed + 10f, t * 0.85f) - 0.5f) * 2f;
        float nRoll = (Mathf.PerlinNoise(noiseSeed + 20f, t * 1.15f) - 0.5f) * 2f;

        // Vibration grows stronger at high speeds + spikes violently during a hull collision
        impactShockTimer = Mathf.Max(0f, impactShockTimer - Time.deltaTime);
        float shockBoost = impactShockTimer * 25.0f;
        float totalJitterAmp = Mathf.Lerp(idleCurrentIntensity, speedJitterIntensity, speedRatio) + shockBoost;

        float jitterPitch = nPitch * totalJitterAmp;
        float jitterYaw = nYaw * (totalJitterAmp * 0.6f);
        float jitterRoll = nRoll * (totalJitterAmp * 1.2f);

        // --- 5. WRITE FINAL VELOCITIES TO UNTOUCHED ROTATOR SCRIPT ---
        pitchField?.SetValue(rotator, currentPitchVel + jitterPitch);
        yawField?.SetValue(rotator, currentYawVel + jitterYaw);
        rollField?.SetValue(rotator, currentRollVel + jitterRoll);
    }

    void FixedUpdate()
    {
        // Decay impact knockback smoothly through water drag
        impactKnockbackVel = Vector3.Lerp(impactKnockbackVel, Vector3.zero, linearWaterDrag * 2.5f * Time.fixedDeltaTime);

        // Calculate desired world displacement for this physics step
        Vector3 localDisplacement = transform.TransformDirection(currentLocalVel) * Time.fixedDeltaTime;
        Vector3 heaveDisplacement = Vector3.up * (currentWorldHeave * Time.fixedDeltaTime);
        Vector3 knockbackDisplacement = impactKnockbackVel * Time.fixedDeltaTime;

        Vector3 totalMove = localDisplacement + heaveDisplacement + knockbackDisplacement;
        float moveDist = totalMove.magnitude;

        // Predictive SphereCast collision check (works even when Rigidbody is Kinematic!)
        if (moveDist > 0.0001f)
        {
            if (Physics.SphereCast(transform.position, hullRadius, totalMove.normalized, out RaycastHit hit, moveDist, collisionLayers, QueryTriggerInteraction.Ignore))
            {
                HandleHullImpact(hit.point, hit.normal);
                // Slide along surface instead of passing through
                totalMove = Vector3.ProjectOnPlane(totalMove, hit.normal);
            }
        }

        transform.position += totalMove;
    }

    // Also catches dynamic objects (like a moving Megalodon) bumping into the sub
    void OnCollisionEnter(Collision collision)
    {
        if (collision.contactCount > 0)
        {
            ContactPoint contact = collision.GetContact(0);
            HandleHullImpact(contact.point, contact.normal);
        }
    }

    void OnTriggerEnter(Collider other)
    {
        Vector3 closestPoint = other.ClosestPoint(transform.position);
        Vector3 normal = (transform.position - closestPoint).normalized;
        if (normal == Vector3.zero) normal = -transform.forward;
        HandleHullImpact(closestPoint, normal);
    }

    private void HandleHullImpact(Vector3 hitPoint, Vector3 hitNormal)
    {
        // Prevent spamming multiple hits in the exact same millisecond
        if (impactShockTimer > 0.35f) return;

        impactShockTimer = 0.5f; // Triggers violent 0.5s hull tremor

        // 1. Physical rebound away from obstacle
        float impactSpeed = Mathf.Max(currentLocalVel.magnitude, 2.0f);
        impactKnockbackVel = hitNormal * (bounceForce + impactSpeed * 0.5f);

        // Kill forward momentum into the wall
        currentLocalVel *= 0.2f;

        // 2. Calculate rotational torque based on where the hit struck the sphere
        Vector3 localHitDir = transform.InverseTransformPoint(hitPoint).normalized;
        // Hitting the top pitches nose down; hitting the left rolls right
        currentPitchVel += localHitDir.y * impactSpinMultiplier;
        currentRollVel -= localHitDir.x * impactSpinMultiplier;
        currentYawVel += localHitDir.x * (impactSpinMultiplier * 0.7f);
    }

    private float NormalizeAngle(float angle)
    {
        while (angle > 180f) angle -= 360f;
        while (angle < -180f) angle += 360f;
        return angle;
    }
}