// Visible headlight beam / lamp glow for HDRP (works in VR single-pass instanced).
// HDRP's underwater fog does not show light shafts from spotlights, so the beam is faked with a
// soft additive cone (HeadlightBeams.cs builds the mesh). Brightness follows the camera exposure.
Shader "Custom/HeadlightBeam"
{
    Properties
    {
        [HDR] _Color ("Beam Colour", Color) = (0.85, 0.95, 1.0, 1.0)
        _Intensity ("Intensity (physical, scaled by exposure)", Float) = 400
        _EdgePower ("Edge Softness", Range(0.5, 8.0)) = 2.0
        _UseLength ("Fade Along Length (1 = beam, 0 = glow)", Range(0, 1)) = 1
        _LengthPower ("Length Fade Power", Range(0.5, 6.0)) = 2.0
        _CameraFade ("Fade Near Camera (m)", Float) = 2.0
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
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Intensity;
                float _EdgePower;
                float _UseLength;
                float _LengthPower;
                float _CameraFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 positionWS : TEXCOORD1;   // camera-relative in HDRP
                float2 uv         : TEXCOORD2;
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
                OUT.uv         = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                float3 viewDir = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                // Soft edges: bright where we look "through" the most water, fading at the silhouette
                float ndotv = abs(dot(normalize(IN.normalWS), viewDir));
                float edge = pow(saturate(ndotv), _EdgePower);
                // Looking along the beam (e.g. from the cockpit) every wall is edge-on, so don't fade edges then
                float3 axisWS = normalize(TransformObjectToWorldDir(float3(0, 0, 1)));
                float along01 = abs(dot(axisWS, viewDir));
                edge = lerp(edge, 1.0, along01 * along01 * _UseLength);
                // Fade from the lamp to the end of the beam
                float along = pow(saturate(1.0 - IN.uv.y), _LengthPower) * smoothstep(0.0, 0.03, IN.uv.y);
                float lengthFade = lerp(1.0, along, _UseLength);
                // Don't blind the pilot when the camera is inside the beam
                float camFade = saturate(length(IN.positionWS) / max(_CameraFade, 0.001));
                float3 col = _Color.rgb * _Intensity * edge * lengthFade * camFade * GetCurrentExposureMultiplier();
                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
