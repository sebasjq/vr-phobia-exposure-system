using OceanSystem;
using UnityEngine;

/// <summary>Removes rain particles below the ocean height sampled at their position.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ParticleSystem))]
public sealed class RainOceanCollision : MonoBehaviour
{
    [SerializeField] private OceanSimulation ocean;

    [Tooltip("Base water height in world coordinates. Use 0 for the supplied ocean at the origin.")]
    [SerializeField] private float seaLevel = 0f;

    [Tooltip("Positive values remove droplets slightly above the sampled surface.")]
    [SerializeField] private float surfaceOffset = 0.1f;

    private ParticleSystem rain;
    private ParticleSystem.Particle[] particles;

    private void Awake()
    {
        rain = GetComponent<ParticleSystem>();
        if (ocean == null)
        {
            Debug.LogError("RainOceanCollision: assign the Ocean object to Ocean.", this);
            enabled = false;
        }
    }

    private void LateUpdate()
    {
        if (ocean == null || !ocean.isActiveAndEnabled || ocean.Collision == null)
            return;

        var main = rain.main;
        if (particles == null || particles.Length < main.maxParticles)
            particles = new ParticleSystem.Particle[main.maxParticles];

        // GetParticles positions use the particle system's simulation space.
        Transform simulationTransform = null;
        if (main.simulationSpace == ParticleSystemSimulationSpace.Local)
            simulationTransform = transform;
        else if (main.simulationSpace == ParticleSystemSimulationSpace.Custom)
            simulationTransform = main.customSimulationSpace != null
                ? main.customSimulationSpace : transform;

        Matrix4x4 toWorld = simulationTransform != null
            ? simulationTransform.localToWorldMatrix : Matrix4x4.identity;

        int count = rain.GetParticles(particles);
        bool changed = false;
        var collision = ocean.Collision;

        for (int i = 0; i < count; i++)
        {
            Vector3 worldPosition = toWorld.MultiplyPoint3x4(particles[i].position);
            float height = seaLevel + collision.GetWaterHeight(worldPosition);

            if (worldPosition.y <= height + surfaceOffset)
            {
                particles[i].remainingLifetime = -1f;
                changed = true;
            }
        }

        if (changed)
            rain.SetParticles(particles, count);
    }
}
