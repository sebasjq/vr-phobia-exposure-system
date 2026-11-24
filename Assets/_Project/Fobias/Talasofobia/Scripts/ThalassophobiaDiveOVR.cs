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

    [Header("Seabed collision (enable in the reef level)")]
    [Tooltip("Activa esta opcion en niveles con fondo. Desactivada conserva la sumersion original.")]
    [SerializeField] private bool stopAtSeabed;
    [Tooltip("Selecciona solo las capas del suelo y de los obstaculos del fondo. Excluye el agua y el jugador.")]
    [SerializeField] private LayerMask seabedLayers;
    [Tooltip("CenterEyeAnchor. Si queda vacio, se intenta obtener del OVRCameraRig.")]
    [SerializeField] private Transform head;
    [Tooltip("Radio de la comprobacion del suelo, en metros.")]
    [SerializeField, Min(0.01f)] private float probeRadius = 0.2f;
    [Tooltip("Separacion minima entre la base virtual del jugador y el suelo, en metros.")]
    [SerializeField, Min(0.001f)] private float floorClearance = 0.05f;

    private OVRCameraRig rig;
    private float currentDepth;
    private bool gripHeld;
    private bool warnedMissingLayers;
    private bool waitingForGripRelease;
    private float surfaceHeight;

    private void Awake()
    {
        rig = GetComponentInChildren<OVRCameraRig>();
        surfaceHeight = transform.position.y;
    }

    private void Update()
    {
        if (Time.timeScale == 0f) return;

        float grip = OVRInput.Get(
            OVRInput.Axis1D.PrimaryHandTrigger,
            OVRInput.Controller.RTouch);

        // A held grip from before a respawn must never start another dive.
        if (waitingForGripRelease)
        {
            if (grip <= releaseThreshold) waitingForGripRelease = false;
            return;
        }
        if (ExperienceTransition.IsBusy) return;

        // Hysteresis prevents small trigger changes from toggling the movement.
        gripHeld = gripHeld ? grip > releaseThreshold : grip >= pressThreshold;

        float targetDepth = gripHeld ? diveDepth : 0f;
        float speed = gripHeld ? descentSpeed : ascentSpeed;
        float nextDepth = Mathf.MoveTowards(currentDepth, targetDepth, speed * Time.deltaTime);
        float depthDelta = nextDepth - currentDepth;

        if (stopAtSeabed && depthDelta > 0f)
            depthDelta = LimitDescent(depthDelta);

        // Never ascend above the surface, even if another script teleported the rig
        // while this component still remembered its previous depth.
        if (depthDelta < 0f)
            depthDelta = -Mathf.Min(-depthDelta,
                Mathf.Max(0f, surfaceHeight - transform.position.y));

        // Move the entire rig. Never write to the tracked camera/CenterEyeAnchor.
        transform.position += Vector3.down * depthDelta;

        // Only accumulate movement actually applied. Holding the grip at the
        // bottom must not add depth that would cause an overshoot on ascent.
        currentDepth = Mathf.Max(0f, currentDepth + depthDelta);
        if (!gripHeld && transform.position.y >= surfaceHeight)
            currentDepth = 0f;
    }

    private float LimitDescent(float requestedDistance)
    {
        if (seabedLayers.value == 0)
        {
            if (!warnedMissingLayers)
            {
                Debug.LogWarning(
                    "Talasofobia: Stop At Seabed esta activo. Asigna Seabed Layers para permitir el descenso.", this);
                warnedMissingLayers = true;
            }
            return 0f;
        }

        if (head == null && rig != null)
            head = rig.centerEyeAnchor;

        float radius = Mathf.Max(0.01f, probeRadius);
        float clearance = Mathf.Max(0.001f, floorClearance);

        // With Floor Level tracking, the moving root defines the virtual floor.
        // Use the tracked head's X/Z so the probe stays below the user even
        // when they move physically away from the tracking origin.
        Vector3 origin = head != null ? head.position : transform.position;
        origin.y = transform.position.y + radius;

        // A sweep does not detect colliders already overlapping its sphere.
        // If overlapping, stop descending but still allow the normal ascent.
        if (Physics.CheckSphere(origin, radius, seabedLayers,
                QueryTriggerInteraction.Ignore))
            return 0f;

        if (Physics.SphereCast(origin, radius, Vector3.down,
                out RaycastHit hit, requestedDistance + clearance,
                seabedLayers, QueryTriggerInteraction.Ignore))
        {
            return Mathf.Clamp(hit.distance - clearance, 0f, requestedDistance);
        }

        return requestedDistance;
    }

    private void OnDisable()
    {
        float rise = Mathf.Min(currentDepth, Mathf.Max(0f, surfaceHeight - transform.position.y));
        transform.position += Vector3.up * rise;
        currentDepth = 0f;
        gripHeld = false;
        warnedMissingLayers = false;
        waitingForGripRelease = true;
    }

    // ExperienceSpawn invokes this AFTER restoring the rig's floor position.
    public void OnExperienceRespawn()
    {
        currentDepth = 0f;
        gripHeld = false;
        surfaceHeight = transform.position.y;
        waitingForGripRelease = true;
    }

    private void OnValidate()
    {
        releaseThreshold = Mathf.Min(releaseThreshold, pressThreshold - 0.01f);
    }
}
