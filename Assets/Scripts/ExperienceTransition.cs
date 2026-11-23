using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using UnityEngine.UI;

// One persistent, head-following world-space Canvas for both VR eyes.
[DisallowMultipleComponent]
[DefaultExecutionOrder(10000)]
public sealed class ExperienceTransition : MonoBehaviour
{
    // Never add the shared UI layer to the headset camera: Meta uses it for flat source canvases.
    private int transitionUiLayer = -1;
    private int transitionUiMask;
    [Header("Fundidos (segundos)")]
    [SerializeField, Min(0f)] private float fadeOutDuration = 0.65f;
    [Tooltip("Fundido desde el clic del nivel, sin esperar a que termine el audio de UI.")]
    [SerializeField, Range(0.05f, 0.3f)] private float entryFadeOutDuration = 0.15f;
    [SerializeField, Min(0f)] private float fadeInDuration = 0.8f;
    [SerializeField, Min(0f)] private float cancelDuration = 0.15f;
    [Tooltip("Tiempo mínimo total de la pantalla de carga, además de sus fases de progreso.")]
    [SerializeField, Min(0f)] private float minimumLoadingDuration = 0.6f;
    [Tooltip("Tiempo mínimo de preparación DESPUÉS de activar la escena detrás del negro.")]
    [SerializeField, Min(0f)] private float sceneSettleDuration = 1.2f;

    [Header("Pausas a negro (segundos)")]
    [Tooltip("Negro antes de mostrar la barra y después de completarla.")]
    [SerializeField, Min(0f)] private float entryBlackPause = 0.6f;
    [Tooltip("Negro antes de cargar el menú y antes de revelarlo.")]
    [SerializeField, Min(0f)] private float exitBlackPause = 0.8f;

    [Header("Barra de entrada (progreso visual)")]
    [Tooltip("Tiempo para avanzar suavemente de 0 a 25 %. Espera allí si la escena aún no está lista.")]
    [SerializeField, Min(0.1f)] private float initialFillDuration = 1.8f;
    [Tooltip("Tiempo para pasar de 25 a 100 % después de preparar la escena.")]
    [SerializeField, Min(0.1f)] private float completionFillDuration = 0.9f;
    [SerializeField, Min(0f)] private float completedBarHoldDuration = 0.25f;

    [Header("Preparación de la escena")]
    [SerializeField, Min(3)] private int minimumPreparationFrames = 8;
    [Tooltip("Tiempo sin mipmaps pendientes antes de completar la barra, si hay texture streaming.")]
    [SerializeField, Min(0f)] private float textureQuietDuration = 0.3f;
    [Tooltip("Espera máxima adicional de streaming. Si se agota, avisa en Console y continúa.")]
    [SerializeField, Min(0.1f)] private float textureWaitTimeout = 10f;

    [Header("Audio de UI antes de cargar")]
    [Tooltip("Opcional. Si está vacío, busca UIAudioManager automáticamente.")]
    [SerializeField] private MonoBehaviour uiAudioManager;
    [Tooltip("Deja terminar el clic sin retrasar el fade visual. Los demás sonidos salientes se detienen y la escena nueva se carga después en silencio.")]
    [FormerlySerializedAs("uiAudioFinishTimeout")]
    [SerializeField, Range(1f, 3f)] private float uiClickGraceDuration = 2f;

    [Header("Audio de la escena visible")]
    [Tooltip("Subida del ambiente después de retirar por completo el negro y renderizar la escena visible. Nunca suena durante la barra o la pausa negra.")]
    [SerializeField, Min(0.1f)] private float sceneAudioFadeInDuration = 0.25f;

    [Header("Aspecto")]
    [SerializeField] private Color barColor = new Color(0.85f, 0.85f, 0.85f, 1f);
    [SerializeField] private string loadingText = "Cargando Experiencia...";
    [SerializeField] private string leavingText = "Abandonando Experiencia...";
    [SerializeField, Min(0.6f)] private float panelDistance = 1f;

    [Header("Mandos durante la transición")]
    [Tooltip("Mantiene visibles los modelos de manos y mandos sobre el fondo negro y las barras. Requiere URP.")]
    [SerializeField] private bool showControllersOverBlack = true;
    [Tooltip("Opcional: raíces de modelos de mandos fuera del rig. La detección de OVRCameraRig y ControllerVisual es automática.")]
    [SerializeField] private Transform[] controllerVisualRoots = new Transform[0];

    private enum State { Idle, Holding, Loading }
    private static ExperienceTransition instance;
    public static bool IsBusy => instance != null && instance.state == State.Loading;

    private State state;
    private Canvas canvas;
    private RectTransform canvasRect;
    private Image background;
    private GameObject content;
    private Text title;
    private RectTransform barFill;
    private Material overlayMaterial;
    private Camera viewerCamera;
    private OVRCameraRig viewerRig;
    private Transform viewerAnchor;
    private float nextViewerSearchTime;
    private Coroutine cancelRoutine;
    private bool keepPreparationBlack;
    private bool renderSceneForPreparation;
    private bool audioHeld;
    private bool revealingAudio;
    private int viewerRenderedFrames;
    private int lastViewerRenderedFrame = -1;
    private int fullyVisibleRenderedFrames;
    private bool awaitingSceneVisibility;
    private float savedListenerVolume;
    private FMOD.Studio.Bus masterAudioBus;
    private bool fmodAudioCaptured;
    private float savedFmodVolume;
    private bool savedFmodMuted;
    private float nextFmodCaptureTime;
    private FMOD.ChannelGroup coreMasterAudio;
    private bool coreMasterCaptured;
    private float savedCoreVolume;
    private bool savedCoreMuted;
    private float nextCoreCaptureTime;
    private float nextVisualRootDiscovery;
    private readonly List<Transform> detectedVisualRoots = new List<Transform>();
    private readonly List<Renderer> visualRendererBuffer = new List<Renderer>();
    private readonly Dictionary<Renderer, uint> savedControllerRenderingLayers = new Dictionary<Renderer, uint>();
    private uint controllerRenderingLayer;
    private uint controllerObjectLayers;
    private ControllerOverlayPass controllerOverlayPass;
    private bool controllerLayerWarningShown;
    private readonly Dictionary<Camera, CameraVisualState> savedCameraVisuals =
        new Dictionary<Camera, CameraVisualState>();

    private struct CameraVisualState
    {
        public int cullingMask;
        public CameraClearFlags clearFlags;
        public Color backgroundColor;
        public UniversalAdditionalCameraData urpData;
        public bool renderPostProcessing;
    }
    private readonly List<FMOD.Studio.EventDescription> uiAudioEvents =
        new List<FMOD.Studio.EventDescription>();
    private readonly List<AudioSource> uiAudioSources = new List<AudioSource>();
    private readonly List<FMOD.Studio.EventDescription> outgoingAudioEvents = new List<FMOD.Studio.EventDescription>();
    private readonly List<AudioSource> outgoingAudioSources = new List<AudioSource>();

