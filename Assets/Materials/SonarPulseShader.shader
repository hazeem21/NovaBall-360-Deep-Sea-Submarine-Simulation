// HDRP version of the sonar pulse (was "Custom/URPSonarPulse").
// Same properties as before (_PulseColor, _RimPower, _Alpha) so SubmarineLightAndSonar.cs still works.
// Additive fresnel rim: bright at the edges of the expanding sphere, clear in the middle.
Shader "Custom/SonarPulse"
{
    Properties
    {
        [HDR] _PulseColor ("Sonar Color", Color) = (0.0, 1.0, 0.65, 1.0)
        _RimPower ("Edge Sharpness", Range(1.0, 10.0)) = 4.0
        _Alpha ("Opacity", Range(0.0, 1.0)) = 0.6
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
    #pragma multi_compile_instancing
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend SrcAlpha One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _PulseColor;
                float _RimPower;
                float _Alpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 positionWS : TEXCOORD1;   // camera-relative in HDRP
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                float3 viewDir = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                float ndotv = abs(dot(normalize(IN.normalWS), viewDir));
                float fresnel = pow(saturate(1.0 - ndotv), _RimPower);
                // HDRP works in physical light units; scale by current exposure so the rim stays visible
                float3 col = _PulseColor.rgb * GetCurrentExposureMultiplier();
                return float4(col, fresnel * _Alpha);
            }
            ENDHLSL
        }
    }
}
