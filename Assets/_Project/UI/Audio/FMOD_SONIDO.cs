
using UnityEngine;
using FMODUnity;
using FMOD.Studio;

public class FMODCarEngine : MonoBehaviour
{
    [Header("FMOD Engine Event")]
    [SerializeField]
    private EventReference engineEvent;

    [Header("Car Animation")]
    [SerializeField]
    private Transform carRoot;

    [Tooltip("Velocidad máxima del auto en km/h")]
    [SerializeField]
    private float maxSpeed = 120f;

    [Tooltip("Aceleración necesaria para alcanzar Load máximo")]
    [SerializeField]
    private float maxAcceleration = 5f;

    [Header("RPM")]
    [SerializeField]
    private float minRPM = 800f;

    [SerializeField]
    private float maxRPM = 7000f;

    [Header("Response")]
    [SerializeField]
    private float responseSpeed = 3f;

    private EventInstance engineInstance;

    private Vector3 previousPosition;

    private float currentSpeed;
    private float currentAcceleration;

    private float currentRPM;
    private float currentLoad;

    private float targetRPM;
    private float targetLoad;

    private void Start()
    {
        if (carRoot == null)
            carRoot = transform;

        previousPosition = carRoot.position;

        currentRPM = minRPM;
        targetRPM = minRPM;

        engineInstance =
            RuntimeManager.CreateInstance(engineEvent);

        ApplyParameters();

        engineInstance.start();
    }

    private void Update()
    {
        if (!engineInstance.isValid())
            return;

        float deltaTime = Time.deltaTime;

        if (deltaTime <= 0f)
            return;

        // Calcular desplazamiento del auto
        Vector3 displacement =
            carRoot.position - previousPosition;

        previousPosition = carRoot.position;

        // Calcular velocidad en km/h
        currentSpeed =
            displacement.magnitude / deltaTime * 3.6f;

        // Calcular aceleración en m/s²
        float speedMetersPerSecond =
            currentSpeed / 3.6f;

        currentAcceleration =
            (speedMetersPerSecond -
            previousSpeedMetersPerSecond) / deltaTime;

        previousSpeedMetersPerSecond =
            speedMetersPerSecond;

        // RPM según la velocidad
        float speedNormalized =
            Mathf.Clamp01(currentSpeed / maxSpeed);

        targetRPM = Mathf.Lerp(
            minRPM,
            maxRPM,
            speedNormalized
        );

        // Load según la aceleración positiva
        float accelerationNormalized =
            Mathf.Clamp01(
                currentAcceleration / maxAcceleration
            );

        targetLoad = accelerationNormalized;

        // Suavizar los parámetros
        float smooth =
            1f - Mathf.Exp(-responseSpeed * deltaTime);

        currentRPM = Mathf.Lerp(
            currentRPM,
            targetRPM,
            smooth
        );

        currentLoad = Mathf.Lerp(
            currentLoad,
            targetLoad,
            smooth
        );

        ApplyParameters();
    }

    private float previousSpeedMetersPerSecond;

    private void ApplyParameters()
    {
        if (!engineInstance.isValid())
            return;

        engineInstance.setParameterByName(
            "RPM", currentRPM);

        engineInstance.setParameterByName(
            "Load", currentLoad);
    }

    public void StopEngine()
    {
        if (!engineInstance.isValid())
            return;

        engineInstance.stop(
            FMOD.Studio.STOP_MODE.ALLOWFADEOUT);

        engineInstance.release();

        engineInstance.clearHandle();
    }

    private void OnDestroy()
    {
        StopEngine();
    }
}
