
using UnityEngine;

public class AnimationSegment : MonoBehaviour
{
    [SerializeField] private Animation anim;
    [SerializeField] private string clipName = "Animation";

    [Header("Tiempo de reproducción (segundos)")]
    [SerializeField] private float startTime = 0.33f;
    [SerializeField] private float endTime = 7f;

    [Header("Control de colisiones")]
    [SerializeField] private Collider doorCollider;

    [Header("Efectos de avería")]
    [SerializeField] private ElevatorFailureEffects failureEffects;

    private AnimationState state;
    private bool isPlayingSegment;

    private void Awake()
    {
        if (anim == null)
            anim = GetComponent<Animation>();

        if (anim == null)
        {
            Debug.LogError("No se encontró el componente Animation.", this);
            return;
        }

        state = anim[clipName];

        if (state == null)
        {
            Debug.LogError("No se encontró el clip: " + clipName, this);
            return;
        }

        state.wrapMode = WrapMode.ClampForever;

        // Estado inicial: puertas abiertas.
        if (doorCollider != null)
            doorCollider.enabled = false;
    }

    public void PlaySegment()
    {
        if (state == null) return;

        anim.Stop(clipName);

        // Desactivar colisión mientras las puertas se mueven.
        if (doorCollider != null)
            doorCollider.enabled = false;

        state.speed = 1f;
        anim.Play(clipName);
        state.time = startTime;

        isPlayingSegment = true;
    }

    private void Update()
    {
        if (isPlayingSegment && state.time >= endTime)
        {
            anim.Stop(clipName);
            isPlayingSegment = false;

            // Activar colisión cuando las puertas terminan de cerrarse.
            if (doorCollider != null)
                doorCollider.enabled = true;

            // Iniciar la sacudida del ascensor.
            if (failureEffects != null)
                failureEffects.PlayFailureShake();
        }
    }
}