    private struct AudioBypassState
    {
        public bool ignoreVolume;
    }
    private readonly Dictionary<AudioSource, AudioBypassState> audioBypassStates =
        new Dictionary<AudioSource, AudioBypassState>();
    private bool Ready => canvas != null && overlayMaterial != null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState() { instance = null; }

    private static ExperienceTransition GetOrCreate()
    {
        if (instance != null) return instance;
        instance = FindFirstObjectByType<ExperienceTransition>();
        if (instance == null)
            instance = new GameObject("ExperienceTransition").AddComponent<ExperienceTransition>();
        return instance;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        // Put this component on an otherwise empty root object.
        transform.SetParent(null, true);
        transform.localScale = Vector3.one;
        DontDestroyOnLoad(gameObject);
        BuildUI();
        SceneManager.sceneLoaded += OnSceneLoaded;
        Application.onBeforeRender += FollowCamera;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        Camera.onPreCull += OnLegacyCameraPreCull;
        Camera.onPostRender += OnLegacyCameraPostRender;
    }

    private void OnDestroy()
    {
        RestoreAudio();
        RestoreControllerRenderingLayers();
        controllerOverlayPass?.Dispose();
        RestoreCameraVisuals();
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Application.onBeforeRender -= FollowCamera;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        Camera.onPreCull -= OnLegacyCameraPreCull;
        Camera.onPostRender -= OnLegacyCameraPostRender;
        if (overlayMaterial != null) Destroy(overlayMaterial);
        if (instance == this) instance = null;
    }

    public static void ShowExitProgress(float progress)
    {
        ExperienceTransition transition = GetOrCreate();
        if (!transition.Ready || IsBusy) return;
        transition.StopCancellation();
        transition.state = State.Holding;
        transition.canvas.enabled = true;
        // Always opaque black. Ignore any gray/alpha settings saved by older versions.
        transition.SetOpaqueBlack();
        transition.CacheUiAudioIfNeeded();
        transition.HoldAudioForLoading();
        transition.content.SetActive(true);
        transition.title.text = transition.leavingText;
        transition.SetProgress(progress);
        transition.FollowCamera();
    }

    public static void CancelExitProgress()
    {
        if (instance == null || instance.state != State.Holding || instance.cancelRoutine != null)
            return;
        instance.cancelRoutine = instance.StartCoroutine(instance.CancelHold());
    }

    public static bool TryLoad(string scene, bool showLoadingBar, Action onFailure = null)
    {
        // Private latch stays locked even during the initial UI callback frame.
        if (instance != null && instance.state == State.Loading) return false;
        ExperienceTransition transition = GetOrCreate();
        if (!transition.Ready || !transition.isActiveAndEnabled) return false;
        transition.StopCancellation();
        bool wasHolding = transition.state == State.Holding;
        transition.state = State.Loading;
        // Lock menu navigation now; the existing click-audio callback can still complete.
        transition.canvas.enabled = true;
        // Lock synchronously, before the first coroutine frame, to reject repeated clicks.
        transition.StartCoroutine(transition.LoadScene(scene, showLoadingBar, wasHolding, onFailure));
        return true;
    }

    private IEnumerator LoadScene(string scene, bool showLoadingBar, bool wasHolding, Action onFailure)
    {
        float selectedAt = Time.unscaledTime;
        CacheUiAudio();
        if (showLoadingBar)
        {
            CacheOutgoingAudioExceptUi();
            StopOutgoingAudioExceptUi();
        }
        if (wasHolding)
        {
            SetProgress(1f);
            yield return null;
        }
        content.SetActive(false);
        float blackoutDuration = showLoadingBar ? Mathf.Clamp(entryFadeOutDuration, 0.05f, 0.3f) : fadeOutDuration;
        yield return FadeBackground(Color.black, blackoutDuration);
        keepPreparationBlack = true;
        SetOpaqueBlack();
        if (showLoadingBar)
            yield return FinishOutgoingUiAudio(selectedAt);
        HoldAudioForLoading();
        // A separate pause makes the plain black phase visible in both directions.
        yield return WaitUnscaled(showLoadingBar ? entryBlackPause : exitBlackPause);
        yield return null;

        if (showLoadingBar)
        {
            title.text = loadingText;
            SetProgress(0f);
            content.SetActive(true);
            yield return null;
        }

        float loadingStarted = Time.unscaledTime;
        AsyncOperation operation = null;
        Exception failure = null;
        try
        {
            operation = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
            if (operation != null) operation.allowSceneActivation = false;
        }
        catch (Exception exception) { failure = exception; }

        if (operation == null)
        {
            Debug.LogError("No se pudo cargar '" + scene + "'. " + failure, this);
            onFailure?.Invoke();
            yield return RevealPreparedScene(showLoadingBar);
            yield break;
        }

        StopOutgoingSceneAudio();

        float initialElapsed = 0f;
        float slowDuration = Mathf.Max(0.1f, initialFillDuration);
        while (operation.progress < 0.9f || (showLoadingBar && initialElapsed < slowDuration))
        {
            if (showLoadingBar)
            {
                // A long activation/loading frame must not make the visual bar jump to its target.
                initialElapsed += Mathf.Min(Time.unscaledDeltaTime, 0.05f);
                float t = Mathf.Clamp01(initialElapsed / slowDuration);
                SetProgress(0.25f * Mathf.SmoothStep(0f, 1f, t));
            }
            yield return null;
        }
        // Keep the bar at 25 % while the destination starts and renders behind the overlay.
        // LoadSceneAsync reaching 0.9 does not describe post-activation preparation.
        if (showLoadingBar) SetProgress(0.25f);
        // The destination has not activated yet: every currently playing voice is outgoing.
        // Clear late/restarted voices before the new scene creates any of its ambience.
        StopOutgoingSceneAudio();
        operation.allowSceneActivation = true;
        while (!operation.isDone) yield return null;

        viewerCamera = null;
        viewerRig = null;
        viewerAnchor = null;
        // Temporarily draw the destination for shader/texture preparation under the opaque UI.
        renderSceneForPreparation = true;
        FollowCamera();
        yield return WaitForScenePreparation();
        renderSceneForPreparation = false;
        FollowCamera();
        if (showLoadingBar)
        {
            while (Time.unscaledTime - loadingStarted < Mathf.Max(0f, minimumLoadingDuration))
                yield return null;
            yield return AnimateProgress(0.25f, 1f, completionFillDuration);
            yield return WaitUnscaled(completedBarHoldDuration);
            content.SetActive(false);
        }
        yield return RevealPreparedScene(showLoadingBar);
    }

