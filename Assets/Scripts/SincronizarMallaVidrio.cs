using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
public class SincronizarMallaVidrio : MonoBehaviour
{
    [SerializeField] private MeshFilter mallaOriginal;

    private MeshFilter mallaVidrio;

    private void Awake()
    {
        mallaVidrio = GetComponent<MeshFilter>();
    }

    private void LateUpdate()
    {
        if (mallaOriginal == null)
            return;

        // Comparte la geometría generada por Meta.
        if (mallaVidrio.sharedMesh != mallaOriginal.sharedMesh)
        {
            mallaVidrio.sharedMesh = mallaOriginal.sharedMesh;
        }
    }
}