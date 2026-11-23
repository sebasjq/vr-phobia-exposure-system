
using UnityEngine;
using FMODUnity;
using FMOD.Studio;

public class FMOD_Ambience_Controller : MonoBehaviour
{
    [Header("FMOD City Ambience Event")]
    [SerializeField]
    private EventReference ambienceEvent;

    [Header("City Ambience Parameters")]
    [SerializeField]
    private float traffic = 0f;

    [SerializeField]
    private float walla = 0f;

    [SerializeField]
    private float eventOrien = 0f;

    private EventInstance ambienceInstance;

    // Valores anteriores
    private float lastTraffic;
    private float lastWalla;
    private float lastEventOrien;

    private void Start()
    {
        PlayAmbience();
    }

    public void PlayAmbience()
    {
        // Evitar instancias duplicadas
        if (ambienceInstance.isValid())
            return;

        // Crear instancia del evento
        ambienceInstance =
            RuntimeManager.CreateInstance(ambienceEvent);

        // Aplicar parámetros antes de reproducir
        ApplyParameters();

        // Reproducir sonido ambiente
        ambienceInstance.start();

        Debug.Log("FMOD: Ambiente de ciudad iniciado");
    }

    private void Update()
    {
        if (!ambienceInstance.isValid())
            return;

        // Actualizar solo cuando cambien los valores
        if (traffic != lastTraffic ||
            walla != lastWalla ||
            eventOrien != lastEventOrien)
        {
            ApplyParameters();
        }
    }

    private void ApplyParameters()
    {
        if (!ambienceInstance.isValid())
            return;

        ambienceInstance.setParameterByName(
            "Traffic", traffic);

        ambienceInstance.setParameterByName(
            "Walla", walla);

        ambienceInstance.setParameterByName(
            "Event Orien", eventOrien);

        // Guardar los valores aplicados
        lastTraffic = traffic;
        lastWalla = walla;
        lastEventOrien = eventOrien;
    }

    // Modificar parámetros desde otros scripts

    public void SetTraffic(float value)
    {
        traffic = value;
        ApplyParameters();
    }

    public void SetWalla(float value)
    {
        walla = value;
        ApplyParameters();
    }

    public void SetEventOrien(float value)
    {
        eventOrien = value;
        ApplyParameters();
    }

    // Modificar los tres parámetros simultáneamente
    public void SetCityParameters(
        float newTraffic,
        float newWalla,
        float newEventOrien)
    {
        traffic = newTraffic;
        walla = newWalla;
        eventOrien = newEventOrien;

        ApplyParameters();
    }

    public void StopAmbience()
    {
        if (!ambienceInstance.isValid())
            return;

        // Detener respetando el fade out de FMOD
        ambienceInstance.stop(
            FMOD.Studio.STOP_MODE.ALLOWFADEOUT);

        // Liberar la instancia
        ambienceInstance.release();

        ambienceInstance.clearHandle();

        Debug.Log("FMOD: Ambiente de ciudad detenido");
    }

    private void OnDestroy()
    {
        StopAmbience();
    }
}
