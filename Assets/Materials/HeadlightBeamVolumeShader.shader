// Fake volumetric headlight beam for HDRP (VR single-pass instanced OK).
// Draws a cone mesh and ray-marches through the cone in object space, so the
// beam looks like a soft shaft of light from any angle (side, behind, from the cockpit).
// The march stops at the scene depth, so the beam ends where it hits the seabed or a creature.
// Cone shape comes from HeadlightBeams.cs (_ConeR0, _ConeSlope, _ConeLength via MaterialPropertyBlock).
Shader "Custom/HeadlightBeamVolume"
{
    Properties
    {
        [HDR] _Color ("Beam Colour", Color) = (0.85, 0.95, 1.0, 1.0)
        _Intensity ("Intensity (physical, scaled by exposure)", Float) = 500
        _EdgePower ("Edge Softness", Range(0.5, 6.0)) = 1.5
        _LengthPower ("Length Fade Power", Range(0.5, 6.0)) = 1.5
        _ConeR0 ("Start Radius", Float) = 0.18
        _ConeSlope ("Radius Growth per m", Float) = 0.27
        _ConeLength ("Length", Float) = 30
        _NoiseScale ("Water Turbulence Scale (1/m)", Float) = 0.6
        _NoiseStrength ("Water Turbulence Strength", Range(0, 1)) = 0.65
        _NoiseDrift ("Turbulence Drift Speed (m/s)", Float) = 0.15
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
            ZTest Always
            Cull Front  // closed outward-facing cone: draw only where the view ray leaves it, so each pixel marches once

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Intensity;
                float _EdgePower;
                float _LengthPower;
                float _ConeR0;
                float _ConeSlope;
                float _ConeLength;
                float _NoiseScale;
                float _NoiseStrength;
                float _NoiseDrift;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;   // camera-relative
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            // Cheap 3D value noise. Sampled in ABSOLUTE world space, so the murk in the beam stays put in the
            // water while the sub moves through it (that parallax is what stops the beam looking glued to the screen).
            float Hash3(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }
            float ValueNoise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(Hash3(i + float3(0,0,0)), Hash3(i + float3(1,0,0)), f.x),
                                 lerp(Hash3(i + float3(0,1,0)), Hash3(i + float3(1,1,0)), f.x), f.y),
                            lerp(lerp(Hash3(i + float3(0,0,1)), Hash3(i + float3(1,0,1)), f.x),
                                 lerp(Hash3(i + float3(0,1,1)), Hash3(i + float3(1,1,1)), f.x), f.y), f.z);
            }
            float Murk(float3 absWS)
            {
                float3 p = absWS * _NoiseScale + float3(0.0, _Time.y * _NoiseDrift, _Time.y * _NoiseDrift * 0.5);
                float n = ValueNoise(p) * 0.65 + ValueNoise(p * 2.7) * 0.35;
                return lerp(1.0, saturate(n * 1.6 - 0.15), _NoiseStrength);
            }

            float Density(float3 q)
            {
                float z = q.z;
                if (z < 0.0 || z > _ConeLength) return 0.0;
                float R = _ConeR0 + _ConeSlope * z;
                float rad = length(q.xy) / R;
                float radial = pow(saturate(1.0 - rad), _EdgePower);
                float lengthFade = pow(saturate(1.0 - z / _ConeLength), _LengthPower) * smoothstep(0.0, 0.4, z);
                return radial * lengthFade;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // Ray from the camera (at 0 in camera-relative space) to this back-face point
                float distBack = length(IN.positionWS);
                float3 dirWS = IN.positionWS / max(distBack, 1e-4);

                // Stop at the scene (seabed, creatures, the sub itself)
                float deviceDepth = LoadCameraDepth(uint2(IN.positionCS.xy));
                PositionInputs posInput = GetPositionInput(IN.positionCS.xy, _ScreenSize.zw, deviceDepth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
                float distScene = (deviceDepth == UNITY_RAW_FAR_CLIP_VALUE) ? 1e6 : length(posInput.positionWS);
                float tEnd = min(distBack, distScene);
                float tStart = max(0.0, distBack - (_ConeLength + 2.0 * (_ConeR0 + _ConeSlope * _ConeLength)));
                if (tEnd <= tStart) return float4(0, 0, 0, 1);

                float3 camOS = TransformWorldToObject(float3(0, 0, 0));
                float3 endOS = TransformWorldToObject(dirWS * tEnd);
                float3 startOS = lerp(camOS, endOS, tStart / tEnd);

                const int STEPS = 20;
                float3 stepOS = (endOS - startOS) / STEPS;
                float3 startWS = dirWS * tStart;
                float3 stepWS = dirWS * ((tEnd - tStart) / STEPS);
                float stepLen = length(stepOS);
                float jitter = frac(sin(dot(IN.positionCS.xy, float2(12.9898, 78.233))) * 43758.5453);
                float sum = 0.0;
                [unroll]
                for (int i = 0; i < STEPS; i++)
                {
                    float3 q = startOS + stepOS * (i + jitter);
                    float d = Density(q);
                    if (d > 0.0) d *= Murk(GetAbsolutePositionWS(startWS + stepWS * (i + jitter)));
                    sum += d * stepLen;
                }

                float3 col = _Color.rgb * _Intensity * sum * GetCurrentExposureMultiplier();
                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
