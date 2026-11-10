using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class CircularMenu : MonoBehaviour
{
    [System.Serializable]
    public class MenuView
    {
        public Transform root;
        public CanvasGroup canvasGroup;
        public Renderer glassRenderer;
        [HideInInspector] public Button[] buttons;
        [HideInInspector] public bool[] originalInteractable;
        [System.NonSerialized] public MaterialPropertyBlock glassProperties;
        public Behaviour rayInteractable;
        public Behaviour pokeInteractable;
    }

    [SerializeField, Min(0.05f)] private float duration = 1.2f;
    [SerializeField] private float[] targetAngles = { 0f, 120f, -120f };
    [SerializeField] private MenuView[] views = new MenuView[4];
    private static readonly int FadeId = Shader.PropertyToID("_Fade");
    private Quaternion initialRotation;
    private int currentView;
    private bool isMoving;
    private const int ExposureIndex = 3;

    [Header("Transición al menú de exposición")]
    [SerializeField] private Transform viewer;
    [Tooltip("OVRCameraRig de esta escena. Recupera la altura y el giro horizontal al regresar.")]
    [SerializeField] private OVRCameraRig playerRig;
    [SerializeField, Min(0f)] private float entryDistance = 1f;
    private Vector3 exposureFinalPosition;
    private bool exposureOpen;
    private bool restoreHeightPending;
    private float restoreHeightReadyTime;
    private int anchorUpdateCount;
    // Session state belongs to the menu and survives scene loads.
    private static Vector3? savedMenuForward;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetMenuOrientation()
    {
        savedMenuForward = null;
    }

    [Header("Identificación opcional de la fobia")]
    [SerializeField] private string exposureId = "";
    [Tooltip("Optional: restores the content of a shared levels panel using the phobia ID.")]
    [SerializeField] private UnityEvent<string> onExposureSelected = new UnityEvent<string>();

    private void Start()
    {
        if (playerRig == null && viewer != null)
            playerRig = viewer.GetComponentInParent<OVRCameraRig>();
        if (viewer == null && playerRig != null)
            viewer = playerRig.centerEyeAnchor;
        initialRotation = transform.localRotation;
        Vector3 startPosition = views[ExposureIndex].root.position;
        Vector3 direction = viewer != null ? startPosition - viewer.position : transform.forward;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) direction = Vector3.forward;
        direction.Normalize();
        exposureFinalPosition = startPosition - direction * entryDistance;

        foreach (MenuView view in views)
        {
            view.buttons = view.root.GetComponentsInChildren<Button>(true);
            view.originalInteractable = new bool[view.buttons.Length];
            view.glassProperties = new MaterialPropertyBlock();
            for (int i = 0; i < view.buttons.Length; i++)
                view.originalInteractable[i] = view.buttons[i].interactable;
        }

        currentView = 0;
        bool returning = ExperienceNavigation.ConsumeReturn(gameObject.scene);
        if (returning)
        {
            int savedView = ExperienceNavigation.MenuView;
            if (savedView >= 0 && savedView < views.Length &&
                savedView < targetAngles.Length && savedView != ExposureIndex)
                currentView = savedView;
            transform.localRotation = initialRotation * Quaternion.Euler(0f, targetAngles[currentView], 0f);
            exposureId = ExperienceNavigation.ExposureId;
            exposureOpen = true;
            views[ExposureIndex].root.position = exposureFinalPosition;
            if ((ExperienceNavigation.MenuEyeHeight.HasValue || savedMenuForward.HasValue) && playerRig != null)
            {
                restoreHeightPending = true;
                restoreHeightReadyTime = Time.unscaledTime + 0.2f;
                anchorUpdateCount = 0;
                playerRig.UpdatedAnchors += RestoreMenuEyeHeight;
            }
        }

        // A transparent panel can still leave Meta surfaces/colliders in the ray path.
        // Keep the entire exposure hierarchy inactive whenever it is closed.
        views[ExposureIndex].root.gameObject.SetActive(returning);
        int visibleView = returning ? ExposureIndex : currentView;
        for (int i = 0; i < views.Length; i++)
        {
            SetVisibility(i, i == visibleView ? 1f : 0f);
            SetInteraction(i, i == visibleView);
        }
        // Invoke after initializing all menu state. This callback should only select content.
        if (returning && !string.IsNullOrEmpty(exposureId))
            onExposureSelected.Invoke(exposureId);
    }

    // Connect every level card to this method instead of directly loading a scene.
    public void LoadExperience(string sceneName)
    {
        if (isMoving || !exposureOpen) return;
        Transform eye = playerRig != null ? playerRig.centerEyeAnchor : viewer;
        if (eye == null)
            Debug.LogWarning("Asigna Player Rig en CircularMenu para conservar la altura al volver.", this);
        savedMenuForward = null;
        if (eye != null)
        {
            Vector3 horizontalForward = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            if (horizontalForward.sqrMagnitude > 0.0001f)
                savedMenuForward = horizontalForward.normalized;
        }
        ExperienceNavigation.Enter(sceneName, gameObject.scene.path, currentView, exposureId,
            eye != null ? (float?)eye.position.y : null);
    }

    private void RestoreMenuEyeHeight(OVRCameraRig rig)
    {
        if (!restoreHeightPending || rig.centerEyeAnchor == null) return;
        anchorUpdateCount++;
        // Allow the new rig's tracking origin and eye pose to initialize first.
        if (anchorUpdateCount < 3 || Time.unscaledTime < restoreHeightReadyTime) return;
        Transform eye = rig.centerEyeAnchor;
        if (savedMenuForward.HasValue)
        {
            Vector3 currentForward = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            if (currentForward.sqrMagnitude > 0.0001f)
            {
                float yawDifference = Vector3.SignedAngle(currentForward, savedMenuForward.Value, Vector3.up);
                // Rotate around the eye, not the rig origin: preserve the eye's world position.
                // Only change yaw; tracked head pitch and roll remain under tracking control.
                rig.transform.RotateAround(eye.position, Vector3.up, yawDifference);
            }
        }
        if (ExperienceNavigation.MenuEyeHeight.HasValue)
        {
            float heightDifference = ExperienceNavigation.MenuEyeHeight.Value - eye.position.y;
            rig.transform.position += Vector3.up * heightDifference;
        }
        savedMenuForward = null;
        restoreHeightPending = false;
        rig.UpdatedAnchors -= RestoreMenuEyeHeight;
    }

    private void OnDisable()
    {
        if (playerRig != null) playerRig.UpdatedAnchors -= RestoreMenuEyeHeight;
        restoreHeightPending = false;
    }

    // Optional alternative to OpenExposure for several phobias sharing one levels panel.
    public void OpenExposureFor(string id)
    {
        if (isMoving || exposureOpen) return;
        exposureId = id;
        onExposureSelected.Invoke(exposureId);
        OpenExposure();
    }

    public void GoTo(int viewIndex)
    {
        if (isMoving || exposureOpen || viewIndex < 0 || viewIndex >= views.Length ||
            viewIndex >= targetAngles.Length || viewIndex == currentView) return;
        StartCoroutine(RotateTo(viewIndex));
    }

    private void SetVisibility(int index, float alpha)
    {
        MenuView view = views[index];
        view.canvasGroup.alpha = alpha;
        if (view.glassRenderer == null) return;
        view.glassRenderer.GetPropertyBlock(view.glassProperties);
        view.glassProperties.SetFloat(FadeId, alpha);
        view.glassRenderer.SetPropertyBlock(view.glassProperties);
    }

    private void SetInteraction(int index, bool enabled)
    {
        MenuView view = views[index];
        view.canvasGroup.interactable = enabled;
        view.canvasGroup.blocksRaycasts = enabled;
        for (int i = 0; i < view.buttons.Length; i++)
        {
            if (view.buttons[i] != null)
                view.buttons[i].interactable = enabled && view.originalInteractable[i];
        }
        if (view.rayInteractable != null) view.rayInteractable.enabled = enabled;
        if (view.pokeInteractable != null) view.pokeInteractable.enabled = enabled;
    }

    private IEnumerator RotateTo(int nextView)
    {
        isMoving = true;
        for (int i = 0; i < views.Length; i++) SetInteraction(i, false);
        int outgoingView = currentView;
        Quaternion startRotation = transform.localRotation;
        Quaternion targetRotation = initialRotation * Quaternion.Euler(0f, targetAngles[nextView], 0f);
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float easedProgress = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
            transform.localRotation = Quaternion.Slerp(startRotation, targetRotation, easedProgress);
            SetVisibility(outgoingView, 1f - easedProgress);
            SetVisibility(nextView, easedProgress);
            yield return null;
        }
        transform.localRotation = targetRotation;
        currentView = nextView;
        for (int i = 0; i < views.Length; i++)
        {
            SetVisibility(i, i == currentView ? 1f : 0f);
            SetInteraction(i, i == currentView);
        }
        isMoving = false;
    }

    public void OpenExposure()
    {
        if (isMoving || exposureOpen) return;
        StartCoroutine(TransitionExposure(true));
    }

    public void CloseExposure()
    {
        if (isMoving || !exposureOpen) return;
        StartCoroutine(TransitionExposure(false));
    }

    private IEnumerator TransitionExposure(bool opening)
    {
        isMoving = true;
        for (int i = 0; i < views.Length; i++) SetInteraction(i, false);
        Transform exposureRoot = views[ExposureIndex].root;
        Vector3 direction = viewer != null ? exposureFinalPosition - viewer.position : transform.forward;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) direction = Vector3.forward;
        direction.Normalize();
        Vector3 farPosition = exposureFinalPosition + direction * entryDistance;
        int outgoingIndex = opening ? currentView : ExposureIndex;
        int incomingIndex = opening ? ExposureIndex : currentView;
        for (int i = 0; i < views.Length; i++)
            SetVisibility(i, i == outgoingIndex ? 1f : 0f);
        exposureRoot.position = opening ? farPosition : exposureFinalPosition;
        if (opening)
        {
            exposureRoot.gameObject.SetActive(true);
            SetInteraction(ExposureIndex, false);
        }
        float delay = duration * 0.35f;
        float totalDuration = duration + delay;
        float elapsed = 0f;
        while (elapsed < totalDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float outgoingEased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
            float incomingEased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((elapsed - delay) / duration));
            SetVisibility(outgoingIndex, 1f - outgoingEased);
            SetVisibility(incomingIndex, incomingEased);
            exposureRoot.position = opening
                ? Vector3.Lerp(farPosition, exposureFinalPosition, incomingEased)
                : Vector3.Lerp(exposureFinalPosition, farPosition, outgoingEased);
            yield return null;
        }
        SetVisibility(outgoingIndex, 0f);
        SetVisibility(incomingIndex, 1f);
        exposureRoot.position = exposureFinalPosition;
        exposureOpen = opening;
        if (!opening)
            exposureRoot.gameObject.SetActive(false);
        SetInteraction(incomingIndex, true);
        isMoving = false;
    }
}
