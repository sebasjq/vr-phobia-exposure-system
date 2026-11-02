using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class CircularMenu : MonoBehaviour
{
    [System.Serializable]
    public class MenuView
    {
        public Transform root;
        public CanvasGroup canvasGroup;
        public Renderer glassRenderer;

        [HideInInspector] public Button[] buttons;
        [HideInInspector] public bool[] originalInteractable;

        // Permite controlar cada vidrio sin modificar el material compartido.
        [System.NonSerialized]
        public MaterialPropertyBlock glassProperties;

        public Behaviour rayInteractable;
        public Behaviour pokeInteractable;
    }

    [SerializeField, Min(0.05f)]
    private float duration = 1.2f;

    // Orden: menú inicial, proyecto y controles.
    [SerializeField]
    private float[] targetAngles = { 0f, 120f, -120f };

    [SerializeField]
    private MenuView[] views = new MenuView[3];

    private static readonly int FadeId = Shader.PropertyToID("_Fade");

    private Quaternion initialRotation;
    private int currentView;
    private bool isMoving;

    private void Start()
    {
        initialRotation = transform.localRotation;

        // Guarda los estados originales antes de bloquear cualquier menú.
        foreach (MenuView view in views)
        {
            view.buttons = view.root.GetComponentsInChildren<Button>(true);
            view.originalInteractable = new bool[view.buttons.Length];
            view.glassProperties = new MaterialPropertyBlock();

            for (int i = 0; i < view.buttons.Length; i++)
            {
                view.originalInteractable[i] =
                    view.buttons[i].interactable;
            }
        }

        // Al comenzar, solo el menú inicial es visible e interactuable.
        currentView = 0;

        for (int i = 0; i < views.Length; i++)
        {
            SetVisibility(i, i == currentView ? 1f : 0f);
            SetInteraction(i, i == currentView);
        }
    }

    // Conserva la misma función para los eventos On Click().
    public void GoTo(int viewIndex)
    {
        if (isMoving ||
            viewIndex < 0 ||
            viewIndex >= views.Length ||
            viewIndex >= targetAngles.Length ||
            viewIndex == currentView)
        {
            return;
        }

        StartCoroutine(RotateTo(viewIndex));
    }

    private void SetVisibility(int index, float alpha)
    {
        MenuView view = views[index];

        view.canvasGroup.alpha = alpha;

        if (view.glassRenderer != null)
        {
            view.glassRenderer.GetPropertyBlock(view.glassProperties);
            view.glassProperties.SetFloat(FadeId, alpha);
            view.glassRenderer.SetPropertyBlock(view.glassProperties);
        }
    }

    private void SetInteraction(int index, bool enabled)
    {
        MenuView view = views[index];

        view.canvasGroup.interactable = enabled;
        view.canvasGroup.blocksRaycasts = enabled;

        for (int i = 0; i < view.buttons.Length; i++)
        {
            if (view.buttons[i] == null)
                continue;

            view.buttons[i].interactable =
                enabled && view.originalInteractable[i];
        }

        // Controla la detección de los interactores de Meta.
        if (view.rayInteractable != null)
            view.rayInteractable.enabled = enabled;

        if (view.pokeInteractable != null)
            view.pokeInteractable.enabled = enabled;
    }

    private IEnumerator RotateTo(int nextView)
    {
        isMoving = true;

        for (int i = 0; i < views.Length; i++)
        {
            SetInteraction(i, false);
        }

        int outgoingView = currentView;

        Quaternion startRotation = transform.localRotation;
        Quaternion targetRotation =
            initialRotation *
            Quaternion.Euler(0f, targetAngles[nextView], 0f);

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;

            float progress = Mathf.Clamp01(elapsed / duration);
            float easedProgress = Mathf.SmoothStep(0f, 1f, progress);

            transform.localRotation = Quaternion.Slerp(
                startRotation,
                targetRotation,
                easedProgress
            );

            SetVisibility(outgoingView, 1f - easedProgress);
            SetVisibility(nextView, easedProgress);

            yield return null;
        }

        transform.localRotation = targetRotation;
        currentView = nextView;

        for (int i = 0; i < views.Length; i++)
        {
            SetVisibility(i, i == currentView ? 1f : 0f);
            SetInteraction(i, i == currentView);
        }

        isMoving = false;
    }
}