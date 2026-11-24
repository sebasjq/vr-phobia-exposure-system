
using UnityEngine;

/// <summary>
/// Sistema de audio de la araña para realidad mixta.
///
/// FUNCIONES:
/// 1. Detecta el movimiento de la araña.
/// 2. Controla el parámetro global SpiderSpeed de FMOD.
/// 3. Aumenta o disminuye el volumen según la distancia al jugador.
/// 4. Reproduce el sonido de las patas continuamente.
/// 5. Reproduce el sonido del ataque cuando se solicita.
///
/// Requiere FMOD Unity Integration y el símbolo USE_FMOD.
/// </summary>

public class SpiderAudioRM : MonoBehaviour
{
#if USE_FMOD
    [Header("Eventos FMOD")]

    [Tooltip("Evento continuo de las patas de la araña.")]
    [SerializeField] FMODUnity.EventReference patasEvent;

    [Tooltip("Evento de sonido del ataque.")]
    [SerializeField] FMODUnity.EventReference attackEvent;

    private FMOD.Studio.EventInstance patasInstance;
#endif

    [Header("Parámetro GLOBAL de FMOD para las patas")]

    [SerializeField] string speedParameter = "SpiderSpeed";


    // --------------------------------------------------
    // DETECCIÓN DE MOVIMIENTO
    // --------------------------------------------------

    [Header("Detección de movimiento")]

    [SerializeField] bool autoDetectMovement = true;

    [Tooltip("Por debajo de esta velocidad se considera quieta.")]
    [SerializeField] float idleBelow = 0.1f;

    [Tooltip("Por encima de esta velocidad se considera corriendo.")]
    [SerializeField] float runAbove = 3.5f;

    [Tooltip("Suavizado de la detección de movimiento.")]
    [SerializeField] float smoothing = 10f;


    // --------------------------------------------------
    // AUDIO POR PROXIMIDAD
    // --------------------------------------------------

    [Header("Audio por proximidad")]

    [Tooltip("Transform de la cámara del jugador.")]
    [SerializeField] Transform jugador;

    [Tooltip("Distancia a la que comienza el volumen máximo.")]
    [SerializeField] float distanciaVolumenMaximo = 1.5f;

    [Tooltip("Distancia a la que se alcanza el volumen mínimo.")]
    [SerializeField] float distanciaVolumenMinimo = 8f;

    [Tooltip("Volumen mínimo cuando la araña está lejos.")]
    [Range(0f, 1f)]
    [SerializeField] float volumenMinimo = 0f;

    [Tooltip("Velocidad de transición del volumen.")]
    [SerializeField] float suavizadoVolumen = 3f;


    // --------------------------------------------------
    // VARIABLES INTERNAS
    // --------------------------------------------------

    private Vector3 lastPosition;

    private float smoothedSpeed;

    private float lastValue = -1f;

    private float volumenActual = 0f;

    private bool audioInicializado = false;


    // --------------------------------------------------
    // INICIALIZACIÓN
    // --------------------------------------------------

    void Start()
    {
        lastPosition = transform.position;

        Apply(0f);

#if USE_FMOD
        InicializarAudio();
#endif
    }


#if USE_FMOD

    private void InicializarAudio()
    {
        if (patasEvent.IsNull)
        {
            Debug.LogWarning(
                "[Spider Audio] No se ha asignado el evento de las patas.",
                this
            );

            return;
        }

        // Crear instancia del sonido continuo.
        patasInstance =
            FMODUnity.RuntimeManager.CreateInstance(patasEvent);

        // Configurar posición inicial.
        patasInstance.set3DAttributes(
            FMODUnity.RuntimeUtils.To3DAttributes(transform)
        );

        // Iniciar reproducción.
        patasInstance.start();

        audioInicializado = true;

        Debug.Log("[Spider Audio] Sonido de patas iniciado.", this);
    }

#endif


    // --------------------------------------------------
    // ACTUALIZACIÓN
    // --------------------------------------------------

    void Update()
    {
        // Detectar movimiento de la araña.
        if (autoDetectMovement)
        {
            DetectarMovimiento();
        }

        // Actualizar volumen según proximidad.
        ActualizarVolumenPorDistancia();

#if USE_FMOD
        // Actualizar posición espacial del sonido.
        if (audioInicializado && patasInstance.isValid())
        {
            patasInstance.set3DAttributes(
                FMODUnity.RuntimeUtils.To3DAttributes(transform)
            );
        }
#endif
    }


    // --------------------------------------------------
    // DETECCIÓN DE MOVIMIENTO
    // --------------------------------------------------

