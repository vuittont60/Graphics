Shader "Hidden/HDRP/VolumetricCloudsCombine"
{
    Properties {}

    SubShader
    {
        HLSLINCLUDE
        #pragma target 4.5
        #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
        //#pragma enable_d3d11_debug_symbols

        #pragma vertex Vert
        #pragma fragment Frag

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/VolumetricLighting/VolumetricCloudsDef.cs.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/SkyUtils.hlsl"

        // For refraction sorting, clouds are considered pre-refraction transparents
        // Atmospheric scattering is computed while tracing
        #define _TRANSPARENT_REFRACTIVE_SORT
        #define _ENABLE_FOG_ON_TRANSPARENT
        #define _BlendMode BLENDINGMODE_ALPHA
        #define ATMOSPHERE_NO_AERIAL_PERSPECTIVE
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/AtmosphericScattering/AtmosphericScattering.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Water/Shaders/UnderWaterUtilities.hlsl"

        TEXTURE2D_X(_CameraColorTexture);
        TEXTURE2D_X(_VolumetricCloudsLightingTexture);
        TEXTURE2D_X(_VolumetricCloudsDepthTexture);
        TEXTURECUBE(_VolumetricCloudsTexture);
        int _Mipmap;

        struct Attributes
        {
            uint vertexID : SV_VertexID;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_Position;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
            return output;
        }
        ENDHLSL

        Tags{ "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            // Pass 0
            Cull   Off
            ZTest  Less // Required for XR occlusion mesh optimization
            ZWrite Off

            // If this is a background pixel, we want the cloud value, otherwise we do not.
            Blend One OneMinusSrcAlpha, Zero One

            HLSLPROGRAM

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Read cloud data
                float4 outColor = LOAD_TEXTURE2D_X(_VolumetricCloudsLightingTexture, input.positionCS.xy);
                outColor.a = 1.0f - outColor.a;

                float deviceDepth = LOAD_TEXTURE2D_X(_VolumetricCloudsDepthTexture, input.positionCS.xy).x;
                float linearDepth = DecodeInfiniteDepth(deviceDepth, _CloudNearPlane);

                float3 V = GetSkyViewDirWS(input.positionCS.xy);
                float3 positionWS = GetCameraPositionWS() - linearDepth * V;

                // Compute pos inputs
                PositionInputs posInput = GetPositionInput(input.positionCS.xy, _ScreenSize.zw, positionWS);
                posInput.linearDepth = linearDepth;

                // Apply fog
                float3 volColor, volOpacity;
                EvaluateAtmosphericScattering(posInput, V, volColor, volOpacity);

                // Composite the result via hardware blending.
                return ApplyFogOnTransparent(outColor, volColor, volOpacity);
            }
            ENDHLSL
        }

        Pass
        {
            // Pass 1
            Cull   Off
            ZWrite Off
            ZTest  Always
            Blend  Off

            HLSLPROGRAM

            float4 Frag(Varyings input) : SV_Target
            {
                // Composite the result via hardware blending.
                float4 clouds = LOAD_TEXTURE2D_X(_VolumetricCloudsLightingTexture, input.positionCS.xy);
                clouds.rgb *= GetInverseCurrentExposureMultiplier();
                float4 color = LOAD_TEXTURE2D_X(_CameraColorTexture, input.positionCS.xy);
                return float4(clouds.xyz + color.xyz * clouds.w, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            // Pass 2
            Cull   Off
            ZWrite Off
            ZTest  Always

            // If this is a background pixel, we want the cloud value, otherwise we do not.
            Blend  One SrcAlpha, Zero One

            HLSLPROGRAM

            float4 Frag(Varyings input) : SV_Target
            {
                // Composite the result via hardware blending.
                float4 clouds = LOAD_TEXTURE2D_X(_VolumetricCloudsLightingTexture, input.positionCS.xy);
                clouds.rgb *= GetInverseCurrentExposureMultiplier();
                return clouds;
            }
            ENDHLSL
        }

        Pass
        {
            // Pass 3
            Cull   Off
            ZWrite Off
            Blend  Off

            HLSLPROGRAM
            float4 Frag(Varyings input) : SV_Target
            {
                return LOAD_TEXTURE2D_X(_VolumetricCloudsLightingTexture, input.positionCS.xy);
            }
            ENDHLSL
        }

        Pass
        {
            // Pass 4
            Cull   Off
            ZWrite Off
            Blend  Off

            HLSLPROGRAM

            float4 Frag(Varyings input) : SV_Target
            {
                // Points towards the camera
                float3 viewDirWS = -GetSkyViewDirWS(input.positionCS.xy * (float)_Mipmap);
                // Fetch the clouds
                return SAMPLE_TEXTURECUBE_LOD(_VolumetricCloudsTexture, s_linear_clamp_sampler, viewDirWS, _Mipmap);
            }
            ENDHLSL
        }

        Pass
        {
            // Pass 5
            Cull   Off
            ZWrite Off
            Blend  Off

            HLSLPROGRAM

            float4 Frag(Varyings input) : SV_Target
            {
                // Construct the view direction
                float3 viewDirWS = -GetSkyViewDirWS(input.positionCS.xy * (float)_Mipmap);
                // Fetch the clouds
                float4 clouds = SAMPLE_TEXTURECUBE_LOD(_VolumetricCloudsTexture, s_linear_clamp_sampler, viewDirWS, _Mipmap);
                // Inverse the exposure
                clouds.rgb *= GetInverseCurrentExposureMultiplier();
                // Read the color value
                float4 color = LOAD_TEXTURE2D_X(_CameraColorTexture, input.positionCS.xy);
                // Combine the clouds
                return float4(clouds.xyz + color.xyz * clouds.w, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            // Pass 6
            Cull   Off
            ZWrite Off
            // If this is a background pixel, we want the cloud value, otherwise we do not.
            Blend  One SrcAlpha, Zero One

            HLSLPROGRAM

            float4 Frag(Varyings input) : SV_Target
            {
                // Construct the view direction
                float3 viewDirWS = -GetSkyViewDirWS(input.positionCS.xy * (float)_Mipmap);
                // Fetch the clouds
                float4 clouds = SAMPLE_TEXTURECUBE_LOD(_VolumetricCloudsTexture, s_linear_clamp_sampler, viewDirWS, _Mipmap);
                // Inverse the exposure
                clouds.rgb *= GetInverseCurrentExposureMultiplier();
                return clouds;
            }
            ENDHLSL
        }

        Pass
        {
            // Pass 7
            // This pass does per pixel sorting with refractive objects
            // Mainly used to correctly sort clouds above water

            Cull   Off
            ZTest  Less // Required for XR occlusion mesh optimization
            ZWrite Off

            // If this is a background pixel, we want the cloud value, otherwise we do not.
            Blend  One OneMinusSrcAlpha, Zero One

            Blend 1 One OneMinusSrcAlpha // before refraction
            Blend 2 One OneMinusSrcAlpha // before refraction alpha

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/VolumetricClouds/VolumetricCloudsUtilities.hlsl"

            void Frag(Varyings input
                , out float4 outColor : SV_Target0
                , out float4 outBeforeRefractionColor : SV_Target1
                , out float4 outBeforeRefractionAlpha : SV_Target2
            )
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Read cloud data
                outColor = LOAD_TEXTURE2D_X(_VolumetricCloudsLightingTexture, input.positionCS.xy);
                outColor.a = 1.0f - outColor.a;

                float deviceDepth = LOAD_TEXTURE2D_X(_VolumetricCloudsDepthTexture, input.positionCS.xy).x;
                float linearDepth = DecodeInfiniteDepth(deviceDepth, _CloudNearPlane);

                float3 V = GetSkyViewDirWS(input.positionCS.xy);
                float3 positionWS = GetCameraPositionWS() - linearDepth * V;

                // Compute pos inputs
                PositionInputs posInput = GetPositionInput(input.positionCS.xy, _ScreenSize.zw, positionWS);
                posInput.linearDepth = linearDepth;
                posInput.deviceDepth = saturate(ConvertCloudDepth(positionWS));

                outColor = ComputeFog(posInput, V, outColor);

                // Sort clouds with refractive objects
                ComputeRefractionSplitColor(posInput, outColor, outBeforeRefractionColor, outBeforeRefractionAlpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
