using System.Collections;
using UnityEngine;
using UnityEngine.Events;

public enum SpiderState { Hidden, Idle, Walk, Run, Attack }

/// <summary>
/// Araña con 4 animaciones: Idle, Walk, Run, Attack.
///
/// Comportamientos:
///  - Orbitar:  da vueltas alrededor del jugador a un radio dado. Puede estar INVISIBLE
///              (el GameObject sigue activo para que el sonido de sus patas se escuche).
///              Alterna caminar / correr / pausas, cambia de sentido y, si está oculta,
///              reaparece en otro ángulo tras una pausa (los pasos "cambian de dirección").
///  - Aproximación final: aparece frente al jugador, camina lento, se detiene a mirarlo,
///              se lanza (corre) y ataca.
///
/// IMPORTANTE: este script debe estar en el MISMO objeto que el Animator
/// si quieres usar Animation Events (AnimEvent_Bite / AnimEvent_Footstep).
/// </summary>
public class SpiderController : MonoBehaviour
{
    enum Mode { None, Orbit, Manual }

    [Header("Referencias")]
    [SerializeField] Transform player;                 // la cámara (cabeza) del XR Origin
    [SerializeField] Animator animator;
    [Tooltip("Si se deja vacío, se buscan todos los Renderers hijos.")]
    [SerializeField] Renderer[] renderers;

    [Header("Nombres de los estados en el Animator")]
    [SerializeField] string idleState = "Idle";
    [SerializeField] string walkState = "Walk";
    [SerializeField] string runState = "Run";
    [SerializeField] string attackState = "Attack";
    [SerializeField] float crossFade = 0.2f;

    [Header("Movimiento")]
    [SerializeField] float walkSpeed = 1.5f;
    [SerializeField] float runSpeed = 5f;
    [SerializeField] float turnSpeed = 180f;
    [Tooltip("Si el modelo mira hacia otro eje, corrige aquí (ej: 90, -90, 180).")]
    [SerializeField] float modelYawOffset = 0f;
    [SerializeField] float groundOffset = 0f;

    [Header("Órbita")]
    [SerializeField] float radiusWobble = 1.5f;
    [SerializeField] float wobbleFrequency = 0.15f;
    [Range(0f, 1f)][SerializeField] float pauseChance = 0.25f;
    [Range(0f, 1f)][SerializeField] float runChance = 0.2f;
    [Range(0f, 1f)][SerializeField] float reverseChance = 0.4f;
    [Range(0f, 1f)][SerializeField] float repositionChance = 0.5f;
    [SerializeField] Vector2 moveDuration = new Vector2(2f, 5f);
    [SerializeField] Vector2 pauseDuration = new Vector2(1.5f, 4f);

    [Header("Aproximación final y ataque")]
    [SerializeField] float stareDistance = 3.5f;
    [SerializeField] float stareDuration = 1.5f;

    [Header("Espera por mirada (antes de lanzarse)")]
    [Tooltip("La araña espera a que el jugador la mire antes de lanzarse.")]
    [SerializeField] bool waitForGaze = true;
    [Tooltip("Ángulo máximo entre hacia dónde mira el jugador y la araña. 40 es generoso en VR.")]
    [SerializeField] float gazeAngle = 40f;
    [Tooltip("Segundos seguidos que debe mirarla.")]
    [SerializeField] float gazeHoldTime = 0.6f;
    [Tooltip("Límite de espera. Si el jugador no mira, se lanza igual para no trabar la experiencia.")]
    [SerializeField] float gazeMaxWait = 8f;
    [Tooltip("Altura sobre el suelo del punto del cuerpo al que se apunta.")]
    [SerializeField] float gazeHeight = 0.3f;
    [SerializeField] float attackDistance = 1.6f;
    [Tooltip("Si está activo, la araña corre el último tramo antes de morder.")]
    [SerializeField] bool lungeWithRun = true;
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
    public float DistanceToPlayer => player == null ? 0f : Vector3.Distance(player.position, transform.position);

