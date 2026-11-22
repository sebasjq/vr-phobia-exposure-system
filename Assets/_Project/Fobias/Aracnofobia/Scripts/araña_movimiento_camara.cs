using UnityEngine;
using UnityEngine.AI;
using Meta.XR.MRUtilityKit;

// Conserva este nombre de clase y el archivo original para mantener las referencias.
public class araña_movimiento_camara : MonoBehaviour
{
    [Header("Referencias")]
    [Tooltip("Asigna CenterEyeAnchor, la cámara del visor.")]
    public Transform jugador;
    public NavMeshAgent agente;
    public Animator animator;

    [Header("Exploración de la habitación")]
    public float velocidadExploracion = 0.7f;
    [Tooltip("Pausas entre recorridos, en segundos.")]
    public Vector2 pausaEntreRecorridos = new Vector2(0.8f, 2.2f);
    [Tooltip("Distancia preferida entre la araña y su siguiente destino.")]
    public float distanciaMinimaRecorrido = 1.2f;

    [Header("Visitas ocasionales al jugador")]
    [Tooltip("Segundos de exploración entre el final de una visita y el inicio de otra.")]
    public Vector2 intervaloVisitas = new Vector2(12f, 22f);
    [Tooltip("Tiempo máximo de cada visita, incluido el desplazamiento hacia el jugador.")]
    public float duracionVisita = 8f;
    public float velocidadPersecucion = 1.5f;
    public float distanciaDetencion = 2.2f;
    public float radioRodeo = 1.4f;
    public float velocidadRodeo = 0.5f;
    public float tiempoEspera = 1.5f;
    public float velocidadGiro = 5f;

    [Header("Ataque durante las visitas")]
    [Tooltip("Como máximo se realiza un ataque por visita. Desactiva para solo acercarse.")]
    public bool habilitarAtaques = true;
    public float distanciaAtaque = 1.5f;
    public float intervaloAtaque = 4f;
    public float duracionAtaque = 1f;

    [Header("Navegación")]
    public float frecuenciaActualizacion = 0.2f;
    [Tooltip("Radio de búsqueda al colocar inicialmente la araña en el NavMesh.")]
    public float distanciaMuestreo = 2f;
    [Tooltip("Radio de ajuste para los destinos del recorrido. No es el tamaño del recorrido.")]
    public float radioMuestreoDestino = 0.35f;
    public float distanciaParadaAgente = 0.1f;
    public float tiempoMaximoSinAvanzar = 3f;
    [Range(4, 64)] public int intentosPorDestino = 24;

    private enum Estado
    {
        EsperandoNavMesh,
        Explorando,
        PausaExploracion,
        VisitandoJugador,
        Atacando
    }

    [SerializeField] private Estado estado = Estado.EsperandoNavMesh;

    private static readonly int idVelocidad = Animator.StringToHash("speed");
    private static readonly int idAtaque = Animator.StringToHash("attack");

    private MRUKRoom habitacion;
    private NavMeshPath ruta;
    private bool inicializado;
    private bool tieneDestino;
    private bool ataqueRealizadoEnVisita;
    private bool avisoHabitacion;
    private bool alejarseTrasVisita;
    private Vector3 destinoActual;
    private Vector3 ultimaPosicionConAvance;
    private float ultimoAvance;
    private float proximaDecision;
    private float proximoIntentoNavMesh;
    private float proximaVisita;
    private float finVisita;
    private float finAtaque;
    private float pausaHasta;
    private float tiempoUltimoAtaque;

    private Vector3 PosicionActual => agente.transform.position;

    private void Awake()
    {
        ObtenerReferencias();
        tiempoUltimoAtaque = Time.time;

        if (agente == null)
            Debug.LogError("Falta asignar el NavMeshAgent de la araña.", this);
    }

    private void ObtenerReferencias()
    {
        if (agente == null) agente = GetComponent<NavMeshAgent>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (ruta == null) ruta = new NavMeshPath();
    }

