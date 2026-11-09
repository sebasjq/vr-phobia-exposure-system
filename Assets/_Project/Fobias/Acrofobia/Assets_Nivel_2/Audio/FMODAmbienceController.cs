using UnityEngine;
using FMODUnity;
using FMOD.Studio;

public class FMODAmbienceController : MonoBehaviour
{
    [Header("FMOD Ambient Event")]
    [SerializeField]
    private EventReference ambienceEvent;

    private EventInstance ambienceInstance;

    private void Start()
    {
        PlayAmbience();
    }

    public void PlayAmbience()
    {
        // Evitar crear instancias duplicadas
        if (ambienceInstance.isValid())
            return;

        // Crear instancia del evento
        ambienceInstance = RuntimeManager.CreateInstance(ambienceEvent);

        // Reproducir sonido ambiente
        ambienceInstance.start();

        Debug.Log("FMOD: Ambiente iniciado");
    }

    public void StopAmbience()
    {
        if (!ambienceInstance.isValid())
            return;

        // Detener respetando el fade out configurado en FMOD
        ambienceInstance.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);

        // Liberar la instancia
        ambienceInstance.release();

        Debug.Log("FMOD: Ambiente detenido");
    }

    private void OnDestroy()
    {
        StopAmbience();
    }
}