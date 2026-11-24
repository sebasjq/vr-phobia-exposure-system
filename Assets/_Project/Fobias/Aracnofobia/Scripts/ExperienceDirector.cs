using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Orquesta las 3 fases. Todos los tiempos y valores se ajustan desde el Inspector.
///
/// Fase 1: niebla mínima, sin araña, el viento sube PASO A PASO.
/// Fase 2: la niebla sube de golpe, la araña (invisible) rodea al jugador de lejos.
/// Fase 3: niebla máxima. La araña sigue en ESPIRAL (rodeando y acercándose sin pausas), se
///         revela fuera de la vista del jugador y, cuando éste la ve, corre directo hacia él,
///         ataca y termina la experiencia.
///
/// Parámetros globales que recibe FMOD:
///   WindIntensity (0-1), FogDensity (0-1), ExperiencePhase (1-4), SpiderDistance (metros)
/// </summary>
public class ExperienceDirector : MonoBehaviour
{
    public enum Phase { NotStarted = 0, Phase1 = 1, Phase2 = 2, Phase3 = 3, Ended = 4 }

    [Header("Referencias")]
    [SerializeField] FogController fog;
    [SerializeField] WindController wind;
    [SerializeField] SpiderController spider;

    [Header("Fase 1 - Calma")]
    [SerializeField] float startDelay = 3f;
    [SerializeField] float phase1FogDensity = 0.004f;
    [Tooltip("Cada valor es un escalón de viento (0-1). Sube de uno en uno.")]
    [SerializeField] float[] windSteps = { 0.1f, 0.2f, 0.3f, 0.45f, 0.6f };
    [SerializeField] float windStepRampTime = 3f;
    [SerializeField] float windStepHoldTime = 7f;

    [Header("Fase 2 - La araña se hace sentir")]
    [SerializeField] float phase2FogDensity = 0.03f;
    [Tooltip("Corto = subida abrupta.")]
    [SerializeField] float phase2FogRiseTime = 2f;
    [Range(0f, 1f)][SerializeField] float phase2Wind = 0.7f;
    [SerializeField] float phase2Duration = 45f;
    [SerializeField] float phase2StartRadius = 25f;
    [SerializeField] float phase2EndRadius = 17f;

    [Header("Fase 3 - Cierre")]
    [SerializeField] float phase3FogDensity = 0.08f;
    [SerializeField] float phase3FogRiseTime = 10f;
    [Range(0f, 1f)][SerializeField] float phase3Wind = 0.9f;
    [Tooltip("Radio final de la espiral, cerca del jugador.")]
    [SerializeField] float phase3FinalRadius = 3f;
    [Tooltip("Duración de la espiral en la fase 3: del radio con que acaba la fase 2 hasta el radio final.")]
    [SerializeField] float phase3Duration = 45f;
    [Tooltip("Segundos desde el inicio de la fase 3 para revelar la araña (solo se revela fuera de la vista del jugador).")]
    [SerializeField] float phase3RevealDelay = 12f;
    [Tooltip("Si el jugador nunca la ve, segundos extra tras terminar la espiral antes de que ataque igual.")]
    [SerializeField] float phase3FinalWait = 6f;
    [Tooltip("Pausa entre el mordisco y el fin de la experiencia.")]
    [SerializeField] float endDelay = 1.5f;

    [Header("Eventos")]
    public UnityEvent<int> onPhaseChanged;
    public UnityEvent onExperienceEnded;     // aquí conectas fundido a negro, créditos, volver al menú...

    public Phase Current { get; private set; } = Phase.NotStarted;

    Coroutine run;
    bool bitten;

    void Start()
    {
        if (fog == null || wind == null || spider == null)
        {
            Debug.LogError(
                "[ExperienceDirector] Faltan referencias en el Inspector: " +
                (fog == null ? "Fog " : "") +
                (wind == null ? "Wind " : "") +
                (spider == null ? "Spider " : "") +
                "(arrástralas al componente ExperienceDirector).", this);
            enabled = false;
            return;
        }

        spider.onBite.AddListener(() => bitten = true);
        StartAt(1);
    }

    void Update()
    {
        if (Current >= Phase.Phase2 && Current < Phase.Ended)
            AudioBridge.SetGlobalParameter("SpiderDistance", spider.DistanceToPlayer);
    }

    /// <summary>Empieza (o salta) a la fase indicada: 1, 2 o 3.</summary>
    public void StartAt(int phase)
    {
        if (run != null) StopCoroutine(run);
        run = StartCoroutine(RunFrom(Mathf.Clamp(phase, 1, 3)));
    }

    // Para probar sin esperar: clic derecho sobre el componente en el Inspector (en Play).
    [ContextMenu("Saltar a Fase 1")] void SkipTo1() => StartAt(1);
    [ContextMenu("Saltar a Fase 2")] void SkipTo2() => StartAt(2);
    [ContextMenu("Saltar a Fase 3")] void SkipTo3() => StartAt(3);

    IEnumerator RunFrom(int from)
    {
        if (from <= 1) yield return Phase1();
        if (from <= 2) yield return Phase2();
        if (from <= 3) yield return Phase3();

        SetPhase(Phase.Ended);
        onExperienceEnded.Invoke();
    }

    // ---------------------------------------------------------------- Fases

    IEnumerator Phase1()
    {
        SetPhase(Phase.Phase1);
        spider.Hide();
        fog.SetDensity(phase1FogDensity);
        wind.SetIntensity(0f);

        yield return new WaitForSeconds(startDelay);

        foreach (float level in windSteps)
        {
            yield return wind.RampTo(level, windStepRampTime);
            yield return new WaitForSeconds(windStepHoldTime);
        }
    }

    IEnumerator Phase2()
    {
        SetPhase(Phase.Phase2);

        fog.FadeTo(phase2FogDensity, phase2FogRiseTime);   // subida abrupta
        wind.RampTo(phase2Wind, 6f);

        spider.BeginOrbit(phase2StartRadius, false);        // invisible, solo se oye
        spider.ChangeOrbitRadius(phase2EndRadius, phase2Duration);

        yield return new WaitForSeconds(phase2Duration);
    }

    IEnumerator Phase3()
    {
        SetPhase(Phase.Phase3);

        fog.FadeTo(phase3FogDensity, phase3FogRiseTime);
        wind.RampTo(phase3Wind, 10f);

        // Si se saltó directo a la fase 3, la araña aún no orbita.
        if (!spider.IsOrbiting)
            spider.BeginOrbit(phase2EndRadius, false);

        // La espiral continúa sin pausas: el radio baja poco a poco hasta el radio final.
        bitten = false;
        spider.ChangeOrbitRadius(phase3FinalRadius, phase3Duration);

        // Pasado un tiempo se revela, pero solo cuando está fuera de la vista del jugador.
        yield return new WaitForSeconds(phase3RevealDelay);
        spider.RevealOutOfView();

        // Si el jugador la ve, la araña corre hacia él por su cuenta (SpiderController).
        // Si nunca la ve, ataca al terminar la espiral más la espera final.
        float timeout = Mathf.Max(0f, phase3Duration - phase3RevealDelay) + phase3FinalWait;
        float waited = 0f;
        while (!bitten && waited < timeout)
        {
            waited += Time.deltaTime;
            yield return null;
        }

        if (!bitten) spider.Charge();
        yield return new WaitUntil(() => bitten);

        yield return new WaitForSeconds(endDelay);
    }

    void SetPhase(Phase phase)
    {
        Current = phase;
        AudioBridge.SetGlobalParameter("ExperiencePhase", (float)phase);
        onPhaseChanged.Invoke((int)phase);
    }
}
