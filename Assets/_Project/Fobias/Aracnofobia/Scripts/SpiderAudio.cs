using UnityEngine;

/// <summary>
/// Conecta el estado de la araña con FMOD. Va en el MISMO objeto que SpiderController.
///
///  - Patas: un Studio Event Emitter (evento "Patas", en bucle) vive en la araña y sigue su posición.
///           Este script solo cambia el parámetro GLOBAL "SpiderSpeed" de FMOD:
///           0 = quieta (silencio)  1 = caminando  2 = corriendo.
///  - Ataque: al entrar en el estado Attack se dispara el evento "spider_attack" pegado a la araña.
///
/// Requiere el símbolo USE_FMOD. Sin él, solo escribe en consola (si AudioBridge.LogCalls está activo).
/// </summary>
[RequireComponent(typeof(SpiderController))]
public class SpiderAudio : MonoBehaviour
{
#if USE_FMOD
    [Header("FMOD")]
    [Tooltip("Evento del ataque (disparo único).")]
    [SerializeField] FMODUnity.EventReference attackEvent;
#endif

    [Header("Parámetro GLOBAL de FMOD para las patas")]
    [SerializeField] string speedParameter = "SpiderSpeed";
    [SerializeField] float idleValue = 0f;
    [SerializeField] float walkValue = 1f;
    [SerializeField] float runValue = 2f;

    SpiderController spider;

    void Awake()
    {
        spider = GetComponent<SpiderController>();
    }

    void OnEnable()
    {
        spider.StateChanged += OnStateChanged;
    }

    void OnDisable()
    {
        spider.StateChanged -= OnStateChanged;
    }

    void OnStateChanged(SpiderState state)
    {
        float speed = state switch
        {
            SpiderState.Walk => walkValue,
            SpiderState.Run => runValue,
            _ => idleValue          // Idle, Hidden y Attack: las patas se callan
        };

        SetSpeed(speed);

        if (state == SpiderState.Attack) PlayAttack();
    }

    void SetSpeed(float value)
    {
        AudioBridge.SetGlobalParameter(speedParameter, value);
    }

    void PlayAttack()
    {
        if (AudioBridge.LogCalls) Debug.Log("[Audio] Spider attack");
#if USE_FMOD
        if (!attackEvent.IsNull) FMODUnity.RuntimeManager.PlayOneShotAttached(attackEvent, gameObject);
#endif
    }
}