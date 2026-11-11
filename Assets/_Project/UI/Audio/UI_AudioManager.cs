
using UnityEngine;
using FMODUnity;

public class UIAudioManager : MonoBehaviour
{
    public static UIAudioManager Instance { get; private set; }

    [Header("FMOD UI Events")]
    [SerializeField] private EventReference clickEvent;
    [SerializeField] private EventReference confirmEvent;
    [SerializeField] private EventReference hoverEvent;
    [SerializeField] private EventReference startEvent;
    [SerializeField] private EventReference rotateEvent;
    [SerializeField] private EventReference backEvent;

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void PlayUIEvent(EventReference eventReference)
    {
        if (!RuntimeManager.IsInitialized)
        {
            Debug.LogWarning("FMOD no está inicializado.");
            return;
        }

        RuntimeManager.PlayOneShot(eventReference);
    }

    public void PlayClick()
    {
        PlayUIEvent(clickEvent);
    }

    public void PlayConfirm()
    {
        PlayUIEvent(confirmEvent);
    }

    public void PlayHover()
    {
        PlayUIEvent(hoverEvent);
    }

    public void PlayStart()
    {
        PlayUIEvent(startEvent);
    }

    public void PlayRotate()
    {
        PlayUIEvent(rotateEvent);
    }

    public void PlayBack()
    {
        PlayUIEvent(backEvent);
    }
}
