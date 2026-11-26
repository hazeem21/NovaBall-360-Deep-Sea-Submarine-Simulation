// Particles that flash green when the sonar wave passes them. This makes the ping visible in OPEN WATER
// (where there is no seabed for it to hit): you see a sphere of sparkles sweeping outward from the sub,
// with real parallax, instead of a flat tint on the screen. Same global values as FullScreen/SonarScan.
Shader "Custom/SonarSpeck"
{
    Properties
    {
        _Size ("Glow Softness", Range(0.5, 4)) = 1.5
        _Boost ("Brightness Boost", Float) = 4
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

            float4 _SonarPingOrigin;
            float4 _SonarPingParams;
            float4 _SonarPingColor;

            CBUFFER_START(UnityPerMaterial)
                float _Size;
                float _Boost;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float glow : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                float3 rws = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(rws);
                OUT.uv = IN.uv;
                float glow = 0;
                if (_SonarPingOrigin.w > 0.5)
                {
                    float d = distance(GetAbsolutePositionWS(rws), _SonarPingOrigin.xyz);
                    float behind = _SonarPingParams.x - d;
                    float w = _SonarPingParams.z * 1.5;
                    float front = exp(-(behind * behind) / (w * w));
                    float energy = 1.0 - saturate(_SonarPingParams.x / _SonarPingParams.y);
                    glow = front * (0.3 + 0.7 * energy) * smoothstep(3.0, 8.0, d);
                }
                OUT.glow = glow;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                float r = length(IN.uv - 0.5) * 2.0;
                float dotShape = pow(saturate(1.0 - r), _Size);
                float3 col = _SonarPingColor.rgb * _SonarPingColor.a * _Boost * IN.glow * dotShape;
                return float4(col, 0);
            }
            ENDHLSL
        }
    }
}