    private IEnumerator RevealPreparedScene(bool enteringExperience)
    {
        // The destination is already active and prepared. Remove all loading UI first.
        content.SetActive(false);
        keepPreparationBlack = true;
        SetOpaqueBlack();
        FollowCamera();
        // Wait for actual headset-camera frames before lifting the curtain.
        yield return WaitForViewerFrames(2);
        yield return WaitUnscaled(enteringExperience ? entryBlackPause : exitBlackPause);
        keepPreparationBlack = false;
        awaitingSceneVisibility = true;
        fullyVisibleRenderedFrames = 0;
        yield return FadeBackground(Color.clear, fadeInDuration, true);
        yield return WaitForVisibleScene();
        BeginAudioReveal();
        yield return FadeSceneAudioIn();
        Hide();
    }

    private IEnumerator WaitForScenePreparation()
    {
        float started = Time.unscaledTime;
        float quietTime = 0f;
        int frames = 0;
        int minimumFrames = Mathf.Max(3, minimumPreparationFrames);
        float settleTime = Mathf.Max(0f, sceneSettleDuration);
        float quietRequired = Mathf.Max(0f, textureQuietDuration);
        float timeout = Mathf.Max(settleTime + quietRequired, textureWaitTimeout);
        while (true)
        {
            // Yield actual rendering frames. The camera still renders the scene behind the black UI.
            yield return null;
            frames++;
            FollowCamera();
            float elapsed = Time.unscaledTime - started;
            bool streaming = QualitySettings.streamingMipmapsActive && Texture.streamingTextureCount > 0;
            int scanFrames = minimumFrames;
            if (streaming)
            {
                ulong perFrame = (ulong)Mathf.Max(1, QualitySettings.streamingMipmapsRenderersPerFrame);
                ulong passes = (Texture.streamingRendererCount + perFrame - 1) / perFrame;
                scanFrames = Mathf.Max(minimumFrames, (int)Math.Min(passes, (ulong)(int.MaxValue - 2)) + 2);
            }
            bool minimumReady = frames >= scanFrames && elapsed >= settleTime && viewerCamera != null;
            bool pending = streaming && (Texture.streamingTexturePendingLoadCount > 0 ||
                Texture.streamingTextureLoadingCount > 0);
            if (minimumReady && !pending)
            {
                quietTime += Time.unscaledDeltaTime;
                if (quietTime >= quietRequired) yield break;
            }
            else quietTime = 0f;

            // Streaming is view-dependent and may keep requesting mipmaps when the head moves.
            if (frames >= minimumFrames && elapsed >= timeout)
            {
                Debug.LogWarning("La preparación de la escena agotó el tiempo de espera. " +
                    "Se continuará la transición; revisa el streaming o aumenta Texture Wait Timeout.", this);
                yield break;
            }
        }
    }

