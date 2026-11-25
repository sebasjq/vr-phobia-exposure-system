using System.Collections;
using UnityEngine;
using FMODUnity;

public class ElevatorFailureEffects : MonoBehaviour
{
    [Header("Objeto que recibirá la sacudida")]
    [SerializeField] private Transform elevatorVisual;

    [Header("Luz de emergencia")]
    [SerializeField] private EmergencyLightController emergencyLight;
    [SerializeField] private EmergencyLightController emergencyLight2;

    [Header("Humo")]
    [SerializeField] private ParticleSystem elevatorSmoke;
    [SerializeField] private ParticleSystem elevatorSmoke2;
    [SerializeField] private ParticleSystem elevatorSmoke3;
    [SerializeField] private ParticleSystem elevatorSmoke4;
    [SerializeField] private float smokeDelay = 1.5f;

    [Header("Alarma FMOD (el emitter solo da el evento y la posición)")]
    [SerializeField] private StudioEventEmitter alarmEmitter;
    [Tooltip("Si la alarma es un loop nunca termina sola: pasado este tiempo se corta y arranca el fallo")]
    [SerializeField] private float alarmMaxSeconds = 6f;
    [Tooltip("Pausa entre que termina la alarma y empieza el estruendo")]
    [SerializeField] private float delayAfterAlarm = 0.3f;

    [Header("Estruendo FMOD")]
    [SerializeField] private StudioEventEmitter impactEmitter;

    [Header("Intensidad de la sacudida")]
    [SerializeField] private float duration = 0.55f;
    [SerializeField] private float positionIntensity = 0.040f;
    [SerializeField] private float rotationIntensity = 0.9f;

    private Vector3 initialPosition;
    private Quaternion initialRotation;
    private bool isShaking;
    private bool sequenceRunning;

    private void Awake()
    {
        if (elevatorVisual == null)
        {
            Debug.LogError("Asigna el objeto visual del ascensor.", this);
            return;
        }
        initialPosition = elevatorVisual.localPosition;
        initialRotation = elevatorVisual.localRotation;
    }

    [ContextMenu("Probar secuencia de falla")]
    public void PlayFailureShake()
    {
        if (sequenceRunning) return;
        StartCoroutine(FailureSequence());
    }

    private IEnumerator FailureSequence()
    {
        sequenceRunning = true;

        // 1) Suena la alarma y se espera a que termine
        yield return StartCoroutine(PlayAlarmAndWait());

        if (delayAfterAlarm > 0f)
            yield return new WaitForSeconds(delayAfterAlarm);

        // 2) Al terminar la alarma: estruendo, luces, humo y sacudida
        PlayEmitter(impactEmitter, "Estruendo");

        if (emergencyLight != null) emergencyLight.StartFlickering();
        if (emergencyLight2 != null) emergencyLight2.StartFlickering();

        LaunchSmoke(elevatorSmoke);
        LaunchSmoke(elevatorSmoke2);
        LaunchSmoke(elevatorSmoke3);
        LaunchSmoke(elevatorSmoke4);

        if (elevatorVisual != null)
            yield return StartCoroutine(Shake());
        else
            Debug.LogError("[Falla] No hay elevatorVisual asignado: no se puede sacudir.", this);

        sequenceRunning = false;
    }

    private IEnumerator PlayAlarmAndWait()
    {
        if (alarmEmitter == null || alarmEmitter.EventReference.IsNull)
        {
            Debug.LogError("[Falla] La alarma no tiene emitter o el emitter no tiene evento. Se salta la alarma.", this);
            yield break;
        }

        FMOD.Studio.EventInstance alarm = default;
        bool ok = true;
        try
        {
            alarm = RuntimeManager.CreateInstance(alarmEmitter.EventReference);
            alarm.set3DAttributes(RuntimeUtils.To3DAttributes(alarmEmitter.gameObject));
            alarm.start();
        }
        catch (System.Exception e)
        {
            Debug.LogError("[Falla] No se pudo iniciar la alarma (banco o evento no encontrado): " + e.Message, this);
            ok = false;
        }
        if (!ok) yield break;

        Debug.Log("[Falla] Alarma iniciada");

        float t = 0f;
        FMOD.Studio.PLAYBACK_STATE state;
        alarm.getPlaybackState(out state);

        while (state != FMOD.Studio.PLAYBACK_STATE.STOPPED && t < alarmMaxSeconds)
        {
            t += Time.deltaTime;
            yield return null;
            alarm.getPlaybackState(out state);
        }

        if (state != FMOD.Studio.PLAYBACK_STATE.STOPPED)
        {
            Debug.LogWarning("[Falla] La alarma no termino sola en " + alarmMaxSeconds +
                             " s (es un loop). Se corta y sigue la secuencia.", this);
            alarm.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
        }
        else if (t < 0.3f)
        {
            Debug.LogWarning("[Falla] La alarma termino casi al instante (" + t.ToString("F2") +
                             " s). El evento esta vacio o el WAV no cargo.", this);
        }
        else
        {
            Debug.Log("[Falla] La alarma termino tras " + t.ToString("F2") + " s");
        }

        alarm.release();
    }

    private void PlayEmitter(StudioEventEmitter e, string nombre)
    {
        if (e == null) { Debug.LogWarning("No se asignó el emisor de " + nombre + ".", this); return; }
        if (e.EventReference.IsNull) { Debug.LogError("El emisor de " + nombre + " no tiene evento asignado.", e); return; }
        e.Play();
        Debug.Log("[Falla] " + nombre + ": Play");
    }

    private void LaunchSmoke(ParticleSystem smoke)
    {
        if (smoke != null) StartCoroutine(StartSmoke(smoke));
    }

    private IEnumerator StartSmoke(ParticleSystem smoke)
    {
        yield return new WaitForSeconds(smokeDelay);
        if (smoke != null) smoke.Play();
    }

    private IEnumerator Shake()
    {
        isShaking = true;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            float intensity = 1f - (elapsed / duration);

            elevatorVisual.localPosition = initialPosition +
                Random.insideUnitSphere * positionIntensity * intensity;

            elevatorVisual.localRotation = initialRotation *
                Quaternion.Euler(
                    Random.Range(-rotationIntensity, rotationIntensity) * intensity,
                    Random.Range(-rotationIntensity, rotationIntensity) * intensity,
                    Random.Range(-rotationIntensity, rotationIntensity) * intensity
                );

            elapsed += Time.deltaTime;
            yield return null;
        }

        elevatorVisual.localPosition = initialPosition;
        elevatorVisual.localRotation = initialRotation;
        isShaking = false;
    }
}