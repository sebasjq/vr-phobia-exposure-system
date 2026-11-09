
using UnityEngine;
using FMODUnity;

public class FootstepsFMOD : MonoBehaviour
{
    [Header("FMOD")]
    [SerializeField]
    private EventReference pasosEvent;

    [Header("Movimiento")]
    [SerializeField]
    private Transform jugador;

    [SerializeField]
    private float velocidadMinima = 0.1f;

    [SerializeField]
    private float intervaloPasos = 0.5f;

    [Header("Audio")]
    [SerializeField]
    private float volumen = 1f;

    private Vector3 posicionAnterior;
    private float temporizador;
    private float velocidadActual;

    private void Start()
    {
        if (jugador == null)
        {
            jugador = transform;
        }

        posicionAnterior = jugador.position;
    }

    private void Update()
    {
        // Calcular desplazamiento del jugador
        Vector3 desplazamiento =
            jugador.position - posicionAnterior;

        // Ignorar movimiento vertical
        desplazamiento.y = 0f;

        velocidadActual =
            desplazamiento.magnitude / Time.deltaTime;

        posicionAnterior = jugador.position;

        // Comprobar si está caminando
        if (velocidadActual > velocidadMinima)
        {
            temporizador += Time.deltaTime;

            if (temporizador >= intervaloPasos)
            {
                ReproducirPaso();
                temporizador = 0f;
            }
        }
        else
        {
            temporizador = 0f;
        }
    }

    private void ReproducirPaso()
    {
        RuntimeManager.PlayOneShot(
            pasosEvent,
            jugador.position
        );
    }
}

