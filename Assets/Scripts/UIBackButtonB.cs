using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Pulsa el Volver disponible de la interfaz al presionar B en el mando derecho.
/// Colocar una sola instancia en Carrusel_UI, que debe permanecer activo.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("UI/Volver con B")]
public sealed class UIBackButtonB : MonoBehaviour
{
    [Header("Botones Volver")]
    [Tooltip("Arrastra los objetos Button desde Hierarchy. Incluye los Volver de " +
             "Proyecto, Controles y todos los paneles de exposición. " +
             "Si hay varios disponibles, se usa el primero de la lista.")]
    [SerializeField] private Button[] backButtons = new Button[0];

    [Header("Entrada")]
    [Tooltip("Intervalo mínimo entre acciones. Las transiciones también deben " +
             "mantener deshabilitados los botones, como hace CircularMenu.")]
    [SerializeField, Min(0.05f)] private float repeatDelay = 0.25f;

    [Tooltip("En Play Mode también permite probar Volver con Escape, " +
             "con la ventana Game enfocada.")]
    [SerializeField] private bool testWithEscapeInEditor = true;

    private double nextAllowedTime = double.NegativeInfinity;

    private void LateUpdate()
    {
        // Two + RTouch corresponde a B. LTouch correspondería a Y.
        // GetDown produce una acción al pulsar, sin repetir al mantener B.
        bool backPressed = OVRInput.GetDown(
            OVRInput.Button.Two, OVRInput.Controller.RTouch);

#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM
        if (testWithEscapeInEditor && Keyboard.current != null)
            backPressed |= Keyboard.current.escapeKey.wasPressedThisFrame;
#elif UNITY_EDITOR && ENABLE_LEGACY_INPUT_MANAGER
        if (testWithEscapeInEditor)
            backPressed |= Input.GetKeyDown(KeyCode.Escape);
#endif

        if (!backPressed || Time.unscaledTimeAsDouble < nextAllowedTime)
            return;

        if (backButtons == null)
            return;

        foreach (Button button in backButtons)
        {
            if (!IsAvailable(button))
                continue;

            // Bloquear antes del evento y terminar tras UNA acción:
            // cerrar exposición no debe activar otro Volver en esta pulsación.
            nextAllowedTime = Time.unscaledTimeAsDouble + repeatDelay;
            button.onClick.Invoke();
            return;
        }
    }

    private static bool IsAvailable(Button button)
    {
        if (button == null || !button.IsActive() || !button.IsInteractable())
            return false;

        // Los paneles del carrusel pueden seguir activos aunque estén ocultos.
        // IsInteractable respeta los CanvasGroup; revisar alpha descarta además
        // un panel totalmente transparente que aún admita interacción.
        foreach (CanvasGroup group in button.GetComponentsInParent<CanvasGroup>())
        {
            if (group.isActiveAndEnabled && group.alpha <= 0.01f)
                return false;
        }

        Canvas canvas = button.GetComponentInParent<Canvas>();
        return canvas == null || canvas.isActiveAndEnabled;
    }
}
