using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class CircularMenu : MonoBehaviour
{
    [System.Serializable]
    public class MenuView
    {
        [Tooltip("ID opcional del panel de niveles. Si está vacío, se usa el nombre de Root al seleccionar el panel directamente.")]
        public string exposureId;
        public Transform root;
        public CanvasGroup canvasGroup;
        public Renderer glassRenderer;
        [HideInInspector] public Button[] buttons;
        [HideInInspector] public bool[] originalInteractable;
        [System.NonSerialized] public MaterialPropertyBlock glassProperties;
        public Behaviour rayInteractable;
        public Behaviour pokeInteractable;
        [System.NonSerialized] public Vector3 exposureStartPosition;
        [System.NonSerialized] public Vector3 exposureFinalPosition;
        [System.NonSerialized] public Renderer[] hiddenRenderers;
        [System.NonSerialized] public Collider[] hiddenColliders;
    }

    [SerializeField, Min(0.05f)] private float duration = 1.2f;
    [SerializeField] private float[] targetAngles = { 0f, 120f, -120f };
    [SerializeField] private MenuView[] views = new MenuView[3];

    [Header("Paneles de niveles por fobia")]
    [SerializeField] private MenuView[] exposurePanels = new MenuView[0];

    private MenuView[] allViews;
    private static readonly int FadeId = Shader.PropertyToID("_Fade");
    private Quaternion initialRotation;
    private int currentView;
    private bool isMoving;
    private const int FirstExposureIndex = 3;
    private int activeExposureIndex;

    [Header("Transición al menú de exposición")]
    [SerializeField] private Transform viewer;
    [Tooltip("OVRCameraRig de esta escena. Recupera la posición y el giro horizontal al regresar.")]
    [SerializeField] private OVRCameraRig playerRig;
    [SerializeField, Min(0f)] private float entryDistance = 1f;
    [Tooltip("Punto de partida común para los paneles de niveles. Si queda vacío, cada panel usa su propio Transform.")]
    [SerializeField] private Transform exposurePlacement;
    private string activeExposureId;
    private bool exposureOpen;
    private bool restorePosePending;
    private float restorePoseReadyTime;
    private int anchorUpdateCount;
    // Session state belongs to the menu and survives scene loads.
    private static Vector3? savedMenuEyePosition;
    private static Vector3? savedMenuForward;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetMenuPose()
    {
        savedMenuEyePosition = null;
        savedMenuForward = null;
    }

    [Header("Identificación opcional para el panel predeterminado")]
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
        allViews = BuildViewList();
        bool useSharedPlacement = exposurePlacement != null;
        Vector3 sharedStartPosition = useSharedPlacement ? exposurePlacement.position : Vector3.zero;
        Quaternion sharedRotation = useSharedPlacement ? exposurePlacement.rotation : Quaternion.identity;
        for (int i = 0; i < allViews.Length; i++)
        {
            MenuView view = allViews[i];
            if (view == null || view.root == null || view.canvasGroup == null)
            {
                string field = exposurePanels.Length > 0 && i >= FirstExposureIndex
                    ? $"Exposure Panels > Element {i - FirstExposureIndex}"
                    : $"Views > Element {i}";
                Debug.LogWarning($"Configura Root y Canvas Group en {field}.", this);
                if (i >= FirstExposureIndex && view?.root != null)
                    view.root.gameObject.SetActive(false);
                continue;
            }
            view.buttons = view.root.GetComponentsInChildren<Button>(true);
            view.originalInteractable = new bool[view.buttons.Length];
            view.glassProperties = new MaterialPropertyBlock();
            for (int j = 0; j < view.buttons.Length; j++)
                view.originalInteractable[j] = view.buttons[j].interactable;

            if (i >= FirstExposureIndex)
            {
                view.exposureStartPosition = useSharedPlacement ? sharedStartPosition : view.root.position;
                view.exposureFinalPosition = view.exposureStartPosition
                    - GetEntryDirection(view.exposureStartPosition) * entryDistance;
                if (useSharedPlacement)
                    view.root.SetPositionAndRotation(sharedStartPosition, sharedRotation);
            }
        }

        currentView = 0;
        activeExposureIndex = -1;
        bool returning = ExperienceNavigation.ConsumeReturn(gameObject.scene);
        if (returning)
        {
            activeExposureId = ExperienceNavigation.ExposureId;
            activeExposureIndex = FindExposureView(activeExposureId);
            if (activeExposureIndex >= 0 && string.IsNullOrEmpty(activeExposureId))
                activeExposureId = GetDefaultExposureId();

            if (activeExposureIndex < 0)
            {
                Debug.LogWarning($"No hay un panel de niveles asignado al ID '{activeExposureId}'.", this);
                if (exposurePanels.Length == 0 && IsViewReady(FirstExposureIndex))
                {
                    activeExposureIndex = FirstExposureIndex;
                    activeExposureId = GetDefaultExposureId();
                }
            }

            int savedView = ExperienceNavigation.MenuView;
            if (savedView >= 0 && savedView < FirstExposureIndex &&
                savedView < targetAngles.Length && IsViewReady(savedView))
                currentView = savedView;
            transform.localRotation = initialRotation * Quaternion.Euler(0f, targetAngles[currentView], 0f);
            exposureOpen = activeExposureIndex >= 0;
            if (exposureOpen)
            {
                MenuView exposureView = allViews[activeExposureIndex];
                exposureView.root.position = exposureView.exposureFinalPosition;
            }
            if (playerRig != null && (savedMenuEyePosition.HasValue ||
                savedMenuForward.HasValue || ExperienceNavigation.MenuEyeHeight.HasValue))
            {
                restorePosePending = true;
                restorePoseReadyTime = Time.unscaledTime + 0.2f;
                anchorUpdateCount = 0;
                playerRig.UpdatedAnchors += RestoreMenuPose;
            }
        }

        int visibleView = exposureOpen ? activeExposureIndex : currentView;
        for (int i = 0; i < allViews.Length; i++)
        {
            bool visible = i == visibleView;
            SetVisibility(i, visible ? 1f : 0f);
            SetInteraction(i, visible);
            if (i >= FirstExposureIndex && !visible && IsViewReady(i))
                allViews[i].root.gameObject.SetActive(false);
        }

        if (exposureOpen)
        {
            allViews[activeExposureIndex].root.gameObject.SetActive(true);
            Canvas.ForceUpdateCanvases();
            if (!string.IsNullOrEmpty(activeExposureId))
                onExposureSelected.Invoke(activeExposureId);
        }
    }

    private MenuView[] BuildViewList()
    {
        if (exposurePanels.Length == 0) return views;

        var combined = new MenuView[FirstExposureIndex + exposurePanels.Length];
        System.Array.Copy(views, combined, Mathf.Min(views.Length, FirstExposureIndex));
        System.Array.Copy(exposurePanels, 0, combined, FirstExposureIndex, exposurePanels.Length);
        return combined;
    }

    // Connect every level card to this method instead of directly loading a scene.
    public void LoadExperience(string sceneName)
    {
        if (isMoving || !exposureOpen) return;
        Transform eye = playerRig != null ? playerRig.centerEyeAnchor : viewer;
        if (eye == null)
            Debug.LogWarning("Asigna Player Rig en CircularMenu para conservar la posición al volver.", this);

        savedMenuEyePosition = eye != null ? (Vector3?)eye.position : null;
        savedMenuForward = null;
        if (eye != null)
        {
            Vector3 horizontalForward = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            if (horizontalForward.sqrMagnitude > 0.0001f)
                savedMenuForward = horizontalForward.normalized;
        }
#if UNITY_EDITOR
        Debug.Log($"[CircularMenu] Cargando nivel '{sceneName}' desde '{gameObject.scene.name}'.", this);
#endif
        ExperienceNavigation.Enter(sceneName, gameObject.scene.path, currentView, activeExposureId,
            savedMenuEyePosition?.y);
    }

    private void RestoreMenuPose(OVRCameraRig rig)
    {
        if (!restorePosePending || rig.centerEyeAnchor == null) return;
        anchorUpdateCount++;
        // Allow the new rig's tracking origin and eye pose to initialize first.
        if (anchorUpdateCount < 3 || Time.unscaledTime < restorePoseReadyTime) return;
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
        if (savedMenuEyePosition.HasValue)
        {
            // Offset the rig by the difference between the tracked eye and its saved menu position.
            rig.transform.position += savedMenuEyePosition.Value - eye.position;
        }
        else if (ExperienceNavigation.MenuEyeHeight.HasValue)
        {
            float heightDifference = ExperienceNavigation.MenuEyeHeight.Value - eye.position.y;
            rig.transform.position += Vector3.up * heightDifference;
        }

        ResetMenuPose();
        restorePosePending = false;
        rig.UpdatedAnchors -= RestoreMenuPose;
    }

    private void OnDisable()
    {
        if (playerRig != null) playerRig.UpdatedAnchors -= RestoreMenuPose;
        restorePosePending = false;
    }

    public void OpenExposureFor(string id)
    {
        OpenExposureView(id, notifySelection: true);
    }

    public void OpenExposurePanel(GameObject panel)
    {
        if (isMoving || exposureOpen) return;

        for (int i = FirstExposureIndex; i < allViews.Length; i++)
        {
            if (!IsViewReady(i) || allViews[i].root.gameObject != panel) continue;

            string id = GetPanelId(allViews[i]);
            if (FindExposureView(id) != i)
            {
                Debug.LogWarning($"El panel '{panel.name}' necesita un Exposure Id único.", this);
                return;
            }

            BeginExposure(i, id, notifySelection: true);
            return;
        }

        Debug.LogWarning("Asigna el Root de un panel registrado en Exposure Panels.", this);
    }

    private void OpenExposureView(string id, bool notifySelection)
    {
        if (isMoving || exposureOpen) return;

        int index = FindExposureView(id);
        if (index < 0)
        {
            Debug.LogWarning($"No hay un panel de niveles asignado al ID '{id}'.", this);
            return;
        }

        BeginExposure(index, string.IsNullOrEmpty(id) ? GetDefaultExposureId() : id, notifySelection);
    }

    private void BeginExposure(int index, string id, bool notifySelection)
    {
        activeExposureIndex = index;
        activeExposureId = id;
#if UNITY_EDITOR
        Debug.Log($"[CircularMenu] Abriendo panel '{allViews[index].root.name}' en '{gameObject.scene.name}'.", this);
#endif
        if (notifySelection) onExposureSelected.Invoke(activeExposureId);
        StartCoroutine(TransitionExposure(true));
    }

    private int FindExposureView(string id)
    {
        if (string.IsNullOrEmpty(id))
            return IsViewReady(FirstExposureIndex) ? FirstExposureIndex : -1;

        int match = -1;
        for (int i = FirstExposureIndex; i < allViews.Length; i++)
        {
            if (!IsViewReady(i) || GetPanelId(allViews[i]) != id) continue;
            if (match >= 0) return -1;
            match = i;
        }

        if (match >= 0) return match;

        // Compatibilidad con escenas que usan un panel compartido y el evento.
        if (exposurePanels.Length == 0 && allViews.Length == FirstExposureIndex + 1 &&
            IsViewReady(FirstExposureIndex) &&
            string.IsNullOrEmpty(allViews[FirstExposureIndex].exposureId))
            return FirstExposureIndex;

        return -1;
    }

    private static string GetPanelId(MenuView view)
    {
        return string.IsNullOrEmpty(view.exposureId) ? view.root.name : view.exposureId;
    }

    private string GetDefaultExposureId()
    {
        MenuView panel = allViews[FirstExposureIndex];
        if (exposurePanels.Length == 0 && string.IsNullOrEmpty(panel.exposureId))
            return exposureId;

        return GetPanelId(panel);
    }

    private bool IsViewReady(int index)
    {
        return index >= 0 && index < allViews.Length && allViews[index]?.buttons != null;
    }

    private Vector3 GetEntryDirection(Vector3 position)
    {
        Vector3 direction = viewer != null ? position - viewer.position : transform.forward;
        direction.y = 0f;
        return direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
    }

    public void GoTo(int viewIndex)
    {
        if (isMoving || exposureOpen || !IsViewReady(viewIndex) || viewIndex >= FirstExposureIndex ||
            viewIndex >= targetAngles.Length || viewIndex == currentView) return;
        StartCoroutine(RotateTo(viewIndex));
    }

    private void SetVisibility(int index, float alpha)
    {
        if (!IsViewReady(index)) return;
        MenuView view = allViews[index];
        view.canvasGroup.alpha = alpha;
        if (view.glassRenderer == null) return;
        view.glassRenderer.GetPropertyBlock(view.glassProperties);
        view.glassProperties.SetFloat(FadeId, alpha);
        view.glassRenderer.SetPropertyBlock(view.glassProperties);
    }

    private void SetInteraction(int index, bool enabled)
    {
        if (!IsViewReady(index)) return;
        MenuView view = allViews[index];
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
        for (int i = 0; i < allViews.Length; i++) SetInteraction(i, false);
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
        for (int i = 0; i < allViews.Length; i++)
        {
            SetVisibility(i, i == currentView ? 1f : 0f);
            SetInteraction(i, i == currentView);
        }
        isMoving = false;
    }

    public void OpenExposure()
    {
        OpenExposureView(string.Empty, notifySelection: false);
    }

    public void CloseExposure()
    {
        if (isMoving || !exposureOpen) return;
        StartCoroutine(TransitionExposure(false));
    }

    private IEnumerator TransitionExposure(bool opening)
    {
        isMoving = true;
        for (int i = 0; i < allViews.Length; i++) SetInteraction(i, false);
        MenuView exposureView = allViews[activeExposureIndex];
        Transform exposureRoot = exposureView.root;
        Vector3 exposureFinalPosition = exposureView.exposureFinalPosition;
        Vector3 farPosition = exposureView.exposureStartPosition;
        int outgoingIndex = opening ? currentView : activeExposureIndex;
        int incomingIndex = opening ? activeExposureIndex : currentView;
        for (int i = 0; i < allViews.Length; i++)
            SetVisibility(i, i == outgoingIndex ? 1f : 0f);
        exposureRoot.position = opening ? farPosition : exposureFinalPosition;
        if (opening)
        {
            exposureRoot.gameObject.SetActive(true);
            RestoreExposureSurfaces(exposureView);
            SetInteraction(activeExposureIndex, false);
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
        if (opening)
        {
            Canvas.ForceUpdateCanvases();
        }
        else
        {
            HideExposureSurfaces(exposureView);
            exposureRoot.position = farPosition;
            activeExposureIndex = -1;
            activeExposureId = null;
        }
        SetInteraction(incomingIndex, true);
        isMoving = false;
    }

    private static void HideExposureSurfaces(MenuView view)
    {
        // Keep Meta's canvas and render texture alive between openings.
        var renderers = new System.Collections.Generic.List<Renderer>();
        foreach (Renderer renderer in view.root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer.forceRenderingOff) continue;
            renderer.forceRenderingOff = true;
            renderers.Add(renderer);
        }
        view.hiddenRenderers = renderers.ToArray();

        var colliders = new System.Collections.Generic.List<Collider>();
        foreach (Collider collider in view.root.GetComponentsInChildren<Collider>(true))
        {
            if (!collider.enabled) continue;
            collider.enabled = false;
            colliders.Add(collider);
        }
        view.hiddenColliders = colliders.ToArray();
    }

    private static void RestoreExposureSurfaces(MenuView view)
    {
        if (view.hiddenRenderers != null)
        {
            foreach (Renderer renderer in view.hiddenRenderers)
            {
                if (renderer != null) renderer.forceRenderingOff = false;
            }
            view.hiddenRenderers = null;
        }

        if (view.hiddenColliders != null)
        {
            foreach (Collider collider in view.hiddenColliders)
            {
                if (collider != null) collider.enabled = true;
            }
            view.hiddenColliders = null;
        }
    }
}