    private IEnumerator AnimateProgress(float start, float end, float seconds)
    {
        float duration = Mathf.Max(0.1f, seconds);
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            float t = Mathf.Clamp01(elapsed / duration);
            SetProgress(Mathf.Lerp(start, end, Mathf.SmoothStep(0f, 1f, t)));
            yield return null;
        }
        SetProgress(end);
    }

    private static IEnumerator WaitUnscaled(float seconds)
    {
        float elapsed = 0f;
        while (elapsed < Mathf.Max(0f, seconds))
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    private IEnumerator WaitForViewerFrames(int count)
    {
        int firstFrame = viewerRenderedFrames;
        while (viewerRenderedFrames - firstFrame < count)
            yield return null;
    }

    private IEnumerator WaitForVisibleScene()
    {
        while (fullyVisibleRenderedFrames < 2)
            yield return null;
    }

    private IEnumerator FadeSceneAudioIn()
    {
        if (!audioHeld || !revealingAudio) yield break;
        float elapsed = 0f;
        float duration = Mathf.Max(0.1f, sceneAudioFadeInDuration);
        while (elapsed < duration)
        {
            elapsed += Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            SetAudioRevealGain(Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration)));
            yield return WaitForViewerFrames(1);
        }
        RestoreAudio();
    }

    private IEnumerator CancelHold()
    {
        // Wait one frame so even a duration of zero leaves a cancellable coroutine handle.
        yield return null;
        content.SetActive(false);
        keepPreparationBlack = true;
        SetOpaqueBlack();
        FollowCamera();
        yield return WaitForViewerFrames(2);
        keepPreparationBlack = false;
        awaitingSceneVisibility = true;
        fullyVisibleRenderedFrames = 0;
        yield return FadeBackground(Color.clear, cancelDuration);
        yield return WaitForVisibleScene();
        BeginAudioReveal();
        yield return FadeSceneAudioIn();
        Hide();
        cancelRoutine = null;
    }

    private void StopCancellation()
    {
        if (cancelRoutine == null) return;
        StopCoroutine(cancelRoutine);
        cancelRoutine = null;
    }

    private IEnumerator FadeBackground(Color target, float seconds, bool waitForRenderedReveal = false)
    {
        Color start = background.color;
        // A prepared scene must be revealed gradually, even if an old Inspector value was zero.
        float duration = waitForRenderedReveal ? Mathf.Max(0.6f, seconds) : Mathf.Max(0f, seconds);
        if (duration > 0f)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                // Shader/scene warmup can produce a long frame. Do not finish the fade in one step.
                elapsed += waitForRenderedReveal ? Mathf.Min(Time.unscaledDeltaTime, 0.05f) : Time.unscaledDeltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
                background.color = Color.Lerp(start, target, t);
                // Audio stays muted throughout the visual fade. It is released separately afterward.
                if (waitForRenderedReveal) yield return WaitForViewerFrames(1);
                else yield return null;
            }
        }
        background.color = target;
    }

    private void Hide()
    {
        keepPreparationBlack = false;
        renderSceneForPreparation = false;
        content.SetActive(false);
        background.color = Color.clear;
        canvas.enabled = false;
        state = State.Idle;
        awaitingSceneVisibility = false;
        fullyVisibleRenderedFrames = 0;
        RestoreControllerRenderingLayers();
        controllerOverlayPass?.Dispose();
        RestoreCameraVisuals();
    }

    private void SetProgress(float value)
    {
        barFill.anchorMax = new Vector2(Mathf.Clamp01(value), 1f);
    }

    private void LateUpdate()
    {
        if (audioHeld && !revealingAudio) MaintainLoadingSilence();
        FollowCamera();
    }

    private void SetOpaqueBlack()
    {
        background.color = new Color(0f, 0f, 0f, 1f);
        background.canvasRenderer.SetColor(Color.white);
        background.canvasRenderer.SetAlpha(1f);
    }

    private void OnDisable()
    {
        renderSceneForPreparation = false;
        RestoreAudio();
        RestoreControllerRenderingLayers();
        controllerOverlayPass?.Dispose();
        RestoreCameraVisuals();
    }

    private bool IsViewerRenderCamera(Camera camera)
    {
        return IsUsableCamera(camera) && (camera == viewerCamera ||
            (viewerRig != null && camera.GetComponentInParent<OVRCameraRig>() == viewerRig));
    }

    private void MaintainCameraVisuals()
    {
        if (!Ready || state == State.Idle) return;
        bool exitHold = state == State.Holding && cancelRoutine == null;
        bool renderBlackOnly = exitHold || (keepPreparationBlack && !renderSceneForPreparation);
        bool opaqueBlack = background.color.a >= 0.999f;
        foreach (Camera camera in Camera.allCameras)
        {
            if (!IsViewerRenderCamera(camera)) continue;
            CameraVisualState saved;
            if (!savedCameraVisuals.TryGetValue(camera, out saved))
            {
                UniversalAdditionalCameraData urp = camera.GetComponent<UniversalAdditionalCameraData>();
                saved = new CameraVisualState
                {
                    cullingMask = camera.cullingMask,
                    clearFlags = camera.clearFlags,
                    backgroundColor = camera.backgroundColor,
                    urpData = urp,
                    renderPostProcessing = urp != null && urp.renderPostProcessing
                };
                savedCameraVisuals.Add(camera, saved);
            }

            // Keep the world excluded in every black pause, including after either bar completes.
            // Only the bounded preparation stage draws the destination under the opaque overlay.
            camera.cullingMask = renderBlackOnly ? transitionUiMask : saved.cullingMask | transitionUiMask;
            camera.clearFlags = renderBlackOnly ? CameraClearFlags.SolidColor : saved.clearFlags;
            camera.backgroundColor = renderBlackOnly ? Color.black : saved.backgroundColor;
            // Lens flares/bloom must not add bright pixels over a fully black screen.
            if (saved.urpData != null)
                saved.urpData.renderPostProcessing = opaqueBlack ? false : saved.renderPostProcessing;
        }
    }

    private void RestoreCameraVisuals()
    {
        foreach (KeyValuePair<Camera, CameraVisualState> entry in savedCameraVisuals)
        {
            if (entry.Key == null) continue;
            entry.Key.cullingMask = entry.Value.cullingMask;
            entry.Key.clearFlags = entry.Value.clearFlags;
            entry.Key.backgroundColor = entry.Value.backgroundColor;
            if (entry.Value.urpData != null)
                entry.Value.urpData.renderPostProcessing = entry.Value.renderPostProcessing;
        }
        savedCameraVisuals.Clear();
    }

    private void DiscoverControllerVisualRoots()
    {
        detectedVisualRoots.Clear();
        foreach (Transform root in controllerVisualRoots) AddControllerVisualRoot(root);
        foreach (OVRCameraRig rig in FindObjectsByType<OVRCameraRig>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            AddControllerVisualRoot(rig.leftHandAnchor);
            AddControllerVisualRoot(rig.rightHandAnchor);
        }

        // Some Meta Interaction visual models live outside the hand anchors.
        foreach (MonoBehaviour component in FindObjectsByType<MonoBehaviour>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (component == null) continue;
            string typeName = component.GetType().Name;
            if (typeName == "ControllerVisual" || typeName == "HandVisual" ||
                typeName == "OVRControllerHelper" || typeName == "OVRHand" ||
                typeName == "OVRCustomHand")
                AddControllerVisualRoot(component.transform);
        }
        nextVisualRootDiscovery = Time.unscaledTime + 0.25f;
    }

    private void AddControllerVisualRoot(Transform root)
    {
        if (root != null && !detectedVisualRoots.Contains(root)) detectedVisualRoots.Add(root);
    }

    private void MaintainControllerVisuals()
    {
        if (!showControllersOverBlack)
        {
            RestoreControllerRenderingLayers();
            return;
        }
        if (Time.unscaledTime < nextVisualRootDiscovery) return;
        DiscoverControllerVisualRoots();
        if (controllerRenderingLayer == 0)
        {
            // Reserve a currently unused rendering bit, without changing GameObject/physics layers.
            uint occupied = 0;
            foreach (Renderer model in FindObjectsByType<Renderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
                occupied |= model.renderingLayerMask;
            for (int bit = 31; bit > 0; bit--)
            {
                uint candidate = 1u << bit;
                if ((occupied & candidate) != 0) continue;
                controllerRenderingLayer = candidate;
                break;
            }
            if (controllerRenderingLayer == 0)
            {
                if (!controllerLayerWarningShown)
                    Debug.LogWarning("No hay un bit de Rendering Layer libre para dibujar los mandos " +
                        "sobre el negro. Revisa las Rendering Layers de los Renderer de esta escena.", this);
                controllerLayerWarningShown = true;
                return;
            }
        }
        controllerObjectLayers = 0;
        foreach (Transform root in detectedVisualRoots)
        {
            if (root == null) continue;
            visualRendererBuffer.Clear();
            root.GetComponentsInChildren<Renderer>(true, visualRendererBuffer);
            foreach (Renderer model in visualRendererBuffer)
            {
                if (model == null) continue;
                if (!savedControllerRenderingLayers.ContainsKey(model))
                    savedControllerRenderingLayers.Add(model, model.renderingLayerMask);
                model.renderingLayerMask |= controllerRenderingLayer;
                controllerObjectLayers |= 1u << model.gameObject.layer;
            }
        }
    }

    private void RestoreControllerRenderingLayers()
    {
        foreach (KeyValuePair<Renderer, uint> saved in savedControllerRenderingLayers)
        {
            if (saved.Key == null) continue;
            // Restore only our bit; preserve changes made by other components to other bits.
            saved.Key.renderingLayerMask = (saved.Key.renderingLayerMask & ~controllerRenderingLayer) |
                (saved.Value & controllerRenderingLayer);
        }
        savedControllerRenderingLayers.Clear();
        detectedVisualRoots.Clear();
        visualRendererBuffer.Clear();
        controllerRenderingLayer = 0;
        controllerObjectLayers = 0;
        nextVisualRootDiscovery = 0f;
        controllerLayerWarningShown = false;
    }

    private void HoldAudioForLoading()
    {
        bool firstHold = !audioHeld;
        if (!audioHeld)
        {
            savedListenerVolume = AudioListener.volume;
            audioHeld = true;
            revealingAudio = false;
            fmodAudioCaptured = false;
            nextFmodCaptureTime = 0f;
            coreMasterCaptured = false;
            nextCoreCaptureTime = 0f;
        }
        revealingAudio = false;
        awaitingSceneVisibility = false;
        fullyVisibleRenderedFrames = 0;
        MaintainLoadingSilence();
        // Commit the mute before any destination Start/OnEnable can start its events.
        if (firstHold && FMODUnity.RuntimeManager.IsInitialized)
            FMODUnity.RuntimeManager.StudioSystem.flushCommands();
    }

    private IEnumerator FinishOutgoingUiAudio(float selectedAt)
    {
        // Let all listeners of the current Button.onClick run, regardless of their order.
        yield return null;
        float duration = Mathf.Clamp(uiClickGraceDuration, 1f, 3f);
        while (Time.unscaledTime - selectedAt < duration)
        {
            StopOutgoingAudioExceptUi();
            yield return null;
        }
        // A fixed grace period avoids racing FMOD's asynchronous playback-state queries.
        StopUiAudio();
    }

    private void CacheOutgoingAudioExceptUi()
    {
        outgoingAudioEvents.Clear();
        outgoingAudioSources.Clear();
        foreach (AudioSource source in FindObjectsByType<AudioSource>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (!uiAudioSources.Contains(source)) outgoingAudioSources.Add(source);
        if (!FMODUnity.RuntimeManager.IsInitialized) return;
        FMOD.Studio.Bank[] banks;
        if (FMODUnity.RuntimeManager.StudioSystem.getBankList(out banks) != FMOD.RESULT.OK) return;
        foreach (FMOD.Studio.Bank bank in banks)
        {
            FMOD.Studio.EventDescription[] events;
            if (!bank.isValid() || bank.getEventList(out events) != FMOD.RESULT.OK) continue;
            foreach (FMOD.Studio.EventDescription description in events)
                if (!uiAudioEvents.Contains(description) && !outgoingAudioEvents.Contains(description))
                    outgoingAudioEvents.Add(description);
        }
    }

    private void StopOutgoingAudioExceptUi()
    {
        foreach (AudioSource source in outgoingAudioSources)
            if (source != null) source.Stop();
        foreach (FMOD.Studio.EventDescription description in outgoingAudioEvents)
        {
            FMOD.Studio.EventInstance[] voices;
            if (!description.isValid() || description.getInstanceList(out voices) != FMOD.RESULT.OK) continue;
            foreach (FMOD.Studio.EventInstance voice in voices)
                if (voice.isValid()) voice.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
        }
    }

    private void StopOutgoingSceneAudio()
    {
        // Only called before allowSceneActivation=true. Never stop the destination's audio.
        foreach (AudioSource source in FindObjectsByType<AudioSource>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
            source.Stop();

        if (!FMODUnity.RuntimeManager.IsInitialized) return;
        FMOD.Studio.Bus outgoingBus;
        FMOD.Studio.System studio = FMODUnity.RuntimeManager.StudioSystem;
        if (studio.getBus("bus:/", out outgoingBus) == FMOD.RESULT.OK && outgoingBus.isValid())
        {
            // Immediate stop discards outgoing ambience instead of freezing a fade-out tail.
            outgoingBus.stopAllEvents(FMOD.Studio.STOP_MODE.IMMEDIATE);
            studio.flushCommands();
        }
    }

    private void CacheUiAudio()
    {
        uiAudioEvents.Clear();
        uiAudioSources.Clear();
        outgoingAudioEvents.Clear();
        outgoingAudioSources.Clear();
        if (uiAudioManager != null) CacheUiAudioFrom(uiAudioManager);
        foreach (MonoBehaviour component in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (component == null || component == uiAudioManager) continue;
            string typeName = component.GetType().Name;
            if (typeName == "UIAudioManager" || typeName == "UI_AudioManager")
                CacheUiAudioFrom(component);
        }
    }

    private void CacheUiAudioIfNeeded()
    {
        if (!audioHeld) CacheUiAudio();
    }

    private void CacheUiAudioFrom(MonoBehaviour manager)
    {
        // Read existing serialized event references without replacing the user's UIAudioManager.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (FieldInfo field in manager.GetType().GetFields(flags))
        {
            if (field.FieldType == typeof(FMODUnity.EventReference))
                AddUiAudioEvent((FMODUnity.EventReference)field.GetValue(manager));
            else if (field.FieldType == typeof(FMODUnity.EventReference[]))
            {
                var events = field.GetValue(manager) as FMODUnity.EventReference[];
                if (events != null)
                    foreach (FMODUnity.EventReference uiEvent in events) AddUiAudioEvent(uiEvent);
            }
            else if (field.FieldType == typeof(AudioSource))
                AddUiAudioSource(field.GetValue(manager) as AudioSource);
        }
        foreach (AudioSource source in manager.GetComponentsInChildren<AudioSource>(true))
            AddUiAudioSource(source);
    }

    private void AddUiAudioEvent(FMODUnity.EventReference reference)
    {
        if (reference.IsNull || !FMODUnity.RuntimeManager.IsInitialized) return;
        try
        {
            FMOD.Studio.EventDescription description = FMODUnity.RuntimeManager.GetEventDescription(reference);
            if (description.isValid() && !uiAudioEvents.Contains(description))
                uiAudioEvents.Add(description);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("No se pudo consultar un evento de UI: " + exception.Message, this);
        }
    }

    private void AddUiAudioSource(AudioSource source)
    {
        if (source != null && !source.loop && !uiAudioSources.Contains(source))
            uiAudioSources.Add(source);
    }

    private void StopUiAudio()
    {
        foreach (AudioSource source in uiAudioSources)
            if (source != null) source.Stop();
        foreach (FMOD.Studio.EventDescription description in uiAudioEvents)
        {
            FMOD.Studio.EventInstance[] instances;
            if (!description.isValid() || description.getInstanceList(out instances) != FMOD.RESULT.OK)
                continue;
            foreach (FMOD.Studio.EventInstance eventInstance in instances)
                if (eventInstance.isValid()) eventInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
        }
    }

    private void MaintainLoadingSilence()
    {
        // New hover/click sounds fired by hidden UI must not replay on reveal.
        StopUiAudio();
        // Mute without pausing: let outgoing effect tails drain during the black screen.
        AudioListener.volume = 0f;
        foreach (AudioSource source in FindObjectsByType<AudioSource>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!source.ignoreListenerVolume) continue;
            if (!audioBypassStates.ContainsKey(source))
            {
                audioBypassStates.Add(source, new AudioBypassState
                {
                    ignoreVolume = source.ignoreListenerVolume
                });
            }
            source.ignoreListenerVolume = false;
        }
        CaptureFmodAudio();
        if (fmodAudioCaptured && masterAudioBus.isValid())
        {
            masterAudioBus.setVolume(0f);
            masterAudioBus.setMute(true);
        }
        CaptureCoreAudio();
        if (coreMasterCaptured && coreMasterAudio.hasHandle())
        {
            coreMasterAudio.setVolume(0f);
            coreMasterAudio.setMute(true);
        }
    }

    private void CaptureFmodAudio()
    {
        if (fmodAudioCaptured && masterAudioBus.isValid()) return;
        if (Time.unscaledTime < nextFmodCaptureTime) return;
        nextFmodCaptureTime = Time.unscaledTime + 0.1f;
        // Do not initialize FMOD just for this screen. Retry when its runtime/banks exist.
        if (!FMODUnity.RuntimeManager.IsInitialized) return;
        FMOD.Studio.Bus bus;
        if (FMODUnity.RuntimeManager.StudioSystem.getBus("bus:/", out bus) != FMOD.RESULT.OK ||
            !bus.isValid()) return;
        float volume;
        bool muted;
        if (bus.getVolume(out volume) != FMOD.RESULT.OK ||
            bus.getMute(out muted) != FMOD.RESULT.OK) return;
        masterAudioBus = bus;
        savedFmodVolume = volume;
        savedFmodMuted = muted;
        fmodAudioCaptured = true;
    }

    private void BeginAudioReveal()
    {
        if (!audioHeld || fullyVisibleRenderedFrames < 2 || keepPreparationBlack ||
            background.color.a > 0f) return;
        StopUiAudio();
        CaptureFmodAudio();
        CaptureCoreAudio();
        revealingAudio = true;
        // The fully uncovered scene has already rendered; only now allow an audio fade-in.
        AudioListener.volume = 0f;
        if (coreMasterCaptured && coreMasterAudio.hasHandle())
        {
            coreMasterAudio.setVolume(0f);
            coreMasterAudio.setMute(savedCoreMuted);
        }
        if (fmodAudioCaptured && masterAudioBus.isValid())
        {
            masterAudioBus.setVolume(coreMasterCaptured ? savedFmodVolume : 0f);
            masterAudioBus.setMute(savedFmodMuted);
        }
    }

    private void CaptureCoreAudio()
    {
        if (coreMasterCaptured && coreMasterAudio.hasHandle()) return;
        if (Time.unscaledTime < nextCoreCaptureTime) return;
        nextCoreCaptureTime = Time.unscaledTime + 0.1f;
        if (!FMODUnity.RuntimeManager.IsInitialized) return;
        FMOD.ChannelGroup group;
        if (FMODUnity.RuntimeManager.CoreSystem.getMasterChannelGroup(out group) != FMOD.RESULT.OK ||
            !group.hasHandle()) return;
        float volume;
        bool muted;
        if (group.getVolume(out volume) != FMOD.RESULT.OK ||
            group.getMute(out muted) != FMOD.RESULT.OK) return;
        coreMasterAudio = group;
        savedCoreVolume = volume;
        savedCoreMuted = muted;
        coreMasterCaptured = true;
    }

    private void SetAudioRevealGain(float gain)
    {
        if (!audioHeld || !revealingAudio || fullyVisibleRenderedFrames < 2) return;
        gain = Mathf.Clamp01(gain);
        AudioListener.volume = savedListenerVolume * gain;
        if (coreMasterCaptured && coreMasterAudio.hasHandle())
            coreMasterAudio.setVolume(savedCoreVolume * gain);
        else if (fmodAudioCaptured && masterAudioBus.isValid())
            masterAudioBus.setVolume(savedFmodVolume * gain);
    }

    private void RestoreAudio()
    {
        if (!audioHeld) return;
        AudioListener.volume = savedListenerVolume;
        if (fmodAudioCaptured && masterAudioBus.isValid())
        {
            masterAudioBus.setVolume(savedFmodVolume);
            masterAudioBus.setMute(savedFmodMuted);
        }
        if (coreMasterCaptured && coreMasterAudio.hasHandle())
        {
            coreMasterAudio.setVolume(savedCoreVolume);
            coreMasterAudio.setMute(savedCoreMuted);
        }
        foreach (KeyValuePair<AudioSource, AudioBypassState> saved in audioBypassStates)
        {
            if (saved.Key == null) continue;
            saved.Key.ignoreListenerVolume = saved.Value.ignoreVolume;
        }
        audioBypassStates.Clear();
        uiAudioEvents.Clear();
        uiAudioSources.Clear();
        outgoingAudioEvents.Clear();
        outgoingAudioSources.Clear();
        audioHeld = false;
        revealingAudio = false;
        fmodAudioCaptured = false;
        coreMasterCaptured = false;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (audioHeld && !revealingAudio) MaintainLoadingSilence();
        RestoreControllerRenderingLayers();
        SelectTransitionUiLayer();
        viewerCamera = null;
        viewerRig = null;
        viewerAnchor = null;
        FollowCamera();
    }

    private void FollowCamera()
    {
        if (!Ready || state == State.Idle) return;
        if (audioHeld && !revealingAudio) MaintainLoadingSilence();
        // Reassert black before rendering, while allowing the cancellation fade to work.
        if (keepPreparationBlack || (state == State.Holding && cancelRoutine == null))
            SetOpaqueBlack();
        if (!IsUsableCamera(viewerCamera) || viewerAnchor == null ||
            (viewerRig != null && !viewerRig.isActiveAndEnabled) ||
            (viewerRig == null && Time.unscaledTime >= nextViewerSearchTime))
        {
            ResolveViewer();
            if (viewerCamera == null) return;
            canvas.worldCamera = viewerCamera;
            // Keep transition graphics isolated from ocean/world geometry.
            foreach (Transform child in canvasRect.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = transitionUiLayer;
        }
        Transform eye = viewerAnchor;
        float distance = Mathf.Max(panelDistance, viewerCamera.nearClipPlane + 0.2f);
        canvasRect.SetPositionAndRotation(eye.position + eye.forward * distance, eye.rotation);
        MaintainControllerVisuals();
        MaintainCameraVisuals();
    }

    private static bool IsUsableCamera(Camera candidate)
    {
        return candidate != null && candidate.isActiveAndEnabled &&
            candidate.cameraType == CameraType.Game && candidate.targetTexture == null;
    }

    private void ResolveViewer()
    {
        nextViewerSearchTime = Time.unscaledTime + 0.25f;
        viewerCamera = null;
        viewerRig = null;
        viewerAnchor = null;
        // Camera.main can be an unrelated landscape camera, as in Acrofobia_3.
        // Find the tracked headset first, including rigs that use separate eye cameras.
        foreach (OVRCameraRig rig in FindObjectsByType<OVRCameraRig>(FindObjectsSortMode.None))
        {
            if (!rig.isActiveAndEnabled || rig.centerEyeAnchor == null) continue;
            Camera candidate = rig.centerEyeAnchor.GetComponent<Camera>();
            if (!IsUsableCamera(candidate) && rig.leftEyeAnchor != null)
                candidate = rig.leftEyeAnchor.GetComponent<Camera>();
            if (!IsUsableCamera(candidate) && rig.rightEyeAnchor != null)
                candidate = rig.rightEyeAnchor.GetComponent<Camera>();
            if (!IsUsableCamera(candidate)) continue;
            viewerRig = rig;
            viewerAnchor = rig.centerEyeAnchor;
            viewerCamera = candidate;
            return;
        }
        foreach (Camera candidate in Camera.allCameras)
        {
            if (!IsUsableCamera(candidate) || !candidate.stereoEnabled) continue;
            viewerCamera = candidate;
            viewerAnchor = candidate.transform;
            return;
        }
        Camera main = Camera.main;
        if (IsUsableCamera(main)) viewerCamera = main;
        else
        {
            foreach (Camera candidate in Camera.allCameras)
            {
                if (!IsUsableCamera(candidate)) continue;
                viewerCamera = candidate;
                break;
            }
        }
        if (viewerCamera != null) viewerAnchor = viewerCamera.transform;
    }

    private void SetVisibilityForCamera(Camera renderingCamera)
    {
        if (!Ready || state == State.Idle) return;
        FollowCamera();
        canvas.enabled = IsViewerRenderCamera(renderingCamera);
    }

    private void RestoreCanvasVisibility()
    {
        if (Ready) canvas.enabled = state != State.Idle;
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        SetVisibilityForCamera(renderingCamera);
        if (!isActiveAndEnabled || !Ready || state == State.Idle || !showControllersOverBlack ||
            !IsViewerRenderCamera(renderingCamera) || background.color.a <= 0f ||
            controllerRenderingLayer == 0 || controllerObjectLayers == 0) return;
        UniversalAdditionalCameraData urp = renderingCamera.GetComponent<UniversalAdditionalCameraData>();
        if (urp == null || urp.scriptableRenderer == null) return;
        ScriptableCullingParameters parameters;
        if (!renderingCamera.TryGetCullingParameters(renderingCamera.stereoEnabled, out parameters)) return;
        // The normal camera can remain UI-only during exit; this cull is used only for the models.
        parameters.cullingMask = controllerObjectLayers;
        parameters.cullingOptions &= ~CullingOptions.OcclusionCull;
        if (controllerOverlayPass == null) controllerOverlayPass = new ControllerOverlayPass();
        controllerOverlayPass.Prepare(context.Cull(ref parameters), controllerRenderingLayer,
            urp.scriptableRenderer);
        urp.scriptableRenderer.EnqueuePass(controllerOverlayPass);
    }

    private void OnEndCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        RecordViewerFrame(renderingCamera);
        RestoreCanvasVisibility();
    }

    private void OnLegacyCameraPreCull(Camera renderingCamera)
    {
        if (GraphicsSettings.currentRenderPipeline == null) SetVisibilityForCamera(renderingCamera);
    }

    private void OnLegacyCameraPostRender(Camera renderingCamera)
    {
        if (GraphicsSettings.currentRenderPipeline != null) return;
        RecordViewerFrame(renderingCamera);
        RestoreCanvasVisibility();
    }

    private void RecordViewerFrame(Camera renderingCamera)
    {
        if (state == State.Idle || !IsViewerRenderCamera(renderingCamera) || !canvas.enabled) return;
        // Multiple eye cameras/passes count as one preparation frame.
        if (lastViewerRenderedFrame != Time.frameCount)
        {
            lastViewerRenderedFrame = Time.frameCount;
            viewerRenderedFrames++;
            if (awaitingSceneVisibility && !keepPreparationBlack && background.color.a <= 0f)
                fullyVisibleRenderedFrames++;
            else
                fullyVisibleRenderedFrames = 0;
        }
    }

    

    

    // Draw only tagged controller renderers after the world-space transition Canvas.
    // Separate depth preserves the models' own occlusion without clearing scene depth.
    private sealed class ControllerOverlayPass : ScriptableRenderPass
    {
        private readonly List<ShaderTagId> shaderTags = new List<ShaderTagId>
        {
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
            new ShaderTagId("SRPDefaultUnlit")
        };
        private CullingResults modelCulling;
        private uint modelRenderingLayer;
        private ScriptableRenderer targetRenderer;
        private RTHandle compatibilityDepth;

        private sealed class PassData
        {
            public RendererListHandle opaque;
            public RendererListHandle transparent;
        }

        public ControllerOverlayPass()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
            profilingSampler = new ProfilingSampler("Transition controller models");
        }

        public void Prepare(CullingResults culling, uint renderingLayer, ScriptableRenderer renderer)
        {
            modelCulling = culling;
            modelRenderingLayer = renderingLayer;
            targetRenderer = renderer;
        }

        private FilteringSettings Filter(RenderQueueRange range)
        {
            return new FilteringSettings(range, -1, modelRenderingLayer);
        }

        private void MatchLightIndices(CullingResults sceneCulling)
        {
            // URP remaps visible-light indices for per-object lighting. Reuse that mapping.
            var sceneMap = sceneCulling.GetLightIndexMap(Allocator.Temp);
            var modelMap = modelCulling.GetLightIndexMap(Allocator.Temp);
            var sceneLights = sceneCulling.visibleLights;
            var modelLights = modelCulling.visibleLights;
            for (int i = 0; i < modelMap.Length; i++)
            {
                modelMap[i] = -1;
                if (modelLights[i].light == null) continue;
                for (int j = 0; j < sceneMap.Length; j++)
                {
                    if (modelLights[i].light != sceneLights[j].light) continue;
                    modelMap[i] = sceneMap[j];
                    break;
                }
            }
            modelCulling.SetLightIndexMap(modelMap);
            modelMap.Dispose();
            sceneMap.Dispose();
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            if (!resources.activeColorTexture.IsValid() || !resources.activeDepthTexture.IsValid()) return;
            UniversalRenderingData rendering = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lights = frameData.Get<UniversalLightData>();
            MatchLightIndices(rendering.cullResults);
            DrawingSettings opaque = RenderingUtils.CreateDrawingSettings(shaderTags, rendering,
                cameraData, lights, SortingCriteria.CommonOpaque);
            DrawingSettings transparent = RenderingUtils.CreateDrawingSettings(shaderTags, rendering,
                cameraData, lights, SortingCriteria.CommonTransparent);
            TextureDesc depth = renderGraph.GetTextureDesc(resources.activeDepthTexture);
            depth.name = "Transition controller depth";
            depth.clearBuffer = true;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                "Transition controller models", out var data, profilingSampler))
            {
                data.opaque = renderGraph.CreateRendererList(new RendererListParams(
                    modelCulling, opaque, Filter(RenderQueueRange.opaque)));
                data.transparent = renderGraph.CreateRendererList(new RendererListParams(
                    modelCulling, transparent, Filter(RenderQueueRange.transparent)));
                builder.UseRendererList(data.opaque);
                builder.UseRendererList(data.transparent);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(renderGraph.CreateTexture(depth), AccessFlags.ReadWrite);
                if (resources.mainShadowsTexture.IsValid())
                    builder.UseTexture(resources.mainShadowsTexture, AccessFlags.Read);
                if (resources.additionalShadowsTexture.IsValid())
                    builder.UseTexture(resources.additionalShadowsTexture, AccessFlags.Read);
                if (resources.ssaoTexture.IsValid())
                    builder.UseTexture(resources.ssaoTexture, AccessFlags.Read);
                foreach (TextureHandle texture in resources.dBuffer)
                    if (texture.IsValid()) builder.UseTexture(texture, AccessFlags.Read);
                builder.AllowPassCulling(false);
                if (cameraData.xr.enabled)
                    builder.EnableFoveatedRasterization(cameraData.xr.supportsFoveatedRendering &&
                        resources.isActiveTargetBackBuffer);
                builder.SetRenderFunc((PassData pass, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(pass.opaque);
                    context.cmd.DrawRendererList(pass.transparent);
                });
            }
        }

