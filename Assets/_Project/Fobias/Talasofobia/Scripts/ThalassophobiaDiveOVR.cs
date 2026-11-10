using UnityEngine;

// Attach to OVRCameraRig, or the parent that moves the entire XR rig.
[DisallowMultipleComponent]
public class ThalassophobiaDiveOVR : MonoBehaviour
{
    [Header("Dive movement (meters and meters per second)")]
    [SerializeField, Min(0f)] private float diveDepth = 2f;
    [SerializeField, Min(0.01f)] private float descentSpeed = 0.5f;
    [SerializeField, Min(0.01f)] private float ascentSpeed = 0.5f;

    [Header("Right grip")]
    [SerializeField, Range(0.01f, 1f)] private float pressThreshold = 0.5f;
    [SerializeField, Range(0f, 0.99f)] private float releaseThreshold = 0.35f;

    private float currentDepth;
    private bool gripHeld;

    private void Update()
    {
        if (Time.timeScale == 0f) return;

        float grip = OVRInput.Get(
            OVRInput.Axis1D.PrimaryHandTrigger,
            OVRInput.Controller.RTouch);

        // Hysteresis prevents small trigger changes from toggling the movement.
        gripHeld = gripHeld ? grip > releaseThreshold : grip >= pressThreshold;

        float targetDepth = gripHeld ? diveDepth : 0f;
        float speed = gripHeld ? descentSpeed : ascentSpeed;
        float nextDepth = Mathf.MoveTowards(currentDepth, targetDepth, speed * Time.deltaTime);

        // Move the entire rig. Never write to the tracked camera/CenterEyeAnchor.
        transform.position += Vector3.down * (nextDepth - currentDepth);
        currentDepth = nextDepth;
    }

    private void OnDisable()
    {
        transform.position += Vector3.up * currentDepth;
        currentDepth = 0f;
        gripHeld = false;
    }

    private void OnValidate()
    {
        releaseThreshold = Mathf.Min(releaseThreshold, pressThreshold - 0.01f);
    }
}