    Mode mode = Mode.None;
    float orbitRadius, orbitAngle, orbitDir = 1f, wobblePhase, currentSpeed;
    Coroutine radiusRoutine;
    bool biteFired;

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
        SetVisible(false);
        SetState(SpiderState.Idle);
    }

    /// <summary>Empieza a rodear al jugador a 'radius' metros.</summary>
    public void BeginOrbit(float radius, bool visible)
    {
        StopAllCoroutines();
        radiusRoutine = null;
        biteFired = false;

        mode = Mode.Orbit;
        orbitRadius = radius;
        orbitAngle = Random.value * Mathf.PI * 2f;
        wobblePhase = Random.value * Mathf.PI * 2f;
        orbitDir = Random.value < 0.5f ? 1f : -1f;

        SetVisible(visible);
        PlaceOnOrbit();
        StartCoroutine(OrbitBehaviour());
    }

    /// <summary>Acerca (o aleja) la órbita de forma gradual.</summary>
    public void ChangeOrbitRadius(float target, float duration)
    {
        if (radiusRoutine != null) StopCoroutine(radiusRoutine);
        radiusRoutine = StartCoroutine(RadiusRoutine(target, duration));
    }

    /// <summary>Aparece delante del jugador y ejecuta la secuencia final.</summary>
    public void StartFinalApproach(float spawnDistance)
    {
        StopAllCoroutines();
        radiusRoutine = null;
        biteFired = false;
        mode = Mode.Manual;
        StartCoroutine(FinalApproachRoutine(spawnDistance));
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
        if (mode == Mode.Orbit) UpdateOrbit();
    }

    void UpdateOrbit()
    {
        if (currentSpeed > 0.01f) wobblePhase += Time.deltaTime * wobbleFrequency * Mathf.PI * 2f;

        float r = Mathf.Max(1f, orbitRadius + Mathf.Sin(wobblePhase) * radiusWobble);
        orbitAngle += orbitDir * (currentSpeed / r) * Time.deltaTime;

        PlaceOnOrbit();

        if (currentSpeed > 0.01f)
        {
            Vector3 tangent = orbitDir * new Vector3(Mathf.Cos(orbitAngle), 0f, -Mathf.Sin(orbitAngle));
            FaceTowards(tangent, Time.deltaTime);
        }
        else
        {
            FaceTowards(player.position - transform.position, Time.deltaTime);
        }
    }

    void PlaceOnOrbit()
    {
        if (player == null) return;
        float r = Mathf.Max(1f, orbitRadius + Mathf.Sin(wobblePhase) * radiusWobble);
        Vector3 pos = player.position + new Vector3(Mathf.Sin(orbitAngle), 0f, Mathf.Cos(orbitAngle)) * r;
        transform.position = pos;
        SnapToGround();
    }

    // ---------------------------------------------------------------- Rutinas

    IEnumerator OrbitBehaviour()
    {
        while (true)
        {
            if (Random.value < pauseChance)
            {
                SetMotion(SpiderState.Idle, 0f);
                yield return new WaitForSeconds(Random.Range(pauseDuration.x, pauseDuration.y));

                // Tras la pausa, si no se ve, reaparece en otro lado: los pasos vienen de otra dirección.
                if (!IsVisible && Random.value < repositionChance)
                    orbitAngle = Random.value * Mathf.PI * 2f;
            }
            else
            {
                if (Random.value < reverseChance) orbitDir *= -1f;

                bool run = Random.value < runChance;
                SetMotion(run ? SpiderState.Run : SpiderState.Walk, run ? runSpeed : walkSpeed);
                yield return new WaitForSeconds(Random.Range(moveDuration.x, moveDuration.y));
            }
        }
    }

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

    IEnumerator FinalApproachRoutine(float spawnDistance)
    {
        // 1) Aparece delante del jugador, mirándolo (la niebla lo revela poco a poco).
        Vector3 fwd = player.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        transform.position = player.position + fwd * spawnDistance;
        SnapToGround();
        FaceInstant(player.position - transform.position);
        SetVisible(true);

        // 2) Camina lento hacia el jugador.
        SetMotion(SpiderState.Walk, walkSpeed);
        while (FlatDistance() > stareDistance)
        {
            MoveTowardsPlayer(walkSpeed);
            yield return null;
        }

        // 3) Se detiene y lo mira.
        SetMotion(SpiderState.Idle, 0f);
        float t = 0f;
        while (t < stareDuration)
        {
            t += Time.deltaTime;
            FaceTowards(player.position - transform.position, Time.deltaTime);
            yield return null;
        }

        // 3b) Espera a que el jugador la mire (con límite de tiempo).
        if (waitForGaze)
        {
            float waited = 0f;
            float held = 0f;
            while (waited < gazeMaxWait && held < gazeHoldTime)
            {
                waited += Time.deltaTime;
                held = IsPlayerLookingAtSpider() ? held + Time.deltaTime : 0f;
                FaceTowards(player.position - transform.position, Time.deltaTime);
                yield return null;
            }
        }

        // 4) Se lanza.
        float speed = lungeWithRun ? runSpeed : walkSpeed;
        SetMotion(lungeWithRun ? SpiderState.Run : SpiderState.Walk, speed);
        while (FlatDistance() > attackDistance)
        {
            MoveTowardsPlayer(speed);
            yield return null;
        }

        // 5) Ataca.
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

    bool IsPlayerLookingAtSpider()
    {
        Vector3 target = transform.position + Vector3.up * gazeHeight;
        Vector3 toSpider = target - player.position;
        return Vector3.Angle(player.forward, toSpider) <= gazeAngle;
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