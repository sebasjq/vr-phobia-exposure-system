using UnityEngine;
using UnityEngine.SceneManagement;

public class CargarEscena : MonoBehaviour
{
    private bool cargando;

    public void Cargar(string nombreEscena)
    {
        if (cargando)
            return;

        if (!Application.CanStreamedLevelBeLoaded(nombreEscena))
        {
            Debug.LogError(
                $"La escena '{nombreEscena}' no está incluida en la compilación."
            );
            return;
        }

        cargando = true;
        SceneManager.LoadSceneAsync(nombreEscena);
    }
}