
using UnityEngine;
using FMODUnity;

public class CollapseAfterDelay : MonoBehaviour
{
    [Header("Tiempo antes del derrumbe")]
    [SerializeField] private float tiempoEspera = 10f;

    [Header("Objetos del derrumbe")]
    [SerializeField] private GameObject[] objetosDerrumbe;

    [Header("Audio FMOD")]
    [SerializeField] private EventReference sonidoDerrumbe;

    private bool derrumbeActivado;

    private void Start()
    {
        Invoke(nameof(ActivarDerrumbe), tiempoEspera);
    }

    public void ActivarDerrumbe()
    {
        if (derrumbeActivado)
            return;

        derrumbeActivado = true;

        // Activar objetos del derrumbe
        foreach (GameObject objeto in objetosDerrumbe)
        {
            if (objeto != null)
                objeto.SetActive(true);
        }

        // Reproducir sonido FMOD
        if (!sonidoDerrumbe.IsNull)
        {
            RuntimeManager.PlayOneShot(
                sonidoDerrumbe,
                transform.position
            );
        }

        Debug.Log("¡Derrumbe activado!");
    }
}
