using System.Collections;
using UnityEngine;
using FMODUnity;

public class AscensorAudio : MonoBehaviour
{
    [Header("Loops (Studio Event Emitter con Play Trigger = None)")]
    public StudioEventEmitter alarma;

    [Header("One-shots")]
    public EventReference averia;

    [Header("Posiciones")]
    public Transform ascensor;
    public Transform puerta;

    public void IniciarAveria()
    {
        RuntimeManager.PlayOneShot(averia, ascensor.position);
        alarma.Play();
       
    }

    public void DetenerAlarma() => alarma.Stop();


}
