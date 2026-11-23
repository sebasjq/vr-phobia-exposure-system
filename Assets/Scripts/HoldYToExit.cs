using UnityEngine;

[DisallowMultipleComponent]
public class HoldYToExit : MonoBehaviour
{
    [SerializeField, Min(0.1f)] private float holdDuration = 2f;
    [SerializeField] private string fallbackMenuScene = "Main_Menu_Scene";
    [Tooltip("Only needed when testing a level directly, without entering from the menu.")]
    [SerializeField] private string fallbackExposureId = "";

    public float HoldProgress => Mathf.Clamp01(heldTime / Mathf.Max(0.1f, holdDuration));
    private float heldTime;
    private bool loading;
    private bool waitingForRelease;
    private bool hasFocus = true;
    private bool paused;

    private void OnEnable()
    {
        heldTime = 0f;
        loading = false;
        waitingForRelease = true;
    }

    private void Update()
    {
        if (!hasFocus || paused) return;
        if (loading)
        {
            // If async loading fails, this scene remains usable after its recovery fade.
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
        bool pressed = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch);
        if (!pressed)
        {
            if (heldTime > 0f) ExperienceTransition.CancelExitProgress();
            heldTime = 0f;
            waitingForRelease = false;
            return;
        }
        if (waitingForRelease) return;
        heldTime += Time.unscaledDeltaTime;
        ExperienceTransition.ShowExitProgress(HoldProgress);
        if (HoldProgress < 1f) return;
        loading = true;
        if (!ExperienceNavigation.ReturnToMenu(fallbackMenuScene, fallbackExposureId))
        {
            loading = false;
            ResetHold();
        }
    }

    private void ResetHold()
    {
        heldTime = 0f;
        waitingForRelease = true;
        ExperienceTransition.CancelExitProgress();
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
