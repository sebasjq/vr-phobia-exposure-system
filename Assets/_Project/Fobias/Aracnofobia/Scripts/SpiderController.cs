using System.Collections;
using UnityEngine;
using UnityEngine.Events;

public enum SpiderState { Hidden, Idle, Walk, Run, Attack }

/// <summary>
/// Araña con 4 animaciones: Idle, Walk, Run, Attack.
///
/// Comportamiento:
///  - ESPIRAL CONTINUA: rodea al jugador sin parar (animación Walk) mientras el radio baja poco a poco.
///    El radio lo controla el Director con ChangeOrbitRadius(). Puede estar INVISIBLE (solo se oye);
///    el GameObject sigue activo para que el sonido de sus patas se escuche.
///  - VISTA: cuando está visible y el jugador la ve (la cabeza, es decir el CenterEyeAnchor del
///    OVRCameraRig, la tiene dentro de su campo de visión central), cambia a Run y va DIRECTO al
///    jugador. Al llegar, ataca (Attack) y se dispara onBite.
///
/// IMPORTANTE: este script debe estar en el MISMO objeto que el Animator
/// si quieres usar Animation Events (AnimEvent_Bite / AnimEvent_Footstep).
/// </summary>
public class SpiderController : MonoBehaviour
{
    enum Mode { None, Orbit, Manual }

    [Header("Referencias")]
    [Tooltip("Arrastra aquí el CenterEyeAnchor del OVRCameraRig (la cabeza del jugador).")]
    [SerializeField] Transform player;
    [SerializeField] Animator animator;
    [Tooltip("Si se deja vacío, se buscan todos los Renderers hijos.")]
    [SerializeField] Renderer[] renderers;

    [Header("Nombres de los estados en el Animator")]
    [SerializeField] string idleState = "Idle";
    [SerializeField] string walkState = "Walk";
    [SerializeField] string runState = "Run";
    [SerializeField] string attackState = "Attack";
    [SerializeField] float crossFade = 0.2f;

    [Header("Movimiento (metros por segundo)")]
    [Tooltip("Velocidad mientras rodea al jugador en espiral (animación Walk).")]
    [SerializeField] float spiralSpeed = 4.5f;
    [Tooltip("Velocidad de la embestida cuando ve al jugador (animación Run).")]
    [SerializeField] float chargeSpeed = 10f;
    [SerializeField] float turnSpeed = 360f;
    [Tooltip("Si el modelo mira hacia otro eje, corrige aquí (ej: 90, -90, 180).")]
    [SerializeField] float modelYawOffset = 0f;
    [SerializeField] float groundOffset = 0f;

    [Header("Sincronía de animación (evita que patine)")]
    [Tooltip("Velocidad (m/s) a la que el clip Walk se ve natural. La animación se acelera en proporción a la velocidad real.")]
    [SerializeField] float walkClipSpeed = 1.5f;
    [Tooltip("Velocidad (m/s) a la que el clip Run se ve natural.")]
    [SerializeField] float runClipSpeed = 5f;
    [Tooltip("Tope de aceleración de la animación.")]
    [SerializeField] float maxAnimatorSpeed = 2.5f;

    [Header("Espiral")]
    [Tooltip("Ondulación del radio, para que el recorrido no sea un círculo perfecto.")]
    [SerializeField] float radiusWobble = 1.5f;
    [SerializeField] float wobbleFrequency = 0.15f;

    [Header("Detección: cuando el jugador la ve")]
    [Tooltip("Ángulo máximo respecto a hacia dónde mira el jugador para considerar que la ve. El Quest 3 ve unos 55° a cada lado; 35° es la zona central.")]
    [SerializeField] float sightAngle = 35f;
    [Tooltip("Distancia máxima a la que cuenta como vista. Más allá, la niebla ya la oculta.")]
    [SerializeField] float sightMaxDistance = 20f;
    [Tooltip("Segundos seguidos que debe tenerla en la zona central para que se lance.")]
    [SerializeField] float sightHoldTime = 0.3f;
    [Tooltip("Altura sobre el suelo del punto del cuerpo al que se apunta.")]
    [SerializeField] float sightHeight = 0.3f;
    [Tooltip("Al revelarse, espera a estar al menos a este ángulo (horizontal) fuera de la vista del jugador, para no aparecer de golpe delante de él.")]
    [SerializeField] float revealMinAngle = 70f;

    [Header("Ataque")]
    [SerializeField] float attackDistance = 1.6f;
    [Tooltip("Activo: el mordisco lo dispara el Animation Event AnimEvent_Bite. Inactivo: se usa biteDelay.")]
    [SerializeField] bool useAnimationEvent = false;
    [SerializeField] float biteDelay = 0.8f;

