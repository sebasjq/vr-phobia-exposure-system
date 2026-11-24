using UnityEngine;

// Keeps the existing scene references: Y exits, X returns to ExperienceSpawn.
[DisallowMultipleComponent]
public class HoldYToExit : MonoBehaviour
{
    [Header("Mantener Y para salir")]
    [SerializeField, Min(0.1f)] private float holdDuration = 2f;
    [SerializeField] private string fallbackMenuScene = "Main_Menu_Scene";
    [Tooltip("Only needed when testing a level directly, without entering from the menu.")]
    [SerializeField] private string fallbackExposureId = "";

    [Header("Mantener X para volver al inicio")]
    [SerializeField] private bool enableRestart = true;
    [SerializeField, Min(0.1f)] private float restartHoldDuration = 3f;
    [Tooltip("Opcional si ExperienceSpawn está en este rig o en su jerarquía.")]
    [SerializeField] private ExperienceSpawn experienceSpawn;

    private enum HoldAction { None, Exit, Restart }
    private HoldAction action;
    public float HoldProgress => Mathf.Clamp01(heldTime / Mathf.Max(0.1f,
        action == HoldAction.Restart ? restartHoldDuration : holdDuration));
    private float heldTime;
    private bool loading;
    private bool waitingForRelease;
    private bool hasFocus = true;
    private bool paused;
    private bool warnedMissingSpawn;

    private void OnEnable()
    {
        heldTime = 0f;
        action = HoldAction.None;
        loading = false;
        waitingForRelease = true;
        warnedMissingSpawn = false;
    }

    private void Update()
    {
        if (!hasFocus || paused) return;
        if (loading)
        {
            if (!ExperienceTransition.IsBusy)
            {
                loading = false;
                ResetHold();
            }
            return;
        }
        if (ExperienceTransition.IsBusy)
        {
            ResetHold();
            return;
        }

        bool yPressed = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch);
        bool xPressed = enableRestart && OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch);
        if (!xPressed && !yPressed)
        {
            ResetHold();
            waitingForRelease = false;
            return;
        }
        // A new hold always starts after releasing both buttons. Never combine their progress.
        if (waitingForRelease) return;
        if (xPressed && yPressed)
        {
            ResetHold();
            return;
        }

        HoldAction requested = xPressed ? HoldAction.Restart : HoldAction.Exit;
        if (action != HoldAction.None && action != requested)
        {
            ResetHold();
            return;
        }
        if (requested == HoldAction.Restart && !ResolveSpawn())
        {
            ResetHold();
            return;
        }

        action = requested;
        heldTime += Time.unscaledDeltaTime;
        if (action == HoldAction.Restart) ExperienceTransition.ShowRestartProgress(HoldProgress);
        else ExperienceTransition.ShowExitProgress(HoldProgress);
        if (HoldProgress < 1f) return;

        loading = true;
        bool started = action == HoldAction.Restart
            ? ExperienceTransition.TryRespawn(experienceSpawn)
            : ExperienceNavigation.ReturnToMenu(fallbackMenuScene, fallbackExposureId);
        if (!started)
        {
            loading = false;
            ResetHold();
        }
    }

    private bool ResolveSpawn()
    {
        if (experienceSpawn == null) experienceSpawn = GetComponent<ExperienceSpawn>();
        if (experienceSpawn == null) experienceSpawn = GetComponentInParent<ExperienceSpawn>();
        if (experienceSpawn == null) experienceSpawn = GetComponentInChildren<ExperienceSpawn>();
        if (experienceSpawn != null && experienceSpawn.CanRespawn) return true;
        if (!warnedMissingSpawn)
        {
            Debug.LogWarning("Para reiniciar con X, añade ExperienceSpawn al OVRCameraRig " +
                "y espera a que termine su colocación inicial.", this);
            warnedMissingSpawn = true;
        }
        return false;
    }

    private void ResetHold()
    {
        if (action != HoldAction.None) ExperienceTransition.CancelExitProgress();
        heldTime = 0f;
        action = HoldAction.None;
        waitingForRelease = true;
    }

    private void OnDisable() { ResetHold(); }

    private void OnApplicationFocus(bool focused)
    {
        hasFocus = focused;
        if (!focused) ResetHold();
    }

    private void OnApplicationPause(bool paused)
    {
        this.paused = paused;
        if (paused) ResetHold();
    }
}

