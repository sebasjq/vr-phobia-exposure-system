using UnityEngine;

[DisallowMultipleComponent]
public class OpenExternalLink : MonoBehaviour
{
    [SerializeField]
    private string url =
        "https://github.com/sebasjq/vr-phobia-exposure-system";

    private double nextAllowedOpen = double.NegativeInfinity;

    public void OpenLink()
    {
        double now = Time.realtimeSinceStartupAsDouble;

        if (string.IsNullOrWhiteSpace(url) || now < nextAllowedOpen)
            return;

        // Bloquear antes de abrir el navegador.
        nextAllowedOpen = now + 1.0;

        Application.OpenURL(url);
    }
}