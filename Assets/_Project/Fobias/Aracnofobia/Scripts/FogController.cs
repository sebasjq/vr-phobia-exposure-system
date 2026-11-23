using System.Collections;
using UnityEngine;

/// <summary>
/// Controla la niebla global (RenderSettings). Usa Exponential Squared:
/// la visibilidad cae al 50% a una distancia de ~0.83 / densidad.
///   0.004 -> ~200 m (casi nada)   0.03 -> ~28 m   0.08 -> ~10 m
/// </summary>
public class FogController : MonoBehaviour
{
    [SerializeField] Color fogColor = new Color(0.64f, 0.68f, 0.72f);

    [Tooltip("Densidad que equivale a 1.0 en el parámetro de FMOD.")]
    [SerializeField] float referenceMaxDensity = 0.08f;

    [SerializeField] string audioParameter = "FogDensity";

    public float Density { get; private set; }

    Coroutine fade;

    void Awake()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = fogColor;
        Density = RenderSettings.fogDensity;
    }

    public void SetDensity(float density)
    {
        if (fade != null) { StopCoroutine(fade); fade = null; }
        Apply(density);
    }

    /// <summary>Transición suave. Se puede esperar con: yield return fog.FadeTo(...)</summary>
    public Coroutine FadeTo(float target, float duration)
    {
        if (fade != null) StopCoroutine(fade);
        if (duration <= 0f) { Apply(target); fade = null; return null; }
        fade = StartCoroutine(FadeRoutine(target, duration));
        return fade;
    }

    IEnumerator FadeRoutine(float target, float duration)
    {
        float start = Density;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            Apply(Mathf.Lerp(start, target, Mathf.SmoothStep(0f, 1f, t / duration)));
            yield return null;
        }
        Apply(target);
        fade = null;
    }

    void Apply(float density)
    {
        Density = Mathf.Max(0f, density);
        RenderSettings.fogDensity = Density;
        AudioBridge.SetGlobalParameter(audioParameter, Mathf.Clamp01(Density / referenceMaxDensity));
    }
}