using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using UnityEngine.UI;

// One persistent, head-following world-space Canvas for both VR eyes.
[DisallowMultipleComponent]
[DefaultExecutionOrder(10000)]
public sealed class ExperienceTransition : MonoBehaviour
{
    [Header("Fundidos (segundos)")]
    [SerializeField, Min(0f)] private float fadeOutDuration = 0.65f;
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
    [Tooltip("Espera fija desde la selección antes de silenciar o detener audio. Permite oír el clic completo.")]
    [FormerlySerializedAs("uiAudioFinishTimeout")]
    [SerializeField, Range(1f, 3f)] private float uiClickGraceDuration = 2f;

    [Header("Aspecto")]
    [SerializeField] private Color barColor = new Color(0.85f, 0.85f, 0.85f, 1f);
    [SerializeField] private string loadingText = "Cargando Experiencia...";
    [SerializeField] private string leavingText = "Abandonando Experiencia...";
    [SerializeField, Min(0.6f)] private float panelDistance = 1f;

    private enum State { Idle, Holding, Loading }
    private static ExperienceTransition instance;
    public static bool IsBusy => instance != null && instance.state == State.Loading &&
        !instance.waitingForClickGrace;

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
    private bool audioHeld;
    private bool revealingAudio;
    private bool waitingForClickGrace;
    private float savedListenerVolume;
    private FMOD.Studio.Bus masterAudioBus;
    private bool fmodAudioCaptured;
    private float savedFmodVolume;
    private bool savedFmodMuted;
    private float nextFmodCaptureTime;
    private readonly List<FMOD.Studio.EventDescription> uiAudioEvents =
        new List<FMOD.Studio.EventDescription>();
    private readonly List<AudioSource> uiAudioSources = new List<AudioSource>();

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
        // Feedback remains allowed during the grace period; the private latch rejects duplicate loads.
        transition.waitingForClickGrace = showLoadingBar;
        transition.canvas.enabled = true;
        // Lock synchronously, before the first coroutine frame, to reject repeated clicks.
        transition.StartCoroutine(transition.LoadScene(scene, showLoadingBar, wasHolding, onFailure));
        return true;
    }

    private IEnumerator LoadScene(string scene, bool showLoadingBar, bool wasHolding, Action onFailure)
    {
        float selectedAt = Time.unscaledTime;
        CacheUiAudio();
        if (wasHolding)
        {
            SetProgress(1f);
            yield return null;
        }
        content.SetActive(false);
        yield return FadeBackground(Color.black, fadeOutDuration);
        keepPreparationBlack = true;
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
            content.SetActive(false);
            keepPreparationBlack = false;
            BeginAudioReveal();
            yield return FadeBackground(Color.clear, fadeInDuration, true);
            Hide();
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
        yield return WaitForScenePreparation();
        if (showLoadingBar)
        {
            while (Time.unscaledTime - loadingStarted < Mathf.Max(0f, minimumLoadingDuration))
                yield return null;
            yield return AnimateProgress(0.25f, 1f, completionFillDuration);
            yield return WaitUnscaled(completedBarHoldDuration);
            content.SetActive(false);
        }
        // The destination is already active and prepared. Remove all loading UI first,
        // but keep both audio engines silent until this plain-black phase has finished.
        background.color = Color.black;
        yield return null;
        yield return WaitUnscaled(showLoadingBar ? entryBlackPause : exitBlackPause);
        keepPreparationBlack = false;
        BeginAudioReveal();
        yield return FadeBackground(Color.clear, fadeInDuration, true);
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

    private IEnumerator CancelHold()
    {
        // Wait one frame so even a duration of zero leaves a cancellable coroutine handle.
        yield return null;
        content.SetActive(false);
        yield return FadeBackground(Color.clear, cancelDuration);
        Hide();
        cancelRoutine = null;
    }

    private void StopCancellation()
    {
        if (cancelRoutine == null) return;
        StopCoroutine(cancelRoutine);
        cancelRoutine = null;
    }

    private IEnumerator FadeBackground(Color target, float seconds, bool revealAudio = false)
    {
        Color start = background.color;
        if (seconds > 0f)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / seconds));
                background.color = Color.Lerp(start, target, t);
                if (revealAudio) SetAudioRevealGain(t);
                yield return null;
            }
        }
        background.color = target;
        if (revealAudio) RestoreAudio();
    }

    private void Hide()
    {
        keepPreparationBlack = false;
        content.SetActive(false);
        background.color = Color.clear;
        canvas.enabled = false;
        state = State.Idle;
        waitingForClickGrace = false;
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
        background.canvasRenderer.SetAlpha(1f);
    }

    private void OnDisable() { RestoreAudio(); }

    private void HoldAudioForLoading()
    {
        if (!audioHeld)
        {
            savedListenerVolume = AudioListener.volume;
            audioHeld = true;
            revealingAudio = false;
            fmodAudioCaptured = false;
            nextFmodCaptureTime = 0f;
        }
        MaintainLoadingSilence();
    }

    private IEnumerator FinishOutgoingUiAudio(float selectedAt)
    {
        // Let all listeners of the current Button.onClick run, regardless of their order.
        yield return null;
        float duration = Mathf.Clamp(uiClickGraceDuration, 1f, 3f);
        while (Time.unscaledTime - selectedAt < duration)
            yield return null;
        waitingForClickGrace = false;
        // A fixed grace period avoids racing FMOD's asynchronous playback-state queries.
        StopUiAudio();
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
        if (uiAudioManager != null) CacheUiAudioFrom(uiAudioManager);
        foreach (MonoBehaviour component in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (component == null || component == uiAudioManager) continue;
            string typeName = component.GetType().Name;
            if (typeName == "UIAudioManager" || typeName == "UI_AudioManager")
                CacheUiAudioFrom(component);
        }
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
            masterAudioBus.setMute(true);
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
        if (!audioHeld) return;
        StopUiAudio();
        CaptureFmodAudio();
        revealingAudio = true;
        // Recover output gain along with the visual fade, without reviving stopped voices.
        AudioListener.volume = 0f;
        if (fmodAudioCaptured && masterAudioBus.isValid())
        {
            masterAudioBus.setVolume(0f);
            masterAudioBus.setMute(savedFmodMuted);
        }
    }

    private void SetAudioRevealGain(float gain)
    {
        if (!audioHeld) return;
        gain = Mathf.Clamp01(gain);
        AudioListener.volume = savedListenerVolume * gain;
        if (fmodAudioCaptured && masterAudioBus.isValid())
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
        foreach (KeyValuePair<AudioSource, AudioBypassState> saved in audioBypassStates)
        {
            if (saved.Key == null) continue;
            saved.Key.ignoreListenerVolume = saved.Value.ignoreVolume;
        }
        audioBypassStates.Clear();
        uiAudioEvents.Clear();
        uiAudioSources.Clear();
        audioHeld = false;
        revealingAudio = false;
        fmodAudioCaptured = false;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (audioHeld && !revealingAudio) MaintainLoadingSilence();
        viewerCamera = null;
        viewerRig = null;
        viewerAnchor = null;
        FollowCamera();
    }

    private void FollowCamera()
    {
        if (!Ready || state == State.Idle) return;
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
            // Use a layer already included in this camera, without editing its culling mask.
            int layer = 5;
            if ((viewerCamera.cullingMask & (1 << layer)) == 0)
            {
                for (layer = 0; layer < 32; layer++)
                    if ((viewerCamera.cullingMask & (1 << layer)) != 0) break;
                if (layer == 32) layer = 0;
            }
            foreach (Transform child in canvasRect.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = layer;
        }
        Transform eye = viewerAnchor;
        float distance = Mathf.Max(panelDistance, viewerCamera.nearClipPlane + 0.2f);
        canvasRect.SetPositionAndRotation(eye.position + eye.forward * distance, eye.rotation);
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
        bool trackedEye = viewerRig != null && renderingCamera != null &&
            renderingCamera.GetComponentInParent<OVRCameraRig>() == viewerRig;
        canvas.enabled = renderingCamera == viewerCamera || trackedEye;
    }

    private void RestoreCanvasVisibility()
    {
        if (Ready) canvas.enabled = state != State.Idle;
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        SetVisibilityForCamera(renderingCamera);
    }

    private void OnEndCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        RestoreCanvasVisibility();
    }

    private void OnLegacyCameraPreCull(Camera renderingCamera)
    {
        if (GraphicsSettings.currentRenderPipeline == null) SetVisibilityForCamera(renderingCamera);
    }

    private void OnLegacyCameraPostRender(Camera renderingCamera)
    {
        if (GraphicsSettings.currentRenderPipeline == null) RestoreCanvasVisibility();
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

    private static RectTransform NewRect(string name, Transform parent, Vector2 size)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
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
