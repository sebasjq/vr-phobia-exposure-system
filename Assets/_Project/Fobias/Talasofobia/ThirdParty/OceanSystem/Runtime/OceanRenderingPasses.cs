using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace OceanSystem
{
    public class OceanGeometryPass : ScriptableRenderPass
    {
        private readonly static ShaderTagId OceanShaderTagId = new ShaderTagId("OceanMain");
        private readonly static ProfilingSampler _profilingSampler = new ProfilingSampler("Ocean Geometry");
        private readonly OceanRendererFeature.OceanRenderingSettings _settings;
        private FilteringSettings _filteringSettings;

        public OceanGeometryPass(OceanRendererFeature.OceanRenderingSettings settings)
        {
            _settings = settings;
            _filteringSettings = new FilteringSettings(RenderQueueRange.all);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            if (!OceanRendererFeature.IsRendering) return;
#endif

            CameraData cameraData = renderingData.cameraData;
            if (!OceanRendererFeature.IsCorrectCameraType(cameraData.cameraType)) return;
            Camera camera = cameraData.camera;

            DrawingSettings drawingSettings = new DrawingSettings(OceanShaderTagId,
                new SortingSettings(camera));
            drawingSettings.enableInstancing = true;

            CommandBuffer cmd = CommandBufferPool.Get();
            // Bind all XR slices, preserving color and depth for ocean occlusion.
            cmd.SetRenderTarget(
                new RenderTargetIdentifier(cameraData.renderer.cameraColorTargetHandle.nameID, 0, CubemapFace.Unknown, -1),
                new RenderTargetIdentifier(cameraData.renderer.cameraDepthTargetHandle.nameID, 0, CubemapFace.Unknown, -1));
            cmd.SetViewport(new Rect(0, 0, cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height));
            SetupGlobalKeywords(cmd);
            SetupCameraGlobals(cmd, camera);
            context.ExecuteCommandBuffer(cmd);

            using (new ProfilingScope(cmd, _profilingSampler))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                drawingSettings.perObjectData = PerObjectData.LightProbe;
                context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref _filteringSettings);
            }
            context.ExecuteCommandBuffer(cmd);

            
            CommandBufferPool.Release(cmd);
        }

        private void SetupCameraGlobals(CommandBuffer cmd, Camera cam)
        {
            cmd.SetGlobalMatrix(GlobalShaderVariables.Misc.InverseViewMatrix, cam.cameraToWorldMatrix);
            cmd.SetGlobalMatrix(GlobalShaderVariables.Misc.InverseProjectionMatrix,
                GL.GetGPUProjectionMatrix(cam.projectionMatrix, false).inverse);
        }

        private void SetupGlobalKeywords(CommandBuffer cmd)
        {
            SetGlobalKeyword(cmd, "OCEAN_TRANSPARENCY_ENABLED", _settings.transparency);
            SetGlobalKeyword(cmd, "OCEAN_UNDERWATER_ENABLED", _settings.underwaterEffect);
        }

        private void SetGlobalKeyword(CommandBuffer cmd, string keyword, bool b)
        {
            if (b)
                cmd.EnableShaderKeyword(keyword);
            else
                cmd.DisableShaderKeyword(keyword);
        }
    }

    public class OceanUnderwaterEffectPass : ScriptableRenderPass
    {
        private readonly OceanRendererFeature.OceanRenderingSettings _settings;
        private readonly Material _underwaterEffectMaterial;
        private RenderTargetIdentifier _submergenceTarget;
        private static readonly int _submergenceTargetID = Shader.PropertyToID("SubmergenceTarget");
        

        public OceanUnderwaterEffectPass(OceanRendererFeature.OceanRenderingSettings settings)
        {
            _settings = settings;
            _underwaterEffectMaterial = new Material(Shader.Find("Hidden/Ocean/UnderwaterEffect"));
            _underwaterEffectMaterial.enableInstancing = true;
            ConfigureInput(ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth);
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            // Preserve camera texture dimension and volumeDepth (two slices in SPI).
            RenderTextureDescriptor descriptor = cameraTextureDescriptor;
            descriptor.width = 128;
            descriptor.height = 128;
            descriptor.msaaSamples = 1;
            descriptor.bindMS = false;
            descriptor.depthBufferBits = 0;
            descriptor.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8_UNorm;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.enableRandomWrite = false;
            descriptor.useDynamicScale = false;
            cmd.GetTemporaryRT(_submergenceTargetID, descriptor, FilterMode.Bilinear);
            _submergenceTarget = new RenderTargetIdentifier(_submergenceTargetID, 0, CubemapFace.Unknown, -1);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            if (!OceanRendererFeature.IsRendering) return;
#endif

            CameraData cameraData = renderingData.cameraData;
            if (!OceanRendererFeature.IsCorrectCameraType(cameraData.cameraType)) return;

            if (_settings.underwaterEffect)
            {
                CommandBuffer cmd = CommandBufferPool.Get("Underwater Effect");
                cmd.SetRenderTarget(_submergenceTarget);
                cmd.SetViewport(new Rect(0, 0, 128, 128));
                // URP's active XR pass supplies the stereo instance multiplier.
                cmd.DrawProcedural(Matrix4x4.identity, _underwaterEffectMaterial, 0, MeshTopology.Quads, 4, 1);
                cmd.SetGlobalTexture(GlobalShaderVariables.Misc.SubmergenceTexture, _submergenceTargetID);

                RenderTargetIdentifier cameraColor = new RenderTargetIdentifier(
                    cameraData.renderer.cameraColorTargetHandle.nameID, 0, CubemapFace.Unknown, -1);
                cmd.SetRenderTarget(cameraColor);
                cmd.SetViewport(new Rect(0, 0, cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height));
                cmd.DrawProcedural(Matrix4x4.identity, _underwaterEffectMaterial, 1, MeshTopology.Quads, 4, 1);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }

        public override void FrameCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(_submergenceTargetID);
        }
    }

    public class OceanSkyMapPass : ScriptableRenderPass
    {
        private OceanRendererFeature.OceanRenderingSettings _settings;

        
        private readonly Material _skyMapMaterial;
        private RenderTexture _skyMap;
        private bool _skyMapRendered;
        private bool NeedToRenderSkyMap => _settings.updateSkyMap || !_skyMapRendered;

        public OceanSkyMapPass(OceanRendererFeature.OceanRenderingSettings settings)
        {
            this._settings = settings;
            _skyMapMaterial = new Material(Shader.Find("Hidden/Ocean/StereographicSky"));
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            if (NeedToRenderSkyMap)
            {
                CreateSkyMapTexture();
                // Target is set explicitly when drawing the sky map.
            } 
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            if (!OceanRendererFeature.IsRendering) return;
#endif

            if (NeedToRenderSkyMap)
            {
                CameraData cameraData = renderingData.cameraData;
                if (!OceanRendererFeature.IsCorrectCameraType(cameraData.cameraType)) return;

                CommandBuffer cmd = CommandBufferPool.Get("Ocean Sky Map");
                RenderSkyMap(cmd);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }

        private void RenderSkyMap(CommandBuffer cmd)
        {
            // Procedural draw avoids cmd.Blit altering XR keywords.
            cmd.SetRenderTarget(_skyMap);
            cmd.SetViewport(new Rect(0, 0, _skyMap.width, _skyMap.height));
            cmd.DrawProcedural(Matrix4x4.identity, _skyMapMaterial, 0, MeshTopology.Quads, 4, 1);
            cmd.SetGlobalTexture(GlobalShaderVariables.Misc.SkyMap, _skyMap);
            _skyMapRendered = true;
        }

        private void CreateSkyMapTexture()
        {
            if (_skyMap == null || _skyMap.height != _settings.skyMapResolution)
            {
                if (_skyMap != null)
                    _skyMap.Release();
                _skyMap = new RenderTexture(_settings.skyMapResolution, _settings.skyMapResolution, 0, 
                    RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear)
                {
                    name = "SkyMap",
                    antiAliasing = 1,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = true,
                    autoGenerateMips = true,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 9
                };
                _skyMap.Create();
            }
        }
    }
}
