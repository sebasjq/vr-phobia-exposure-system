using UnityEngine;

[DisallowMultipleComponent]
public class HoldYToExit : MonoBehaviour
{
    [SerializeField, Min(0.1f)] private float holdDuration = 2f;
    [SerializeField] private string fallbackMenuScene = "Main_Menu_Scene";
    [Tooltip("Only needed when testing a level directly, without entering from the menu.")]
    [SerializeField] private string fallbackExposureId = "";

    public float HoldProgress => Mathf.Clamp01(heldTime / holdDuration);
    private float heldTime;
    private bool loading;
    private bool waitingForRelease;

    private void OnEnable()
    {
        heldTime = 0f;
        loading = false;
        // A button already held when entering the scene must be released first.
        waitingForRelease = true;
    }

    private void Update()
    {
        if (loading) return;
        bool pressed = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch);
        if (!pressed)
        {
            heldTime = 0f;
            waitingForRelease = false;
            return;
        }
        if (waitingForRelease) return;
        heldTime += Time.unscaledDeltaTime;
        if (heldTime < holdDuration) return;
        loading = true;
        if (!ExperienceNavigation.ReturnToMenu(fallbackMenuScene, fallbackExposureId))
        {
            loading = false;
            heldTime = 0f;
            waitingForRelease = true;
        }
    }

    private void OnApplicationFocus(bool focused)
    {
        if (focused) return;
        heldTime = 0f;
        waitingForRelease = true;
    }
}
