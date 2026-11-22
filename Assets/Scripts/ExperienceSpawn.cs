using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(OVRCameraRig))]
public sealed class ExperienceSpawn : MonoBehaviour
{
    [SerializeField, Tooltip("Punto de aparición del nivel. Si queda vacío, se usa la posición inicial del OVRCameraRig.")]
    private Transform spawnPoint;

    [SerializeField, Tooltip("Raíz del jugador que mueve el joystick. Asígnala si es un padre del OVRCameraRig.")]
    private Transform locomotionRoot;

    private OVRCameraRig rig;
    private Transform movementRoot;
    private Vector3 initialRigPosition;
    private float trackingReadyTime;
    private int anchorUpdates;
    private bool positioned;

    private void Awake()
    {
        rig = GetComponent<OVRCameraRig>();
        movementRoot = locomotionRoot != null ? locomotionRoot : transform;
        initialRigPosition = transform.position;

        if (movementRoot != transform && !transform.IsChildOf(movementRoot))
        {
            Debug.LogWarning("Locomotion Root debe ser el OVRCameraRig o un padre suyo.", this);
            movementRoot = transform;
        }
    }

    private void OnEnable()
    {
        if (positioned) return;

        anchorUpdates = 0;
        trackingReadyTime = Time.unscaledTime + 0.2f;
        rig.UpdatedAnchors += PlaceAtSpawn;
    }

    private void OnDisable()
    {
        rig.UpdatedAnchors -= PlaceAtSpawn;
    }

    private void PlaceAtSpawn(OVRCameraRig updatedRig)
    {
        Transform eye = updatedRig.centerEyeAnchor;
        if (eye == null) return;

        anchorUpdates++;
        if (anchorUpdates < 3 || Time.unscaledTime < trackingReadyTime) return;

        Vector3 destination = spawnPoint != null ? spawnPoint.position : initialRigPosition;
        Vector3 correction = destination - eye.position;
        // Align the eye horizontally without changing the level's floor or tracked eye height.
        correction.y = 0f;
        movementRoot.position += correction;

        positioned = true;
        updatedRig.UpdatedAnchors -= PlaceAtSpawn;
    }
}
