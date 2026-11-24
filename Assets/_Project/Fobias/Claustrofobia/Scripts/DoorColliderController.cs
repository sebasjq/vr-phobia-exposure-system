
using UnityEngine;

public class DoorColliderController : MonoBehaviour
{
    [Header("Collider de las puertas")]
    [SerializeField] private Collider doorCollider;

    [Header("Tiempo de la animación")]
    [SerializeField] private float closedTime = 8f;
    [SerializeField] private float openTime = 0f;

    public void UpdateDoorCollider(float animationTime)
    {
        bool doorsClosed = animationTime >= closedTime ||
                           animationTime <= openTime;

        doorCollider.enabled = doorsClosed;
    }
}
