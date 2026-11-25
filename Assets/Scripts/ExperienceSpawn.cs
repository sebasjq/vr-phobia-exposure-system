using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(OVRCameraRig))]
public sealed class ExperienceSpawn : MonoBehaviour
{
    [SerializeField, Tooltip("Punto de aparición a la altura del suelo virtual. Si queda vacío, se usa la posición inicial del OVRCameraRig.")]
    private Transform spawnPoint;

    [SerializeField, Tooltip("Raíz del jugador que mueve el joystick. Asígnala si es un padre del OVRCameraRig.")]
    private Transform locomotionRoot;

    private OVRCameraRig rig;
    private Transform movementRoot;
    private Vector3 initialRigPosition;
    private float trackingReadyTime;
    private int anchorUpdates;
    private bool positioned;
    private Oculus.Interaction.Locomotion.FirstPersonLocomotor[] locomotors;
    private bool captureGroundedHeight;
    private int stableGroundFrames;
    private float previousGroundedHeight;

    public bool CanRespawn => isActiveAndEnabled && positioned && movementRoot != null &&
        rig != null && rig.centerEyeAnchor != null;

    // Reuse the spawn without reloading the scene or changing the tracked eye height.
    public bool TryRespawn()
    {
        if (!CanRespawn) return false;

        CharacterController[] controllers = movementRoot.GetComponentsInChildren<CharacterController>();
        var enabledControllers = new System.Collections.Generic.List<CharacterController>();
        try
        {
            foreach (CharacterController controller in controllers)
            {
                if (!controller.enabled) continue;
                enabledControllers.Add(controller);
                controller.enabled = false;
            }

            Vector3 destination = spawnPoint != null ? spawnPoint.position : initialRigPosition;
            Vector3 correction = destination - rig.centerEyeAnchor.position;
            // Y belongs to the rig's virtual floor, never to the tracked headset.
            correction.y = destination.y - transform.position.y;
            movementRoot.position += correction;

            // The dive controller can move this rig or an ancestor. Reset each once,
            // after teleporting, so its surface height matches the recovered position.
            var diveControllers = new System.Collections.Generic.HashSet<ThalassophobiaDiveOVR>(
                movementRoot.GetComponentsInChildren<ThalassophobiaDiveOVR>(true));
            diveControllers.UnionWith(GetComponentsInParent<ThalassophobiaDiveOVR>(true));
            foreach (ThalassophobiaDiveOVR diveController in diveControllers)
                diveController.OnExperienceRespawn();

            foreach (Oculus.Interaction.Locomotion.FirstPersonLocomotor locomotor in locomotors)
                if (locomotor != null && locomotor.isActiveAndEnabled)
                    locomotor.Velocity = Vector3.zero;

            foreach (Rigidbody body in movementRoot.GetComponentsInChildren<Rigidbody>())
            {
                if (body.isKinematic) continue;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            Physics.SyncTransforms();
            return true;
        }
        finally
        {
            foreach (CharacterController controller in enabledControllers)
                if (controller != null) controller.enabled = true;
        }
    }

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

        locomotors = movementRoot.GetComponentsInChildren<
            Oculus.Interaction.Locomotion.FirstPersonLocomotor>(true);
        // Walking scenes can start with the rig above the floor in the editor.
        // Keep the original X/Z, but remember Y only after Meta has grounded it.
        // Diving scenes retain their original surface height, never the seabed.
        bool usesDiving = movementRoot.GetComponentsInChildren<ThalassophobiaDiveOVR>(true).Length > 0 ||
            GetComponentInParent<ThalassophobiaDiveOVR>() != null;
        captureGroundedHeight = spawnPoint == null && !usesDiving && locomotors.Length > 0;
        previousGroundedHeight = transform.position.y;
    }

    private void LateUpdate()
    {
        if (!captureGroundedHeight || !positioned || Time.timeScale == 0f) return;

        bool grounded = false;
        foreach (Oculus.Interaction.Locomotion.FirstPersonLocomotor locomotor in locomotors)
        {
            if (locomotor != null && locomotor.isActiveAndEnabled && locomotor.IsGrounded)
            {
                grounded = true;
                break;
            }
        }
        if (!grounded)
        {
            stableGroundFrames = 0;
            return;
        }

        float height = transform.position.y;
        if (Mathf.Abs(height - previousGroundedHeight) > 0.001f)
            stableGroundFrames = 0;
        previousGroundedHeight = height;
        stableGroundFrames++;
        if (stableGroundFrames < 3) return;

        initialRigPosition.y = height;
        captureGroundedHeight = false;
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

