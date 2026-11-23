using UnityEngine;
using UnityEngine.SceneManagement;

// Session state shared across scenes; the transition renderer owns the persistent GameObject.
public static class ExperienceNavigation
{
    public static string MenuScene { get; private set; }
    public static int MenuView { get; private set; }
    public static string ExposureId { get; private set; }
    public static float? MenuEyeHeight { get; private set; }
    private static bool returnRequested;

    private struct SessionSnapshot
    {
        public string scene;
        public int view;
        public string exposure;
        public float? eyeHeight;
        public bool returning;
    }

    private static SessionSnapshot Capture()
    {
        return new SessionSnapshot
        {
            scene = MenuScene, view = MenuView, exposure = ExposureId,
            eyeHeight = MenuEyeHeight, returning = returnRequested
        };
    }

    private static void Restore(SessionSnapshot snapshot)
    {
        MenuScene = snapshot.scene;
        MenuView = snapshot.view;
        ExposureId = snapshot.exposure;
        MenuEyeHeight = snapshot.eyeHeight;
        returnRequested = snapshot.returning;
    }

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

    // Signature preserved: existing CircularMenu buttons keep their current connections.
    public static void Enter(string experienceScene, string menuScene, int menuView, string exposureId,
        float? menuEyeHeight = null)
    {
        if (ExperienceTransition.IsBusy || !CanLoad(experienceScene) || !CanLoad(menuScene)) return;
        SessionSnapshot previous = Capture();
        MenuScene = menuScene;
        MenuView = menuView;
        ExposureId = exposureId ?? "";
        MenuEyeHeight = menuEyeHeight;
        returnRequested = false;
        Time.timeScale = 1f;
        if (!ExperienceTransition.TryLoad(experienceScene, true, () => Restore(previous)))
            Restore(previous);
    }

    public static bool ReturnToMenu(string fallbackScene, string fallbackExposureId)
    {
        if (ExperienceTransition.IsBusy) return false;
        bool hasOrigin = !string.IsNullOrEmpty(MenuScene);
        string destination = hasOrigin ? MenuScene : fallbackScene;
        if (!CanLoad(destination)) return false;
        SessionSnapshot previous = Capture();
        if (!hasOrigin)
        {
            MenuScene = destination;
            MenuView = 0;
            ExposureId = fallbackExposureId ?? "";
            MenuEyeHeight = null;
        }
        returnRequested = true;
        Time.timeScale = 1f;
        if (ExperienceTransition.TryLoad(destination, false, () => Restore(previous)))
            return true;
        Restore(previous);
        return false;
    }

    public static bool ConsumeReturn(Scene menuScene)
    {
        if (!returnRequested || (MenuScene != menuScene.path && MenuScene != menuScene.name))
            return false;
        returnRequested = false;
        return true;
    }
}
