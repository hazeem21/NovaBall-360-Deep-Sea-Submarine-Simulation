using UnityEngine;

/// <summary>Simple chase camera: put it on the camera and drag the shark into Target.</summary>
public class SharkFollowCamera : MonoBehaviour
{
    public Transform target;
    [Tooltip("Offset from the shark in its own space (behind, above).")]
    public Vector3 offset = new Vector3(0f, 3f, -9f);
    [Range(0.01f, 2f)] public float smoothTime = 0.6f;

    Vector3 _vel;

    void LateUpdate()
    {
        if (target == null) return;
        Quaternion yaw = Quaternion.Euler(0f, target.eulerAngles.y, 0f);
        Vector3 want = target.position + yaw * offset;
        transform.position = Vector3.SmoothDamp(transform.position, want, ref _vel, smoothTime);
        transform.rotation = Quaternion.Slerp(transform.rotation,
            Quaternion.LookRotation(target.position + Vector3.up * 0.5f - transform.position), 1f - Mathf.Exp(-4f * Time.deltaTime));
    }
}