    private void Update()
    {
        if (agente == null) return;

        // La habitación y el NavMesh pueden tardar en cargar o reconstruirse.
        if (!agente.isActiveAndEnabled || !agente.isOnNavMesh)
        {
            inicializado = false;
            estado = Estado.EsperandoNavMesh;
            ActualizarAnimacion(0f);

            if (Time.time >= proximoIntentoNavMesh)
            {
                proximoIntentoNavMesh = Time.time + 1f;
                IntentarColocarSobreNavMesh(false);
            }
            return;
        }

        if (!inicializado) InicializarNavegacion();

        // El recorrido usa los límites reales de MRUK, no un radio alrededor del jugador.
        if (habitacion == null)
        {
            habitacion = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            if (habitacion == null)
            {
                Detener();
                ActualizarAnimacion(0f);
                if (!avisoHabitacion)
                {
                    Debug.LogWarning("Esperando la habitación de MRUK para iniciar el recorrido.", this);
                    avisoHabitacion = true;
                }
                return;
            }
        }

        ActualizarAnimacion(agente.velocity.magnitude);
        RegistrarAvance();

        bool mirandoAlJugador = jugador != null &&
            (estado == Estado.Atacando ||
             (estado == Estado.VisitandoJugador &&
              DistanciaHorizontal(PosicionActual, jugador.position) <= distanciaDetencion));
        agente.updateRotation = !mirandoAlJugador;
        if (mirandoAlJugador) MirarJugador();

        if (estado == Estado.Atacando)
        {
            if (Time.time >= finAtaque)
            {
                if (Time.time >= finVisita) FinalizarVisita();
                else
                {
                    estado = Estado.VisitandoJugador;
                    pausaHasta = Time.time + tiempoEspera;
                }
            }
            return;
        }

        if (estado == Estado.VisitandoJugador && Time.time >= finVisita)
            FinalizarVisita();

        // El tiempo dispara las visitas aunque el jugador permanezca inmóvil.
        if ((estado == Estado.Explorando || estado == Estado.PausaExploracion) &&
            jugador != null && Time.time >= proximaVisita)
            IniciarVisita();

        if (Time.time < proximaDecision) return;
        proximaDecision = Time.time + frecuenciaActualizacion;

        switch (estado)
        {
            case Estado.Explorando:
                Explorar();
                break;
            case Estado.PausaExploracion:
                if (Time.time >= pausaHasta)
                {
                    estado = Estado.Explorando;
                    Explorar();
                }
                break;
            case Estado.VisitandoJugador:
                VisitarJugador();
                break;
        }
    }

    // Conserva esta función en Scene Navigation > On Nav Mesh Initialized.
    public void ColocarSobreNavMesh()
    {
        proximoIntentoNavMesh = Time.time + 1f;
        IntentarColocarSobreNavMesh(true);
    }

    private void IntentarColocarSobreNavMesh(bool mostrarAviso)
    {
        ObtenerReferencias();
        if (agente == null || !agente.gameObject.activeInHierarchy) return;

        var filtro = new NavMeshQueryFilter
        {
            agentTypeID = agente.agentTypeID,
            areaMask = agente.areaMask
        };

        if (!NavMesh.SamplePosition(PosicionActual, out NavMeshHit hit, distanciaMuestreo, filtro))
        {
            if (mostrarAviso)
                Debug.LogWarning("No hay NavMesh compatible cerca de la araña. Revisa su posición y Scene Navigation.", this);
            return;
        }

        agente.enabled = false;
        agente.transform.position = hit.position + Vector3.up * agente.baseOffset;
        agente.enabled = true;
        if (agente.isOnNavMesh) InicializarNavegacion();
    }

    private void InicializarNavegacion()
    {
        habitacion = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        avisoHabitacion = false;
        agente.acceleration = 8f;
        agente.autoBraking = true;
        agente.updatePosition = true;
        agente.updateRotation = true;
        agente.speed = velocidadExploracion;
        agente.stoppingDistance = distanciaParadaAgente;
        // El desplazamiento lo controla NavMeshAgent; el Animator solo lo representa.
        if (animator != null) animator.applyRootMotion = false;
        Detener();
        inicializado = true;
        estado = Estado.Explorando;
        alejarseTrasVisita = false;
        pausaHasta = 0f;
        proximaDecision = Time.time;
        ProgramarVisita();
    }

