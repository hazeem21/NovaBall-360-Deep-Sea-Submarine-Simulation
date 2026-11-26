using UnityEngine;

public class SubmarineCameraController : MonoBehaviour
{
    [Header("Camera References")]
    [SerializeField] Camera cockpitCamera;
    [SerializeField] Camera exteriorCamera;

    [Header("Switch Controls")]
    [SerializeField] KeyCode switchKey = KeyCode.V;

    [Header("Exterior Orbit Inspection")]
    [SerializeField] Transform orbitTarget;        // Assign Orientation Cube or NOVA_ABYSS_Submarine
    [SerializeField] float defaultDistance = 6.0f;
    [SerializeField] float minDistance = 2.5f;
    [SerializeField] float maxDistance = 15.0f;
    [SerializeField] float orbitSpeed = 3.5f;
    [SerializeField] float zoomSpeed = 4.0f;

    private bool isCockpitView = true;
    private float currentYaw = 0f;
    private float currentPitch = 20f;
    private float currentDistance;

    void Start()
    {
        if (orbitTarget == null) orbitTarget = transform;
        currentDistance = defaultDistance;
        SetActiveCamera(true);
    }

    void Update()
    {
        // Press V to toggle between Cockpit and Exterior view
        if (Input.GetKeyDown(switchKey))
        {
            isCockpitView = !isCockpitView;
            SetActiveCamera(isCockpitView);
        }

        // Allow mouse orbit and zoom when in Exterior Inspection view
        if (!isCockpitView && exteriorCamera != null && orbitTarget != null)
        {
            HandleExteriorOrbit();
        }
    }

    private void SetActiveCamera(bool cockpitActive)
    {
        if (cockpitCamera != null) cockpitCamera.enabled = cockpitActive;
        if (exteriorCamera != null) exteriorCamera.enabled = !cockpitActive;
    }

    private void HandleExteriorOrbit()
    {
        // Hold Right Mouse Button and move mouse to inspect around the submarine
        if (Input.GetMouseButton(1))
        {
            currentYaw += Input.GetAxis("Mouse X") * orbitSpeed;
            currentPitch -= Input.GetAxis("Mouse Y") * orbitSpeed;
            currentPitch = Mathf.Clamp(currentPitch, -80f, 80f);
        }

        // Mouse Scroll Wheel to zoom in/out for close damage inspection
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f)
        {
            currentDistance = Mathf.Clamp(currentDistance - scroll * zoomSpeed, minDistance, maxDistance);
        }

        // Position Exterior Camera relative to the submarine's orientation
        Quaternion orbitRot = orbitTarget.rotation * Quaternion.Euler(currentPitch, currentYaw, 0f);
        Vector3 offset = orbitRot * new Vector3(0f, 0f, -currentDistance);

        exteriorCamera.transform.position = orbitTarget.position + offset;
        exteriorCamera.transform.LookAt(orbitTarget.position, orbitTarget.up);
    }
}