
using System.Collections;
using UnityEngine;

public class EmergencyLightController : MonoBehaviour
{
    [Header("Luz de emergencia")]
    [SerializeField] private Light emergencyLight;

    [Header("Indicador visual")]
    [SerializeField] private Renderer emergencyVisual;

    [Header("Parpadeo")]
    [SerializeField] private float minIntensity = 1f;
    [SerializeField] private float maxIntensity = 10f;
    [SerializeField] private float minInterval = 0.02f;
    [SerializeField] private float maxInterval = 0.07f;

    private Coroutine flickerRoutine;
    private Material visualMaterial;

    private void Awake()
    {
        if (emergencyLight == null)
            emergencyLight = GetComponent<Light>();

        if (emergencyVisual != null)
        {
            visualMaterial = emergencyVisual.material;
            visualMaterial.EnableKeyword("_EMISSION");
            visualMaterial.SetColor("_EmissionColor", Color.black);
        }

        if (emergencyLight != null)
            emergencyLight.enabled = false;
    }

    public void StartFlickering()
    {
        if (emergencyLight == null || flickerRoutine != null)
            return;

        emergencyLight.enabled = true;
        flickerRoutine = StartCoroutine(Flicker());
    }

    private IEnumerator Flicker()
    {
        while (true)
        {
            float intensity = Random.Range(minIntensity, maxIntensity);

            emergencyLight.intensity = intensity;

            if (visualMaterial != null)
                visualMaterial.SetColor("_EmissionColor", Color.red * intensity);

            yield return new WaitForSeconds(
                Random.Range(minInterval, maxInterval)
            );
        }
    }
}
