
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

    [Header("Alarma FMOD")]
    [SerializeField] private StudioEventEmitter alarmEmitter;

    [Header("Intensidad de la sacudida")]
    [SerializeField] private float duration = 0.55f;
    [SerializeField] private float positionIntensity = 0.040f;
    [SerializeField] private float rotationIntensity = 0.9f;

    private Vector3 initialPosition;
    private Quaternion initialRotation;
    private bool isShaking;

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

    public void PlayFailureShake()
    {
        if (elevatorVisual == null || isShaking)
            return;

        // Reproducir alarma FMOD
        if (alarmEmitter != null)
            alarmEmitter.Play();

        // Iniciar luces de emergencia
        if (emergencyLight != null)
            emergencyLight.StartFlickering();

        if (emergencyLight2 != null)
            emergencyLight2.StartFlickering();

        // Iniciar humo
        if (elevatorSmoke != null)
            StartCoroutine(StartSmoke());

        if (elevatorSmoke2 != null)
            StartCoroutine(StartSmoke2());

        if (elevatorSmoke3 != null)
            StartCoroutine(StartSmoke3());

        if (elevatorSmoke4 != null)
            StartCoroutine(StartSmoke4());

        // Iniciar sacudida
        StartCoroutine(Shake());
    }

    private IEnumerator StartSmoke()
    {
        yield return new WaitForSeconds(smokeDelay);

        if (elevatorSmoke != null)
            elevatorSmoke.Play();
    }

    private IEnumerator StartSmoke2()
    {
        yield return new WaitForSeconds(smokeDelay);

        if (elevatorSmoke2 != null)
            elevatorSmoke2.Play();
    }

    private IEnumerator StartSmoke3()
    {
        yield return new WaitForSeconds(smokeDelay);

        if (elevatorSmoke3 != null)
            elevatorSmoke3.Play();
    }

    private IEnumerator StartSmoke4()
    {
        yield return new WaitForSeconds(smokeDelay);

        if (elevatorSmoke4 != null)
            elevatorSmoke4.Play();
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
