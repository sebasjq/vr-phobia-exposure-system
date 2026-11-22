
using UnityEngine;
using FMODUnity;
using FMOD.Studio;

public class FMODAmbienceController : MonoBehaviour
{
    [Header("FMOD Ambient Event")]
    [SerializeField]
    private EventReference ambienceEvent;

    [Header("Ambience Parameters")]
    [SerializeField]
    private float rain = 0f;

    [SerializeField]
    private float wind = 0f;

    [SerializeField]
    private float cover = 0f;

    private EventInstance ambienceInstance;

    // Valores anteriores para detectar cambios
    private float lastRain;
    private float lastWind;
    private float lastCover;

    private void Start()
    {
        PlayAmbience();
    }

    public void PlayAmbience()
    {
        // Evitar instancias duplicadas
        if (ambienceInstance.isValid())
            return;

        // Crear instancia
        ambienceInstance =
            RuntimeManager.CreateInstance(ambienceEvent);

        // Aplicar parámetros antes de reproducir
        ApplyParameters();

        // Iniciar ambiente
        ambienceInstance.start();

        Debug.Log("FMOD: Ambiente iniciado");
    }

    private void Update()
    {
        if (!ambienceInstance.isValid())
            return;

        // Actualizar únicamente si los valores cambiaron
        if (rain != lastRain ||
            wind != lastWind ||
            cover != lastCover)
        {
            ApplyParameters();
        }
    }

    private void ApplyParameters()
    {
        if (!ambienceInstance.isValid())
            return;

        // Aplicar parámetros a la instancia
        ambienceInstance.setParameterByName(
            "Rain", rain);

        ambienceInstance.setParameterByName(
            "Wind", wind);

        ambienceInstance.setParameterByName(
            "Cover", cover);

        // Guardar los últimos valores aplicados
        lastRain = rain;
        lastWind = wind;
        lastCover = cover;
    }

    // Métodos públicos para otros scripts

    public void SetRain(float value)
    {
        rain = value;
        ApplyParameters();
    }

    public void SetWind(float value)
    {
        wind = value;
        ApplyParameters();
    }

    public void SetCover(float value)
    {
        cover = value;
        ApplyParameters();
    }

    // Cambiar los tres parámetros al mismo tiempo
    public void SetWeatherParameters(
        float newRain,
        float newWind,
        float newCover)
    {
        rain = newRain;
        wind = newWind;
        cover = newCover;

        ApplyParameters();
    }

    public void StopAmbience()
    {
        if (!ambienceInstance.isValid())
            return;

        // Detener con fade out
        ambienceInstance.stop(
            FMOD.Studio.STOP_MODE.ALLOWFADEOUT);

        // Liberar la instancia
        ambienceInstance.release();

        ambienceInstance.clearHandle();

        Debug.Log("FMOD: Ambiente detenido");
    }

    private void OnDestroy()
    {
        StopAmbience();
    }
}