#pragma warning disable CS0618, CS0672 // URP compatibility-mode callbacks.
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.graphicsFormat = GraphicsFormat.None;
            descriptor.depthStencilFormat = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil);
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.enableRandomWrite = false;
            RenderingUtils.ReAllocateIfNeeded(ref compatibilityDepth, descriptor,
                FilterMode.Point, TextureWrapMode.Clamp, name: "Transition controller depth");
            ConfigureTarget(targetRenderer.cameraColorTargetHandle, compatibilityDepth);
            ConfigureClear(ClearFlag.Depth, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            MatchLightIndices(renderingData.cullResults);
            DrawingSettings opaque = RenderingUtils.CreateDrawingSettings(shaderTags,
                ref renderingData, SortingCriteria.CommonOpaque);
            DrawingSettings transparent = RenderingUtils.CreateDrawingSettings(shaderTags,
                ref renderingData, SortingCriteria.CommonTransparent);
            var opaqueParameters = new RendererListParams(
                modelCulling, opaque, Filter(RenderQueueRange.opaque));
            var transparentParameters = new RendererListParams(
                modelCulling, transparent, Filter(RenderQueueRange.transparent));
            var opaqueList = context.CreateRendererList(ref opaqueParameters);
            var transparentList = context.CreateRendererList(ref transparentParameters);
            CommandBuffer cmd = CommandBufferPool.Get("Transition controller models");
            cmd.DrawRendererList(opaqueList);
            cmd.DrawRendererList(transparentList);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore CS0618, CS0672

        public void Dispose()
        {
            compatibilityDepth?.Release();
            compatibilityDepth = null;
        }
    }

    private void BuildUI()
    {
        // Resources keeps the shader included in Android builds even when UI is generated in code.
        Shader shader = Resources.Load<Shader>("ExperienceTransitionOverlay");
        if (shader == null)
        {
            Debug.LogError("Falta Assets/Resources/ExperienceTransitionOverlay.shader.", this);
            return;
        }
        if (!SelectTransitionUiLayer()) return;
        overlayMaterial = new Material(shader) { name = "Experience transition overlay", renderQueue = 5000 };
        canvasRect = NewRect("TransitionCanvas", transform, new Vector2(1200f, 800f));
        canvasRect.localScale = Vector3.one * 0.001f;
        canvas = canvasRect.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = short.MaxValue;
        // A CanvasGroup on the manager or its former UI hierarchy must not make black translucent.
        CanvasGroup opacity = canvasRect.gameObject.AddComponent<CanvasGroup>();
        opacity.alpha = 1f;
        opacity.ignoreParentGroups = true;
        opacity.interactable = false;
        opacity.blocksRaycasts = false;
        // No raycaster, colliders, EventSystem, or additional camera is needed.
        background = NewImage("Background", canvasRect, new Vector2(30000f, 30000f), Color.clear);
        RectTransform panel = NewRect("Content", canvasRect, new Vector2(1000f, 200f));
        content = panel.gameObject;
        RectTransform label = NewRect("Title", panel, new Vector2(1000f, 90f));
        label.anchoredPosition = new Vector2(0f, 40f);
        title = label.gameObject.AddComponent<Text>();
        title.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        title.fontSize = 38;
        title.alignment = TextAnchor.MiddleCenter;
        title.color = Color.white;
        title.raycastTarget = false;
        title.material = overlayMaterial;
        Image track = NewImage("ProgressTrack", panel, new Vector2(680f, 12f),
            new Color(0.2f, 0.2f, 0.2f, 1f));
        track.rectTransform.anchoredPosition = new Vector2(0f, -38f);
        Image fill = NewImage("ProgressFill", track.transform, Vector2.zero, barColor);
        barFill = fill.rectTransform;
        barFill.anchorMin = Vector2.zero;
        barFill.anchorMax = Vector2.zero;
        barFill.offsetMin = Vector2.zero;
        barFill.offsetMax = Vector2.zero;
        SetProgress(0f);
        Hide();
    }

    private bool SelectTransitionUiLayer()
    {
        uint occupied = 0;
        foreach (Transform item in FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (canvasRect != null && item.IsChildOf(canvasRect)) continue;
            occupied |= 1u << item.gameObject.layer;
        }
        // Reuse our slot when the destination has not started using it for something else.
        int chosen = -1;
        if (transitionUiLayer >= 8 && transitionUiLayer <= 30 &&
            string.IsNullOrEmpty(LayerMask.LayerToName(transitionUiLayer)) &&
            (occupied & (1u << transitionUiLayer)) == 0)
            chosen = transitionUiLayer;
        else
            for (int layer = 30; layer >= 8; layer--)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(layer)) ||
                    (occupied & (1u << layer)) != 0) continue;
                chosen = layer;
                break;
            }
        if (chosen < 0)
        {
            Debug.LogError("La transición necesita una capa de usuario sin nombre y sin objetos. " +
                "Libera una capa entre 8 y 30. No se usará la capa UI de los Canvas planos.", this);
            return false;
        }
        transitionUiLayer = chosen;
        transitionUiMask = 1 << chosen;
        if (canvasRect != null)
            foreach (Transform item in canvasRect.GetComponentsInChildren<Transform>(true))
                item.gameObject.layer = chosen;
        return true;
    }

    private RectTransform NewRect(string name, Transform parent, Vector2 size)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.gameObject.layer = transitionUiLayer;
        rect.SetParent(parent, false);
        rect.sizeDelta = size;
        return rect;
    }

    private Image NewImage(string name, Transform parent, Vector2 size, Color color)
    {
        Image image = NewRect(name, parent, size).gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        image.material = overlayMaterial;
        return image;
    }
}
