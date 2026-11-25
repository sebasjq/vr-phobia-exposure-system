using System.Collections.Generic;
using UnityEngine;
using MetaCharacter = Oculus.Interaction.Locomotion.CharacterController;
using MetaLocomotor = Oculus.Interaction.Locomotion.FirstPersonLocomotor;

// Scene-local overlap recovery for the closed cabin proxy colliders.
// Meta remains responsible for input, walking, slopes and gravity.
[DisallowMultipleComponent]
[DefaultExecutionOrder(10000)]
public sealed class CabinCollisionRecovery : MonoBehaviour
{
    [SerializeField] private Transform solidRoot;
    [SerializeField] private CapsuleCollider capsule;
    [SerializeField] private MetaCharacter character;
    [SerializeField] private MetaLocomotor locomotor;
    [SerializeField, Min(0.0001f)] private float separation = 0.002f;
    private readonly HashSet<Collider> solids = new HashSet<Collider>();
    private readonly Collider[] hits = new Collider[128];
    private int solidLayers;

    public void Configure(Transform root, CapsuleCollider body, MetaCharacter controller, MetaLocomotor mover)
    {
        solidRoot = root; capsule = body; character = controller; locomotor = mover;
        CacheSolids();
    }
    private void Awake() { CacheSolids(); }
    private void CacheSolids()
    {
        solids.Clear(); solidLayers = 0;
        if (solidRoot == null) return;
        foreach (BoxCollider collider in solidRoot.GetComponentsInChildren<BoxCollider>(true))
        {
            if (!collider.isTrigger)
            {
                solids.Add(collider);
                solidLayers |= 1 << collider.gameObject.layer;
            }
        }
    }
    private void LateUpdate()
    {
        if (capsule == null || character == null || locomotor == null || solidRoot == null ||
            !capsule.enabled || !character.isActiveAndEnabled || !locomotor.isActiveAndEnabled ||
            solids.Count == 0 || Time.timeScale == 0f) return;
        Vector3 position = capsule.transform.position;
        Quaternion rotation = capsule.transform.rotation;
        Vector3 scale = capsule.transform.lossyScale;
        float radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        float halfSegment = Mathf.Max(0f, capsule.height * Mathf.Abs(scale.y) * 0.5f - radius);
        Vector3 up = rotation * Vector3.up;
        Vector3 centerOffset = rotation * Vector3.Scale(capsule.center, scale);
        bool moved = false;
        for (int iteration = 0; iteration < 6; iteration++)
        {
            Vector3 center = position + centerOffset;
            int count = Physics.OverlapCapsuleNonAlloc(center - up * halfSegment, center + up * halfSegment,
                radius, hits, solidLayers, QueryTriggerInteraction.Ignore);
            float deepest = 0f;
            Vector3 correction = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                Collider obstacle = hits[i];
                if (obstacle == null || !solids.Contains(obstacle) || !obstacle.enabled || !obstacle.gameObject.activeInHierarchy) continue;
                if (Physics.ComputePenetration(capsule, position, rotation, obstacle,
                    obstacle.transform.position, obstacle.transform.rotation, out Vector3 direction, out float distance) &&
                    distance > 0.001f && distance > deepest)
                {
                    deepest = distance;
                    correction = direction * (distance + separation);
                }
            }
            if (deepest <= 0f) break;
            position += correction;
            moved = true;
        }
        if (!moved) return;
        character.SetPosition(position);
        locomotor.Velocity = Vector3.zero;
        locomotor.ResetPlayerToCharacter();
        Physics.SyncTransforms();
    }
}
