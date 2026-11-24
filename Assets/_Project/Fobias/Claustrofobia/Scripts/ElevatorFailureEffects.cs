
using System.Collections;
using UnityEngine;

public class ElevatorFailureEffects : MonoBehaviour
{
    [Header("Objeto que recibirá la sacudida")]
    [SerializeField] private Transform elevatorVisual;

    [Header("Luz de emergencia")]
    [SerializeField] private EmergencyLightController emergencyLight;

    [Header("Humo")]
    [SerializeField] private ParticleSystem elevatorSmoke;
    [SerializeField] private float smokeDelay = 1.5f;

    [Header("Intensidad de la sacudida")]
    [SerializeField] private float duration = 0.35f;
    [SerializeField] private float positionIntensity = 0.025f;
    [SerializeField] private float rotationIntensity = 0.8f;

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

        if (emergencyLight != null)
            emergencyLight.StartFlickering();

        if (elevatorSmoke != null)
            StartCoroutine(StartSmoke());

        StartCoroutine(Shake());
    }

    private IEnumerator StartSmoke()
    {
        yield return new WaitForSeconds(smokeDelay);

        if (elevatorSmoke != null)
            elevatorSmoke.Play();
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
