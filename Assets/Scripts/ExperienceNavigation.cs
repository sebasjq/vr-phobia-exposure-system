using UnityEngine;
using UnityEngine.SceneManagement;

// Session state shared across scenes. No persistent GameObject is required.
public static class ExperienceNavigation
{
    public static string MenuScene { get; private set; }
    public static int MenuView { get; private set; }
    public static string ExposureId { get; private set; }
    public static float? MenuEyeHeight { get; private set; }
    private static bool returnRequested;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetSession()
    {
        MenuScene = null;
        MenuView = 0;
        ExposureId = "";
        MenuEyeHeight = null;
        returnRequested = false;
    }

    public static bool CanLoad(string scene)
    {
        if (!string.IsNullOrWhiteSpace(scene) && Application.CanStreamedLevelBeLoaded(scene))
            return true;
        Debug.LogError("No se puede cargar la escena '" + scene + "'. Revisa Scene List del Build Profile.");
        return false;
    }

    public static void Enter(string experienceScene, string menuScene, int menuView, string exposureId,
        float? menuEyeHeight = null)
    {
        if (!CanLoad(experienceScene) || !CanLoad(menuScene)) return;
        MenuScene = menuScene;
        MenuView = menuView;
        ExposureId = exposureId ?? "";
        MenuEyeHeight = menuEyeHeight;
        returnRequested = false;
        Time.timeScale = 1f;
        SceneManager.LoadScene(experienceScene);
    }

    public static bool ReturnToMenu(string fallbackScene, string fallbackExposureId)
    {
        bool hasOrigin = !string.IsNullOrEmpty(MenuScene);
        string destination = hasOrigin ? MenuScene : fallbackScene;
        if (!CanLoad(destination)) return false;
        if (!hasOrigin)
        {
            MenuScene = destination;
            MenuView = 0;
            ExposureId = fallbackExposureId ?? "";
            MenuEyeHeight = null;
        }
        returnRequested = true;
        Time.timeScale = 1f;
        SceneManager.LoadScene(destination);
        return true;
    }

    public static bool ConsumeReturn(Scene menuScene)
    {
        if (!returnRequested || (MenuScene != menuScene.path && MenuScene != menuScene.name))
            return false;
        returnRequested = false;
        return true;
    }
}