    private void Explorar()
    {
        if (tieneDestino)
        {
            if (LlegoAlDestino())
            {
                PausarExploracion(Random.Range(pausaEntreRecorridos.x, pausaEntreRecorridos.y));
                return;
            }
            if (!DestinoFallido()) return;
            Detener();
        }

        agente.speed = velocidadExploracion;
        Bounds limites = habitacion.GetRoomBounds();
        float altura = habitacion.FloorAnchor != null
            ? habitacion.FloorAnchor.transform.position.y : PosicionActual.y;
        int rutasRestantes = 3;

        // Primero prefiere destinos distantes; después de una visita, también alejados del jugador.
        // La segunda pasada permite desplazamientos cortos en habitaciones pequeñas.
        for (int pasada = 0; pasada < 2; pasada++)
        {
            for (int i = 0; i < intentosPorDestino && rutasRestantes > 0; i++)
            {
                Vector3 candidato = new Vector3(
                    Random.Range(limites.min.x, limites.max.x), altura,
                    Random.Range(limites.min.z, limites.max.z));

                if (pasada == 0 && alejarseTrasVisita && jugador != null &&
                    DistanciaHorizontal(candidato, jugador.position) < distanciaDetencion)
                    continue;

                float distanciaMinima = pasada == 0 ? distanciaMinimaRecorrido : 0.25f;
                if (AsignarDestinoValido(candidato, distanciaMinima, ref rutasRestantes))
                {
                    alejarseTrasVisita = false;
                    return;
                }
            }
        }

        // Si no hay destino válido, reintenta sin bloquearse ni llenar la consola.
        PausarExploracion(1f);
    }

    private void PausarExploracion(float duracion)
    {
        Detener();
        estado = Estado.PausaExploracion;
        pausaHasta = Time.time + duracion;
    }

    private void ProgramarVisita()
    {
        proximaVisita = Time.time + Random.Range(intervaloVisitas.x, intervaloVisitas.y);
    }

    private void IniciarVisita()
    {
        Detener();
        estado = Estado.VisitandoJugador;
        finVisita = Time.time + duracionVisita;
        ataqueRealizadoEnVisita = false;
        pausaHasta = 0f;
        proximaDecision = Time.time;
    }

    private void VisitarJugador()
    {
        if (jugador == null)
        {
            FinalizarVisita();
            return;
        }

        float distancia = DistanciaHorizontal(PosicionActual, jugador.position);
        if (habilitarAtaques && !ataqueRealizadoEnVisita && distancia <= distanciaAtaque &&
            Time.time >= tiempoUltimoAtaque + intervaloAtaque)
        {
            Detener();
            estado = Estado.Atacando;
            ataqueRealizadoEnVisita = true;
            tiempoUltimoAtaque = Time.time;
            finAtaque = Time.time + duracionAtaque;
            if (animator != null) animator.SetTrigger(idAtaque);
            return;
        }

        if (Time.time < pausaHasta) return;
        agente.speed = distancia > distanciaDetencion ? velocidadPersecucion : velocidadRodeo;

        if (tieneDestino)
        {
            if (LlegoAlDestino())
            {
                Detener();
                pausaHasta = Time.time + tiempoEspera;
                return;
            }
            if (!DestinoFallido()) return;
            Detener();
        }

        // Busca posiciones alrededor del jugador, sin dirigirse al centro de su cuerpo.
        Vector3 centro = jugador.position;
        centro.y = habitacion.FloorAnchor != null
            ? habitacion.FloorAnchor.transform.position.y : PosicionActual.y;
        float anguloCercano = Mathf.Atan2(PosicionActual.z - centro.z, PosicionActual.x - centro.x);
        int rutasRestantes = 3;
        for (int i = 0; i < intentosPorDestino && rutasRestantes > 0; i++)
        {
            float angulo = i == 0 && distancia > distanciaDetencion
                ? anguloCercano : Random.Range(0f, Mathf.PI * 2f);
            Vector3 candidato = centro + new Vector3(Mathf.Cos(angulo), 0f, Mathf.Sin(angulo)) * radioRodeo;
            if (AsignarDestinoValido(candidato, 0.25f, ref rutasRestantes)) return;
        }

        // Puede no haber espacio accesible alrededor del jugador: continúa explorando.
        FinalizarVisita();
    }

    private void FinalizarVisita()
    {
        Detener();
        estado = Estado.Explorando;
        alejarseTrasVisita = true;
        agente.updateRotation = true;
        pausaHasta = 0f;
        proximaDecision = Time.time;
        ProgramarVisita();
    }

    private bool AsignarDestinoValido(Vector3 candidato, float distanciaMinima, ref int rutasRestantes)
    {
        if (!habitacion.IsPositionInRoom(candidato, false)) return false;
        var filtro = new NavMeshQueryFilter
        {
            agentTypeID = agente.agentTypeID,
            areaMask = agente.areaMask
        };
        if (!NavMesh.SamplePosition(candidato, out NavMeshHit hit, radioMuestreoDestino, filtro)) return false;
        if (!habitacion.IsPositionInRoom(hit.position, false)) return false;
        if (DistanciaHorizontal(PosicionActual, hit.position) < distanciaMinima) return false;

        // SamplePosition encuentra un punto, pero no garantiza que se pueda llegar.
        rutasRestantes--;
        if (!agente.CalculatePath(hit.position, ruta) || ruta.status != NavMeshPathStatus.PathComplete)
            return false;
        if (!agente.SetPath(ruta)) return false;

        destinoActual = hit.position;
        tieneDestino = true;
        ultimaPosicionConAvance = PosicionActual;
        ultimoAvance = Time.time;
        agente.isStopped = false;
        return true;
    }