    private void DetectarMovimiento()
    {
        float dt = Time.deltaTime;

        if (dt <= 0f) return;

        Vector3 delta = transform.position - lastPosition;

        lastPosition = transform.position;

        // Ignorar desplazamientos verticales.
        delta.y = 0f;

        float speed = delta.magnitude / dt;

        smoothedSpeed = Mathf.Lerp(
            smoothedSpeed,
            speed,
            1f - Mathf.Exp(-smoothing * dt)
        );

        // 0 = quieta.
        // 1 = caminando.
        // 2 = corriendo.

        float level =
            smoothedSpeed < idleBelow ? 0f :
            smoothedSpeed > runAbove ? 2f : 1f;

        Apply(level);
    }


    // --------------------------------------------------
    // CONTROL DEL PARÁMETRO GLOBAL FMOD
    // --------------------------------------------------

    private void Apply(float value)
    {
        value = Mathf.Clamp(value, 0f, 2f);

        if (Mathf.Approximately(value, lastValue))
            return;

        lastValue = value;

        AudioBridge.SetGlobalParameter(
            speedParameter,
            value
        );
    }


    /// <summary>
    /// 0 = quieta.
    /// 1 = caminando.
    /// 2 = corriendo.
    /// </summary>

    public void SetSpeedLevel(float level)
    {
        Apply(level);
    }


    // --------------------------------------------------
    // AUDIO POR DISTANCIA
    // --------------------------------------------------

    private void ActualizarVolumenPorDistancia()
    {
        if (jugador == null)
            return;

        // Medir distancia horizontal.
        // Evita que la altura de la cámara VR
        // altere artificialmente la distancia.

        Vector3 posicionAraña = transform.position;

        Vector3 posicionJugador = jugador.position;

        posicionAraña.y = 0f;

        posicionJugador.y = 0f;

        float distancia = Vector3.Distance(
            posicionAraña,
            posicionJugador
        );


        // Cerca = 1.
        // Lejos = 0.

        float porcentaje = Mathf.InverseLerp(
            distanciaVolumenMinimo,
            distanciaVolumenMaximo,
            distancia
        );


        // Suavizar la transición.
        porcentaje = Mathf.SmoothStep(
            0f,
            1f,
            porcentaje
        );


        // Calcular volumen objetivo.

        float volumenObjetivo = Mathf.Lerp(
            volumenMinimo,
            1f,
            porcentaje
        );


        // Evitar cambios bruscos de volumen.

        volumenActual = Mathf.Lerp(
            volumenActual,
            volumenObjetivo,
            1f - Mathf.Exp(
                -suavizadoVolumen * Time.deltaTime
            )
        );


#if USE_FMOD

        // Aplicar volumen al evento continuo.

        if (audioInicializado && patasInstance.isValid())
        {
            patasInstance.setVolume(volumenActual);
        }

#endif
    }


    // --------------------------------------------------
    // ATAQUE DE LA ARAÑA
    // --------------------------------------------------

    /// <summary>
    /// Reproducir sonido de ataque.
    /// Se puede llamar desde un Animation Event.
    /// </summary>

    public void PlayAttack()
    {
        Debug.Log("[Audio] Spider attack", this);

#if USE_FMOD

        if (!attackEvent.IsNull)
        {
            FMODUnity.RuntimeManager.PlayOneShotAttached(
                attackEvent,
                gameObject
            );
        }
        else
        {
            Debug.LogWarning(
                "[Spider Audio] No se ha asignado el evento de ataque.",
                this
            );
        }

#endif
    }


    // --------------------------------------------------
    // LIBERAR RECURSOS DE FMOD
    // --------------------------------------------------

    void OnDestroy()
    {
#if USE_FMOD

        if (patasInstance.isValid())
        {
            patasInstance.stop(
                FMOD.Studio.STOP_MODE.ALLOWFADEOUT
            );

            patasInstance.release();
        }

        audioInicializado = false;

#endif
    }


    // --------------------------------------------------
    // VALIDACIÓN DE PARÁMETROS
    // --------------------------------------------------

    void OnValidate()
    {
        idleBelow = Mathf.Max(0f, idleBelow);

        runAbove = Mathf.Max(idleBelow + 0.1f, runAbove);

        smoothing = Mathf.Max(0.1f, smoothing);

        distanciaVolumenMaximo = Mathf.Max(
            0.1f,
            distanciaVolumenMaximo
        );

        distanciaVolumenMinimo = Mathf.Max(
            distanciaVolumenMaximo + 0.1f,
            distanciaVolumenMinimo
        );

        suavizadoVolumen = Mathf.Max(
            0.1f,
            suavizadoVolumen
        );
    }
}
