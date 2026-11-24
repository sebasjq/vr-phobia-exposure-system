using System.Collections;
using UnityEngine;

/// <summary>
/// Una sola variable (Intensity 0..1) controla el Wind Zone (visual)
/// y el parámetro de FMOD (sonido), para que siempre estén sincronizados.
/// </summary>
public class WindController : MonoBehaviour
{
    [SerializeField] WindZone windZone;

    [Header("Rangos del Wind Zone (intensidad 0 -> 1)")]
    [SerializeField] Vector2 mainRange = new Vector2(0.1f, 0.9f);
    [SerializeField] Vector2 turbulenceRange = new Vector2(0.1f, 0.35f);
    [SerializeField] Vector2 pulseMagnitudeRange = new Vector2(0f, 0.6f);

    [SerializeField] string audioParameter = "WindIntensity";

    public float Intensity { get; private set; }

    Coroutine ramp;

    void Reset()
    {
        windZone = GetComponent<WindZone>();
    }

    public void SetIntensity(float value)
    {
        if (ramp != null) { StopCoroutine(ramp); ramp = null; }
        Apply(value);
    }

    /// <summary>Transición suave. Se puede esperar con: yield return wind.RampTo(...)</summary>
    public Coroutine RampTo(float target, float duration)
    {
        if (ramp != null) StopCoroutine(ramp);
        if (duration <= 0f) { Apply(target); ramp = null; return null; }
        ramp = StartCoroutine(RampRoutine(target, duration));
        return ramp;
    }

    IEnumerator RampRoutine(float target, float duration)
    {
        float start = Intensity;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            Apply(Mathf.Lerp(start, target, Mathf.SmoothStep(0f, 1f, t / duration)));
            yield return null;
        }
        Apply(target);
        ramp = null;
    }

    void Apply(float value)
    {
        Intensity = Mathf.Clamp01(value);

        if (windZone != null)
        {
            windZone.windMain = Mathf.Lerp(mainRange.x, mainRange.y, Intensity);
            windZone.windTurbulence = Mathf.Lerp(turbulenceRange.x, turbulenceRange.y, Intensity);
            windZone.windPulseMagnitude = Mathf.Lerp(pulseMagnitudeRange.x, pulseMagnitudeRange.y, Intensity);
        }

        AudioBridge.SetGlobalParameter(audioParameter, Intensity);
    }
}