    public UnityEvent onBite;
    public UnityEvent onFootstep;

    /// <summary>Se dispara cada vez que cambia el estado (Idle, Walk, Run, Attack). Lo usa SpiderAudio.</summary>
    public event System.Action<SpiderState> StateChanged;

    public SpiderState State { get; private set; } = SpiderState.Hidden;
    public bool IsVisible { get; private set; }
    public bool IsOrbiting => mode == Mode.Orbit;
    public bool IsCharging => charging;
    public float DistanceToPlayer => player == null ? 0f : Vector3.Distance(player.position, transform.position);

    Mode mode = Mode.None;
    float orbitRadius, orbitAngle, orbitDir = 1f, wobblePhase, currentSpeed, seenTime;
    Coroutine radiusRoutine;
    bool biteFired, charging;

    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (renderers == null || renderers.Length == 0) renderers = GetComponentsInChildren<Renderer>(true);

        if (animator != null)
        {
            // Si los renderers están apagados, el Animator no debe pausarse
            // (si no, los Animation Events de pasos dejarían de dispararse).
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.applyRootMotion = false;
        }

        SetVisible(false);
    }

    // ---------------------------------------------------------------- API pública

    public void SetVisible(bool visible)
    {
        IsVisible = visible;
        foreach (var r in renderers) if (r != null) r.enabled = visible;
    }

    public void Hide()
    {
        StopAllCoroutines();
        radiusRoutine = null;
        mode = Mode.None;
        currentSpeed = 0f;
        charging = false;
        biteFired = false;
        seenTime = 0f;
        SetVisible(false);
        SetState(SpiderState.Idle);
        SyncAnimatorSpeed(SpiderState.Idle, 0f);
    }

    /// <summary>Empieza a rodear al jugador (espiral) desde 'radius' metros.</summary>
    public void BeginOrbit(float radius, bool visible)
    {
        StopAllCoroutines();
        radiusRoutine = null;
        biteFired = false;
        charging = false;
        seenTime = 0f;

        mode = Mode.Orbit;
        orbitRadius = radius;
        orbitAngle = Random.value * Mathf.PI * 2f;
        wobblePhase = Random.value * Mathf.PI * 2f;
        orbitDir = Random.value < 0.5f ? 1f : -1f;

        SetVisible(visible);
        PlaceOnOrbit();
        SetMotion(SpiderState.Walk, spiralSpeed);
    }

    /// <summary>Cambia el radio de forma gradual: si es menor que el actual, la órbita se vuelve espiral hacia el jugador.</summary>
    public void ChangeOrbitRadius(float target, float duration)
    {
        if (radiusRoutine != null) StopCoroutine(radiusRoutine);
        radiusRoutine = StartCoroutine(RadiusRoutine(target, duration));
    }

    /// <summary>La hace visible, pero solo cuando está fuera de la vista del jugador (así no aparece de golpe).</summary>
    public void RevealOutOfView()
    {
        if (IsVisible) return;
        StartCoroutine(RevealRoutine());
    }

    /// <summary>Corre directo al jugador y ataca. Se llama sola cuando el jugador la ve; el Director puede forzarla.</summary>
    public void Charge()
    {
        if (charging) return;
        charging = true;

        StopAllCoroutines();
        radiusRoutine = null;
        mode = Mode.Manual;
        SetVisible(true);
        StartCoroutine(ChargeRoutine());
    }

    // Animation Events (opcionales)
    public void AnimEvent_Bite() { TriggerBite(); }

    public void AnimEvent_Footstep()
    {
        onFootstep.Invoke();
    }

    // ---------------------------------------------------------------- Update

    void Update()
    {
        if (player == null) return;

        if (mode == Mode.Orbit)
        {
            UpdateOrbit();
            CheckSight();
        }
    }

    float CurrentOrbitRadius()
    {
        // La ondulación se reduce al acercarse para no atravesar al jugador.
        float wobble = Mathf.Min(radiusWobble, orbitRadius * 0.15f);
        return Mathf.Max(1f, orbitRadius + Mathf.Sin(wobblePhase) * wobble);
    }

    void UpdateOrbit()
    {
        wobblePhase += Time.deltaTime * wobbleFrequency * Mathf.PI * 2f;

        float r = CurrentOrbitRadius();
        orbitAngle += orbitDir * (currentSpeed / r) * Time.deltaTime;

        PlaceOnOrbit();

        Vector3 tangent = orbitDir * new Vector3(Mathf.Cos(orbitAngle), 0f, -Mathf.Sin(orbitAngle));
        FaceTowards(tangent, Time.deltaTime);
    }

    void PlaceOnOrbit()
    {
        if (player == null) return;
        float r = CurrentOrbitRadius();
        Vector3 pos = player.position + new Vector3(Mathf.Sin(orbitAngle), 0f, Mathf.Cos(orbitAngle)) * r;
        transform.position = pos;
        SnapToGround();
    }

    // Si el jugador la ve (visible, cerca y en el centro de su vista) el tiempo suficiente, embiste.
    void CheckSight()
    {
        if (!IsVisible || charging) return;

        if (PlayerSeesSpider())
        {
            seenTime += Time.deltaTime;
            if (seenTime >= sightHoldTime) Charge();
        }
        else
        {
            seenTime = 0f;
        }
    }

    bool PlayerSeesSpider()
    {
        Vector3 toSpider = transform.position + Vector3.up * sightHeight - player.position;
        if (toSpider.magnitude > sightMaxDistance) return false;
        return Vector3.Angle(player.forward, toSpider) <= sightAngle;
    }

    // ---------------------------------------------------------------- Rutinas

    IEnumerator RadiusRoutine(float target, float duration)
    {
        float start = orbitRadius;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            orbitRadius = Mathf.Lerp(start, target, t / duration);
            yield return null;
        }
        orbitRadius = target;
        radiusRoutine = null;
    }

    IEnumerator RevealRoutine()
    {
        while (!IsVisible)
        {
            if (player == null || HorizontalAngleToSpider() >= revealMinAngle)
            {
                SetVisible(true);
                yield break;
            }
            yield return null;
        }
    }

    IEnumerator ChargeRoutine()
    {
        // 1) Corre directo hacia el jugador.
        SetMotion(SpiderState.Run, chargeSpeed);
        while (FlatDistance() > attackDistance)
        {
            MoveTowardsPlayer(chargeSpeed);
            yield return null;
        }

        // 2) Ataca.
        SetMotion(SpiderState.Attack, 0f);
        FaceInstant(player.position - transform.position);

        if (!useAnimationEvent)
        {
            yield return new WaitForSeconds(biteDelay);
            TriggerBite();
        }
        // Si useAnimationEvent está activo, el clip debe llamar a AnimEvent_Bite().
    }

    // ---------------------------------------------------------------- Utilidades

    void TriggerBite()
    {
        if (biteFired) return;
        biteFired = true;
        onBite.Invoke();
    }

    void SetMotion(SpiderState state, float speed)
    {
        currentSpeed = speed;
        SetState(state);
        SyncAnimatorSpeed(state, speed);
    }

    void SetState(SpiderState state)
    {
        if (state == State) return;
        State = state;
        StateChanged?.Invoke(state);

        if (animator == null) return;
        string name = state switch
        {
            SpiderState.Walk => walkState,
            SpiderState.Run => runState,
            SpiderState.Attack => attackState,
            _ => idleState
        };
        if (!string.IsNullOrEmpty(name)) animator.CrossFadeInFixedTime(name, crossFade);
    }

    // Acelera la animación en proporción a la velocidad real para que no parezca que patina.
    void SyncAnimatorSpeed(SpiderState state, float speed)
    {
        if (animator == null) return;

        float reference = state == SpiderState.Walk ? walkClipSpeed
                        : state == SpiderState.Run ? runClipSpeed
                        : 0f;

        animator.speed = reference > 0f
            ? Mathf.Clamp(speed / reference, 0.5f, maxAnimatorSpeed)
            : 1f;
    }

    // Ángulo horizontal entre hacia dónde mira el jugador y la dirección a la araña.
    float HorizontalAngleToSpider()
    {
        Vector3 fwd = player.forward; fwd.y = 0f;
        Vector3 toSpider = transform.position - player.position; toSpider.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f || toSpider.sqrMagnitude < 0.0001f) return 180f;
        return Vector3.Angle(fwd, toSpider);
    }

    float FlatDistance()
    {
        Vector3 d = player.position - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    void MoveTowardsPlayer(float speed)
    {
        Vector3 dir = player.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;

        FaceTowards(dir, Time.deltaTime);
        transform.position += dir.normalized * speed * Time.deltaTime;
        SnapToGround();
    }

    void FaceTowards(Vector3 dir, float dt)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;
        Quaternion target = Quaternion.LookRotation(dir) * Quaternion.Euler(0f, modelYawOffset, 0f);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * dt);
    }

    void FaceInstant(Vector3 dir)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.LookRotation(dir) * Quaternion.Euler(0f, modelYawOffset, 0f);
    }

    void SnapToGround()
    {
        Terrain t = Terrain.activeTerrain;
        if (t == null) return;
        Vector3 p = transform.position;
        p.y = t.SampleHeight(p) + t.transform.position.y + groundOffset;
        transform.position = p;
    }
}