    private bool LlegoAlDestino()
    {
        float tolerancia = Mathf.Max(0.15f, agente.stoppingDistance + 0.05f);
        return !agente.pathPending && agente.remainingDistance <= tolerancia &&
            DistanciaHorizontal(PosicionActual, destinoActual) <= tolerancia;
    }

    private bool DestinoFallido()
    {
        return !agente.pathPending &&
            (!agente.hasPath || agente.pathStatus != NavMeshPathStatus.PathComplete ||
             Time.time - ultimoAvance >= tiempoMaximoSinAvanzar);
    }

    private void RegistrarAvance()
    {
        if (tieneDestino && (PosicionActual - ultimaPosicionConAvance).sqrMagnitude >= 0.0016f)
        {
            ultimaPosicionConAvance = PosicionActual;
            ultimoAvance = Time.time;
        }
    }

    private void Detener()
    {
        tieneDestino = false;
        if (agente != null && agente.isActiveAndEnabled && agente.isOnNavMesh)
        {
            agente.isStopped = true;
            agente.ResetPath();
        }
    }

    private void ActualizarAnimacion(float velocidad)
    {
        if (animator != null)
            animator.SetFloat(idVelocidad, velocidad, 0.1f, Time.deltaTime);
    }

    private void MirarJugador()
    {
        Vector3 direccion = jugador.position - PosicionActual;
        direccion.y = 0f;
        if (direccion.sqrMagnitude < 0.001f) return;
        agente.transform.rotation = Quaternion.Slerp(agente.transform.rotation,
            Quaternion.LookRotation(direccion), velocidadGiro * Time.deltaTime);
    }

    private static float DistanciaHorizontal(Vector3 a, Vector3 b)
    {
        a.y = b.y;
        return Vector3.Distance(a, b);
    }

    private void OnDisable()
    {
        Detener();
        inicializado = false;
        estado = Estado.EsperandoNavMesh;
        if (animator != null) animator.SetFloat(idVelocidad, 0f);
    }

    private void OnValidate()
    {
        frecuenciaActualizacion = Mathf.Max(0.05f, frecuenciaActualizacion);
        distanciaMuestreo = Mathf.Max(0.05f, distanciaMuestreo);
        radioMuestreoDestino = Mathf.Max(0.05f, radioMuestreoDestino);
        distanciaParadaAgente = Mathf.Clamp(distanciaParadaAgente, 0f, 0.3f);
        velocidadExploracion = Mathf.Max(0.05f, velocidadExploracion);
        velocidadPersecucion = Mathf.Max(0.05f, velocidadPersecucion);
        velocidadRodeo = Mathf.Max(0.05f, velocidadRodeo);
        velocidadGiro = Mathf.Max(0.1f, velocidadGiro);
        distanciaMinimaRecorrido = Mathf.Max(0.25f, distanciaMinimaRecorrido);
        radioRodeo = Mathf.Max(0.3f, radioRodeo);
        distanciaDetencion = Mathf.Max(radioRodeo, distanciaDetencion);
        tiempoEspera = Mathf.Max(0f, tiempoEspera);
        duracionVisita = Mathf.Max(0.5f, duracionVisita);
        duracionAtaque = Mathf.Max(0.05f, duracionAtaque);
        intervaloAtaque = Mathf.Max(duracionAtaque, intervaloAtaque);
        distanciaAtaque = Mathf.Max(0.1f, distanciaAtaque);
        tiempoMaximoSinAvanzar = Mathf.Max(1f, tiempoMaximoSinAvanzar);
        intentosPorDestino = Mathf.Clamp(intentosPorDestino, 4, 64);
        intervaloVisitas.x = Mathf.Max(1f, intervaloVisitas.x);
        intervaloVisitas.y = Mathf.Max(intervaloVisitas.x, intervaloVisitas.y);
        pausaEntreRecorridos.x = Mathf.Max(0.05f, pausaEntreRecorridos.x);
        pausaEntreRecorridos.y = Mathf.Max(pausaEntreRecorridos.x, pausaEntreRecorridos.y);
    }
}
