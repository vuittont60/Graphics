using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    // All of TAA here, work on TAA == work on this file.

    internal enum TemporalAAQuality
    {
        VeryLow = 0,
        Low,
        Medium,
        High,
        VeryHigh
    }

    internal static class TemporalAA
    {
        public sealed class PersistentData : CameraHistoryItem
        {
            private int[] m_TaaAccumulationTextureIds = new int[2];
            private int[] m_TaaAccumulationVersions = new int[2];
            private static readonly string[] m_TaaAccumulationNames = new []
            {
                "TaaAccumulationTex0",
                "TaaAccumulationTex1"
            };

            private RenderTextureDescriptor m_Descriptor;
            private Hash128 m_DescKey;

            /// <summary>
            /// Called internally on instance creation.
            /// Sets up RTHandle ids.
            /// </summary>
            public override void OnCreate(BufferedRTHandleSystem owner, uint typeId)
            {
                base.OnCreate(owner, typeId);
                m_TaaAccumulationTextureIds[0] = MakeId(0);
                m_TaaAccumulationTextureIds[1] = MakeId(1);
            }

            /// <summary>
            /// Release TAA accumulation textures.
            /// </summary>
            public override void Reset()
            {
                for (int i = 0; i < m_TaaAccumulationTextureIds.Length; i++)
                {
                    ReleaseHistoryFrameRT(m_TaaAccumulationTextureIds[i]);
                    m_TaaAccumulationVersions[i] = -1;
                }

                m_Descriptor.width = 0;
                m_Descriptor.height = 0;
                m_Descriptor.graphicsFormat = GraphicsFormat.None;
                m_DescKey = Hash128.Compute(0);
            }

            /// <summary>
            /// Get TAA accumulation texture.
            /// </summary>
            public RTHandle GetAccumulationTexture(int eyeIndex = 0)
            {
                return GetCurrentFrameRT(m_TaaAccumulationTextureIds[eyeIndex]);
            }

            /// <summary>
            /// Get TAA accumulation texture version.
            /// </summary>
            // Tracks which frame the accumulation was last updated.
            public int GetAccumulationVersion(int eyeIndex = 0)
            {
                return m_TaaAccumulationVersions[eyeIndex];
            }

            internal void SetAccumulationVersion(int eyeIndex, int version)
            {
                m_TaaAccumulationVersions[eyeIndex] = version;
            }

            // Check if the TAA accumulation texture is valid.
            private bool IsValid()
            {
                return GetAccumulationTexture(0) != null;
            }

            // True if the desc changed, graphicsFormat etc.
            private bool IsDirty(ref RenderTextureDescriptor desc)
            {
                return m_DescKey != Hash128.Compute(ref desc);
            }

            private void Alloc(ref RenderTextureDescriptor desc, bool xrMultipassEnabled)
            {
                AllocHistoryFrameRT(m_TaaAccumulationTextureIds[0], 1, ref desc,  m_TaaAccumulationNames[0]);

                if (xrMultipassEnabled)
                    AllocHistoryFrameRT(m_TaaAccumulationTextureIds[1], 1, ref desc,  m_TaaAccumulationNames[1]);

                m_Descriptor = desc;
                m_DescKey = Hash128.Compute(ref desc);
            }

            // Return true if the RTHandles were reallocated.
            internal bool Update(ref RenderTextureDescriptor cameraDesc, bool xrMultipassEnabled = false)
            {
                if (cameraDesc.width > 0 && cameraDesc.height > 0 && cameraDesc.graphicsFormat != GraphicsFormat.None)
                {
                    var taaDesc = TemporalAA.TemporalAADescFromCameraDesc(ref cameraDesc);

                    if (IsDirty(ref taaDesc))
                        Reset();

                    if (!IsValid())
                    {
                        Alloc(ref taaDesc, xrMultipassEnabled);
                        return true;
                    }
                }
                return false;
            }
        }

        internal static class ShaderConstants
        {
            public static readonly int _TaaAccumulationTex = Shader.PropertyToID("_TaaAccumulationTex");
            public static readonly int _TaaMotionVectorTex = Shader.PropertyToID("_TaaMotionVectorTex");

            public static readonly int _TaaFilterWeights   = Shader.PropertyToID("_TaaFilterWeights");

            public static readonly int _TaaFrameInfluence     = Shader.PropertyToID("_TaaFrameInfluence");
            public static readonly int _TaaVarianceClampScale = Shader.PropertyToID("_TaaVarianceClampScale");

            public static readonly int _CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
        }

        internal static class ShaderKeywords
        {
            public static readonly string TAA_LOW_PRECISION_SOURCE = "TAA_LOW_PRECISION_SOURCE";
        }

        [Serializable]
        internal struct Settings
        {
            public TemporalAAQuality quality;
            public float frameInfluence;
            public float jitterScale;
            public float mipBias;
            public float varianceClampScale;
            public float contrastAdaptiveSharpening;

            [NonSerialized] public int resetHistoryFrames;      // Number of frames the history is reset. 0 no reset, 1 normal reset, 2 XR reset, -1 infinite (toggle on)
            [NonSerialized] public int jitterFrameCountOffset;  // Jitter "seed" == Time.frameCount + jitterFrameCountOffset. Used for testing determinism.

            public static Settings Create()
            {
                Settings s;

                s.quality                    = TemporalAAQuality.High;
                s.frameInfluence             = 0.1f;
                s.jitterScale                = 1.0f;
                s.mipBias                    = 0.0f;
                s.varianceClampScale         = 0.9f;
                s.contrastAdaptiveSharpening = 0.0f; // Disabled

                s.resetHistoryFrames = 0;
                s.jitterFrameCountOffset = 0;

                return s;
            }
        }


        /// <summary>
        /// A function delegate that returns a jitter offset for the provided frame
        /// This provides support for cases where a non-standard jitter pattern is desired
        /// </summary>
        /// <param name="frameIndex">index of the current frame</param>
        /// <param name="jitter">computed jitter offset</param>
        /// <param name="allowScaling">true if the jitter function's output supports scaling</param>
        internal delegate void JitterFunc(int frameIndex, out Vector2 jitter, out bool allowScaling);

        static internal Matrix4x4 CalculateJitterMatrix(UniversalCameraData cameraData, JitterFunc jitterFunc)
        {
            Matrix4x4 jitterMat = Matrix4x4.identity;

            bool isJitter = cameraData.IsTemporalAAEnabled();
            if (isJitter)
            {
                int taaFrameCountOffset = cameraData.taaSettings.jitterFrameCountOffset;
                int taaFrameIndex = Time.frameCount + taaFrameCountOffset;

                float actualWidth = cameraData.cameraTargetDescriptor.width;
                float actualHeight = cameraData.cameraTargetDescriptor.height;
                float jitterScale = cameraData.taaSettings.jitterScale;

                Vector2 jitter;
                bool allowScaling;
                jitterFunc(taaFrameIndex, out jitter, out allowScaling);

                if (allowScaling)
                    jitter *= jitterScale;

                float offsetX = jitter.x * (2.0f / actualWidth);
                float offsetY = jitter.y * (2.0f / actualHeight);

                jitterMat = Matrix4x4.Translate(new Vector3(offsetX, offsetY, 0.0f));
            }

            return jitterMat;
        }

        static void CalculateJitter(int frameIndex, out Vector2 jitter, out bool allowScaling)
        {
            // The variance between 0 and the actual halton sequence values reveals noticeable
            // instability in Unity's shadow maps, so we avoid index 0.
            float jitterX = HaltonSequence.Get((frameIndex & 1023) + 1, 2) - 0.5f;
            float jitterY = HaltonSequence.Get((frameIndex & 1023) + 1, 3) - 0.5f;

            jitter = new Vector2(jitterX, jitterY);
            allowScaling = true;
        }

        // Static allocation of JitterFunc delegate to avoid GC
        internal static JitterFunc s_JitterFunc = CalculateJitter;

        private static readonly Vector2[] taaFilterOffsets = new Vector2[]
        {
            new Vector2(0.0f, 0.0f),

            new Vector2(0.0f, 1.0f),
            new Vector2(1.0f, 0.0f),
            new Vector2(-1.0f, 0.0f),
            new Vector2(0.0f, -1.0f),

            new Vector2(-1.0f, 1.0f),
            new Vector2(1.0f, -1.0f),
            new Vector2(1.0f, 1.0f),
            new Vector2(-1.0f, -1.0f)
        };

        private static readonly float[] taaFilterWeights = new float[taaFilterOffsets.Length + 1];

        internal static float[] CalculateFilterWeights(float jitterScale)
        {
            // Based on HDRP
            // Precompute weights used for the Blackman-Harris filter.
            float totalWeight = 0;
            for (int i = 0; i < 9; ++i)
            {
                // The internal jitter function used by TAA always allows scaling
                CalculateJitter(Time.frameCount, out var jitter, out var _);
                jitter *= jitterScale;

                // The rendered frame (pixel grid) is already jittered.
                // We sample 3x3 neighbors with int offsets, but weight the samples
                // relative to the distance to the non-jittered pixel center.
                // From the POV of offset[0] at (0,0), the original pixel center is at (-jitter.x, -jitter.y).
                float x = taaFilterOffsets[i].x - jitter.x;
                float y = taaFilterOffsets[i].y - jitter.y;
                float d2 = (x * x + y * y);

                taaFilterWeights[i] = Mathf.Exp((-0.5f / (0.22f)) * d2);
                totalWeight += taaFilterWeights[i];
            }

            // Normalize weights.
            for (int i = 0; i < 9; ++i)
            {
                taaFilterWeights[i] /= totalWeight;
            }

            return taaFilterWeights;
        }

        internal static GraphicsFormat[] AccumulationFormatList = new GraphicsFormat[]
        {
            GraphicsFormat.R16G16B16A16_SFloat,
            GraphicsFormat.B10G11R11_UFloatPack32,
            GraphicsFormat.R8G8B8A8_UNorm,
            GraphicsFormat.B8G8R8A8_UNorm,
        };

        internal static RenderTextureDescriptor TemporalAADescFromCameraDesc(ref RenderTextureDescriptor cameraDesc)
        {
            RenderTextureDescriptor taaDesc = cameraDesc;

            // Explicitly set from cameraDesc.* for clarity.
            taaDesc.width = cameraDesc.width;
            taaDesc.height = cameraDesc.height;
            taaDesc.msaaSamples = 1;
            taaDesc.volumeDepth = cameraDesc.volumeDepth;
            taaDesc.mipCount = 0;
            taaDesc.graphicsFormat = cameraDesc.graphicsFormat;
            taaDesc.sRGB = false;
            taaDesc.depthBufferBits = 0;
            taaDesc.dimension = cameraDesc.dimension;
            taaDesc.vrUsage = cameraDesc.vrUsage;
            taaDesc.memoryless = RenderTextureMemoryless.None;
            taaDesc.useMipMap = false;
            taaDesc.autoGenerateMips = false;
            taaDesc.enableRandomWrite = false;
            taaDesc.bindMS = false;
            taaDesc.useDynamicScale = false;

            if (!SystemInfo.IsFormatSupported(taaDesc.graphicsFormat, GraphicsFormatUsage.Render))
            {
                taaDesc.graphicsFormat = GraphicsFormat.None;
                for (int i = 0; i < AccumulationFormatList.Length; i++)
                    if (SystemInfo.IsFormatSupported(AccumulationFormatList[i], GraphicsFormatUsage.Render))
                    {
                        taaDesc.graphicsFormat = AccumulationFormatList[i];
                        break;
                    }
            }

            return taaDesc;
        }

        internal static string ValidateAndWarn(UniversalCameraData cameraData)
        {
            string warning = null;

            if (cameraData.taaPersistentData == null)
            {
                warning = "Disabling TAA due to invalid persistent data.";
            }

            if (warning == null && cameraData.cameraTargetDescriptor.msaaSamples != 1)
            {
                if (cameraData.xr != null && cameraData.xr.enabled)
                    warning = "Disabling TAA because MSAA is on. MSAA must be disabled globally for all cameras in XR mode.";
                else
                    warning = "Disabling TAA because MSAA is on.";
            }

            if(warning == null && cameraData.camera.TryGetComponent<UniversalAdditionalCameraData>(out var additionalCameraData))
            {
                if (additionalCameraData.renderType == CameraRenderType.Overlay ||
                    additionalCameraData.cameraStack.Count > 0)
                {
                    warning = "Disabling TAA because camera is stacked.";
                }
            }

            if (warning == null && cameraData.camera.allowDynamicResolution)
                warning = "Disabling TAA because camera has dynamic resolution enabled. You can use a constant render scale instead.";

            if(warning == null && !cameraData.postProcessEnabled)
                warning = "Disabling TAA because camera has post-processing disabled.";

            const int warningThrottleFrames = 60 * 1; // 60 FPS * 1 sec
            if(Time.frameCount % warningThrottleFrames == 0)
                Debug.LogWarning(warning);

            return warning;
        }

        internal static void ExecutePass(CommandBuffer cmd, Material taaMaterial, ref CameraData cameraData, RTHandle source, RTHandle destination, RenderTexture motionVectors)
        {
            using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.TemporalAA)))
            {
                int multipassId = 0;
#if ENABLE_VR && ENABLE_XR_MODULE
                multipassId = cameraData.xr.multipassId;
#endif
                bool isNewFrame = cameraData.taaPersistentData.GetAccumulationVersion(multipassId) != Time.frameCount;

                RTHandle taaHistoryAccumulationTex = cameraData.taaPersistentData.GetAccumulationTexture(multipassId);
                taaMaterial.SetTexture(ShaderConstants._TaaAccumulationTex, taaHistoryAccumulationTex);

                // On frame rerender or pause, stop all motion using a black motion texture.
                // This is done to avoid blurring the Taa resolve due to motion and Taa history mismatch.
                //
                // Taa history copy is in sync with motion vectors and Time.frameCount, but we updated the TAA history
                // for the next frame, as we did not know that we're going render this frame again.
                // We would need history double buffering to solve this properly, but at the cost of memory.
                //
                // Frame #1: MotionVectors.Update: #1 Prev: #-1, Taa.Execute: #1 Prev: #-1, Taa.CopyHistory: #1 Prev: #-1
                // Frame #2: MotionVectors.Update: #2 Prev: #1, Taa.Execute: #2 Prev #1, Taa.CopyHistory: #2
                // <pause or render frame #2 again>
                // Frame #2: MotionVectors.Update: #2, Taa.Execute: #2 prev #2   (Ooops! Incorrect history for frame #2!)
                taaMaterial.SetTexture(ShaderConstants._TaaMotionVectorTex, isNewFrame ? motionVectors : Texture2D.blackTexture);

                ref var taa = ref cameraData.taaSettings;
                float taaInfluence = taa.resetHistoryFrames == 0 ? taa.frameInfluence : 1.0f;
                taaMaterial.SetFloat(ShaderConstants._TaaFrameInfluence, taaInfluence);
                taaMaterial.SetFloat(ShaderConstants._TaaVarianceClampScale, taa.varianceClampScale);

                if (taa.quality == TemporalAAQuality.VeryHigh)
                    taaMaterial.SetFloatArray(ShaderConstants._TaaFilterWeights, CalculateFilterWeights(taa.jitterScale));

                switch (taaHistoryAccumulationTex.rt.graphicsFormat)
                {
                    // Avoid precision issues with YCoCg and low bit color formats.
                    case GraphicsFormat.B10G11R11_UFloatPack32:
                    case GraphicsFormat.R8G8B8A8_UNorm:
                    case GraphicsFormat.B8G8R8A8_UNorm:
                        taaMaterial.EnableKeyword(ShaderKeywords.TAA_LOW_PRECISION_SOURCE);
                        break;
                    default:
                        taaMaterial.DisableKeyword(ShaderKeywords.TAA_LOW_PRECISION_SOURCE);
                        break;
                }

                Blitter.BlitCameraTexture(cmd, source, destination, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, taaMaterial, (int)taa.quality);

                if (isNewFrame)
                {
                    int kHistoryCopyPass = taaMaterial.shader.passCount - 1;
                    Blitter.BlitCameraTexture(cmd, destination, taaHistoryAccumulationTex, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, taaMaterial, kHistoryCopyPass);
                    cameraData.taaPersistentData.SetAccumulationVersion(multipassId, Time.frameCount);
                }
            }
        }

        private class TaaPassData
        {
            internal TextureHandle dstTex;
            internal TextureHandle srcColorTex;
            internal TextureHandle srcDepthTex;
            internal TextureHandle srcMotionVectorTex;
            internal TextureHandle srcTaaAccumTex;

            internal Material material;
            internal int passIndex;

            internal float taaFrameInfluence;
            internal float taaVarianceClampScale;
            internal float[] taaFilterWeights;

            internal bool taaLowPrecisionSource;
        }

        internal static void Render(RenderGraph renderGraph, Material taaMaterial, UniversalCameraData cameraData, ref TextureHandle srcColor, ref TextureHandle srcDepth, ref TextureHandle srcMotionVectors, ref TextureHandle dstColor)
        {
            int multipassId = 0;
#if ENABLE_VR && ENABLE_XR_MODULE
            multipassId = cameraData.xr.multipassId;
#endif

            ref var taa = ref cameraData.taaSettings;

            bool isNewFrame = cameraData.taaPersistentData.GetAccumulationVersion(multipassId) != Time.frameCount;
            float taaInfluence = taa.resetHistoryFrames == 0 ? taa.frameInfluence : 1.0f;

            RTHandle accumulationTexture = cameraData.taaPersistentData.GetAccumulationTexture(multipassId);
            TextureHandle srcAccumulation = renderGraph.ImportTexture(accumulationTexture);

            // On frame rerender or pause, stop all motion using a black motion texture.
            // This is done to avoid blurring the Taa resolve due to motion and Taa history mismatch.
            // The TAA history was updated for the next frame, as we did not know yet that we're going render this frame again.
            // We would need to keep the both the current and previous history (double buffering) in order to resolve
            // either this frame (again) or the next frame correctly, but it would cost more memory.
            TextureHandle activeMotionVectors = isNewFrame ? srcMotionVectors : renderGraph.defaultResources.blackTexture;

            using (var builder = renderGraph.AddRasterRenderPass<TaaPassData>("Temporal Anti-aliasing", out var passData, ProfilingSampler.Get(URPProfileId.RG_TAA)))
            {
                passData.dstTex = dstColor;
                builder.SetRenderAttachment(dstColor, 0, AccessFlags.Write);
                passData.srcColorTex = srcColor;
                builder.UseTexture(srcColor, AccessFlags.Read);
                passData.srcDepthTex = srcDepth;
                builder.UseTexture(srcDepth, AccessFlags.Read);
                passData.srcMotionVectorTex = activeMotionVectors;
                builder.UseTexture(activeMotionVectors, AccessFlags.Read);
                passData.srcTaaAccumTex = srcAccumulation;
                builder.UseTexture(srcAccumulation, AccessFlags.Read);

                passData.material = taaMaterial;
                passData.passIndex = (int)taa.quality;

                passData.taaFrameInfluence = taaInfluence;
                passData.taaVarianceClampScale = taa.varianceClampScale;

                if (taa.quality == TemporalAAQuality.VeryHigh)
                    passData.taaFilterWeights = CalculateFilterWeights(taa.jitterScale);
                else
                    passData.taaFilterWeights = null;

                switch (accumulationTexture.rt.graphicsFormat)
                {
                    // Avoid precision issues with YCoCg and low bit color formats.
                    case GraphicsFormat.B10G11R11_UFloatPack32:
                    case GraphicsFormat.R8G8B8A8_UNorm:
                    case GraphicsFormat.B8G8R8A8_UNorm:
                        passData.taaLowPrecisionSource = true;
                        break;
                    default:
                        passData.taaLowPrecisionSource = false;
                        break;
                }

                builder.SetRenderFunc(static (TaaPassData data, RasterGraphContext context) =>
                {
                    data.material.SetFloat(ShaderConstants._TaaFrameInfluence, data.taaFrameInfluence);
                    data.material.SetFloat(ShaderConstants._TaaVarianceClampScale, data.taaVarianceClampScale);
                    data.material.SetTexture(ShaderConstants._TaaAccumulationTex, data.srcTaaAccumTex);
                    data.material.SetTexture(ShaderConstants._TaaMotionVectorTex, data.srcMotionVectorTex);
                    data.material.SetTexture(ShaderConstants._CameraDepthTexture, data.srcDepthTex);
                    CoreUtils.SetKeyword(data.material, ShaderKeywords.TAA_LOW_PRECISION_SOURCE, data.taaLowPrecisionSource);

                    if(data.taaFilterWeights != null)
                        data.material.SetFloatArray(ShaderConstants._TaaFilterWeights, data.taaFilterWeights);

                    Blitter.BlitTexture(context.cmd, data.srcColorTex, Vector2.one, data.material, data.passIndex);
                });
            }

            if (isNewFrame)
            {
                int kHistoryCopyPass = taaMaterial.shader.passCount - 1;
                using (var builder = renderGraph.AddRasterRenderPass<TaaPassData>("Temporal Anti-aliasing Copy History", out var passData, ProfilingSampler.Get(URPProfileId.RG_TAACopyHistory)))
                {
                    passData.dstTex = srcAccumulation;
                    builder.SetRenderAttachment(srcAccumulation, 0, AccessFlags.Write);
                    passData.srcColorTex = dstColor;
                    builder.UseTexture(dstColor, AccessFlags.Read);   // Resolved color is the new history

                    passData.material = taaMaterial;
                    passData.passIndex = kHistoryCopyPass;

                    builder.SetRenderFunc((TaaPassData data, RasterGraphContext context) => { Blitter.BlitTexture(context.cmd, data.srcColorTex, Vector2.one, data.material, data.passIndex); });
                }

                cameraData.taaPersistentData.SetAccumulationVersion(multipassId, Time.frameCount);
            }
        }
    }
}
