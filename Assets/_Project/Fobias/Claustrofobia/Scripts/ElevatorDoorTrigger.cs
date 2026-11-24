
using UnityEngine;

public class ElevatorDoorTrigger : MonoBehaviour
{
    [SerializeField] private Transform playerCamera;
    [SerializeField] private AnimationSegment doorAnimation;

    private bool activated = false;

    private void Update()
    {
        if (activated || playerCamera == null)
            return;

        Vector3 cameraPosition = playerCamera.position;

        if (GetComponent<Collider>().bounds.Contains(cameraPosition))
        {
            doorAnimation.PlaySegment();
            activated = true;
        }
    }
}
