using UnityEngine;
using FMODUnity;
using FMOD.Studio;
using OceanSystem;

/// <summary>
/// Audio de talasofobia: ambiente, filtro por profundidad y cruces de superficie.
/// Colocar fuera de carpetas Editor y ThirdParty.
/// </summary>
public class ThalassophobiaAudioController : MonoBehaviour
{
    [Header("Eventos FMOD")]
    [SerializeField] private EventReference ambienteMar;
    [SerializeField] private EventReference entrarAgua;
    [SerializeField] private EventReference salirAgua;

    [Header("Referencias de la escena")]
    [Tooltip("Camara o CenterEyeAnchor que sigue la cabeza; no la raiz del rig.")]
    [SerializeField] private Transform cabeza;
    [Tooltip("Opcional. Si se asigna, consulta las olas mediante OceanCollision.")]
    [SerializeField] private OceanSimulation simulacionOceano;
    [Tooltip("Origen del ambiente 3D. Vacio: sigue la posicion de la cabeza.")]
    [SerializeField] private Transform origenAmbiente;

    [Header("Emisores adicionales (opcional)")]
    [Tooltip("Arrastra Audio_Orilla. Sus emisores conservaran sus posiciones y recibiran el parametro local Profundidad. Vacio: comportamiento original.")]
    [SerializeField] private Transform raizEmisoresMar;

    [Header("Agua y profundidad")]
    [Tooltip("Altura base de la superficie en coordenadas del mundo.")]
    [SerializeField] private float nivelMar = 0f;
    [Tooltip("Metros bajo la superficie necesarios para Profundidad = 1.")]
    [Min(0.01f)]
    [SerializeField] private float profundidadMaxima = 1f;
    [Tooltip("Margen a cada lado de la superficie para evitar cruces repetidos.")]
    [Min(0f)]
    [SerializeField] private float margenCruce = 0.05f;
    [Tooltip("Tiempo de suavizado del parametro, en segundos.")]
    [Min(0f)]
    [SerializeField] private float suavizado = 0.2f;

    [Header("Estado (solo lectura durante la prueba)")]
    [SerializeField, Range(0f, 1f)] private float profundidadActual;
    [SerializeField] private bool bajoAgua;

    private EventInstance ambiente;
    private PARAMETER_ID profundidadId;
    private bool ambiente3D;
    private bool estadoInicializado;
    private bool iniciado;
    private float velocidadSuavizado;
    private StudioEventEmitter[] emisoresMar = new StudioEventEmitter[0];

    private void Start()
    {
        iniciado = true;
        IniciarAmbiente();
    }

    private void OnEnable()
    {
        if (iniciado)
            IniciarAmbiente();
    }

    private void IniciarAmbiente()
    {
        if (ambiente.isValid())
            return;

        // Solo recoger emisores de la raiz asignada en esta escena.
        emisoresMar = raizEmisoresMar != null
            ? raizEmisoresMar.GetComponentsInChildren<StudioEventEmitter>(true)
            : new StudioEventEmitter[0];

        if (cabeza == null || ambienteMar.IsNull)
        {
            Debug.LogError(
                "Talasofobia: asigna Cabeza y el evento Ambiente Mar.", this);
            return;
        }

        try
        {
            ambiente = RuntimeManager.CreateInstance(ambienteMar);
            var resultado = ambiente.getDescription(out EventDescription descripcion);
            if (resultado == FMOD.RESULT.OK)
                resultado = descripcion.is3D(out ambiente3D);

            if (resultado == FMOD.RESULT.OK)
            {
                resultado = descripcion.getParameterDescriptionByName(
                    "Profundidad", out PARAMETER_DESCRIPTION parametro);
                profundidadId = parametro.id;
            }

            if (resultado != FMOD.RESULT.OK)
            {
                Debug.LogError(
                    "Talasofobia: no se pudo preparar Ambiente_Mar/Profundidad: "
                    + resultado, this);
                DetenerAmbiente();
                return;
            }

            estadoInicializado = false;
            velocidadSuavizado = 0f;
            profundidadActual = 0f;
            ActualizarPosicion();
            ambiente.setParameterByID(profundidadId, 0f);
            resultado = ambiente.start();
            if (resultado != FMOD.RESULT.OK)
            {
                Debug.LogError("Talasofobia: no se pudo iniciar el ambiente: "
                    + resultado, this);
                DetenerAmbiente();
            }
        }
        catch (EventNotFoundException error)
        {
            Debug.LogError("Talasofobia: revisa los eventos y bancos FMOD. "
                + error.Message, this);
            DetenerAmbiente();
        }
    }

    private void LateUpdate()
    {
        if (!ambiente.isValid() || cabeza == null)
            return;

        ActualizarPosicion();

        float alturaAgua = nivelMar;
        if (simulacionOceano != null)
        {
            // La simulacion crea Collision durante su primer Update.
            if (!simulacionOceano.isActiveAndEnabled
                || simulacionOceano.Collision == null)
                return;

            // Readback Cascades debe estar en Largest o Two Largest.
            alturaAgua += simulacionOceano.Collision.GetWaterHeight(cabeza.position);
        }

        float metrosBajoAgua = alturaAgua - cabeza.position.y;
        float objetivo = Mathf.Clamp01(
            metrosBajoAgua / Mathf.Max(0.01f, profundidadMaxima));

        if (!estadoInicializado)
        {
            // No reproducir un cruce solo por iniciar la escena bajo el agua.
            bajoAgua = metrosBajoAgua > 0f;
            profundidadActual = objetivo;
            estadoInicializado = true;
        }
        else
        {
            float margen = Mathf.Max(0f, margenCruce);
            if (!bajoAgua && metrosBajoAgua > margen)
            {
                bajoAgua = true;
                ReproducirCruce(entrarAgua);
            }
            else if (bajoAgua && metrosBajoAgua < -margen)
            {
                bajoAgua = false;
                ReproducirCruce(salirAgua);
            }

            profundidadActual = suavizado <= 0f
                ? objetivo
                : Mathf.SmoothDamp(profundidadActual, objetivo,
                    ref velocidadSuavizado, suavizado);
        }

        ambiente.setParameterByID(profundidadId, profundidadActual);
        ActualizarProfundidadEmisores();
    }

    private void ActualizarProfundidadEmisores()
    {
        foreach (StudioEventEmitter emisor in emisoresMar)
        {
            if (emisor == null || !emisor.isActiveAndEnabled)
                continue;

            // El emisor conserva la gestion de su instancia y de su posicion.
            // SetParameter tambien actualiza la cache si FMOD ha detenido
            // una instancia por estar fuera de su distancia maxima.
            // El suavizado ya lo aplica este controlador.
            emisor.SetParameter("Profundidad", profundidadActual, true);
        }
    }

    private void ActualizarPosicion()
    {
        if (!ambiente3D)
            return;

        Transform origen = origenAmbiente != null ? origenAmbiente : cabeza;
        ambiente.set3DAttributes(RuntimeUtils.To3DAttributes(origen.gameObject));
    }

    private void ReproducirCruce(EventReference evento)
    {
        if (!evento.IsNull)
            RuntimeManager.PlayOneShot(evento, cabeza.position);
    }

    private void OnDisable()
    {
        DetenerAmbiente();
    }

    private void OnDestroy()
    {
        DetenerAmbiente();
    }

    private void DetenerAmbiente()
    {
        if (!ambiente.isValid())
            return;

        ambiente.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
        ambiente.release();
        ambiente.clearHandle();
        estadoInicializado = false;
    }
}


