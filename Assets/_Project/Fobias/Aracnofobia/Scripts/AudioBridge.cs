using UnityEngine;

/// <summary>
/// Único punto de contacto con el audio. Mientras no tengas FMOD instalado,
/// solo registra los valores en consola (si LogCalls está activo).
///
/// Cuando integres FMOD: Project Settings > Player > Scripting Define Symbols
/// y agrega USE_FMOD. Nada más hay que cambiar en los otros scripts.
/// </summary>
public static class AudioBridge
{
    public static bool LogCalls = false;

    /// <summary>Parámetro GLOBAL de FMOD (ej: WindIntensity, FogDensity, ExperiencePhase, SpiderDistance).</summary>
    public static void SetGlobalParameter(string name, float value)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (LogCalls) Debug.Log($"[Audio] {name} = {value:F2}");
#if USE_FMOD
        FMODUnity.RuntimeManager.StudioSystem.setParameterByName(name, value);
#endif
    }
}