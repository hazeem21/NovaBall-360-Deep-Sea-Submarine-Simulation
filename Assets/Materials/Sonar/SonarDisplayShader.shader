// Green scanning-sonar screen (PPI, "plan position indicator") for the cockpit panel. HDRP, VR-safe.
// Everything is drawn procedurally: range rings, bearing spokes, bearing numbers (0..315),
// the rotating sweep with its fading trail, the outgoing ping ring, and the contacts stored in _EchoTex
// (x = bearing 0..360, y = range 0..1):
//   R = ACTIVE echoes (terrain + creatures that sent an echo back after a ping; already faded over time by the script)
//   G = PASSIVE bearing lines (things heard moving; no distance, refreshed by the sweep)
// SonarDisplay.cs writes the texture, the sweep angle and the ping ring.
Shader "Custom/SonarDisplay"
{
    Properties
    {
        _EchoTex ("Echoes (bearing x range)", 2D) = "black" {}
        [HDR] _Phosphor ("Phosphor Colour", Color) = (0.25, 1.0, 0.35, 1)
        _Nits ("Screen Brightness", Float) = 1.2
        _PingRing ("Ping Ring Radius 0-1 (set by script, <0 = none)", Float) = -1
        _Sweep ("Sweep Angle (deg, set by script)", Float) = 0
        _TrailLength ("Sweep Trail Length (0-1 of a turn)", Range(0.02, 1)) = 0.25
        _EchoPersist ("Echo Afterglow (0-1 of a turn)", Range(0.05, 1)) = 0.9
        _Rings ("Range Rings", Float) = 4
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
    #pragma multi_compile_instancing
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

        // Opaque screen: write depth first so the world-space sonar ping does not shine through the panel
        Pass
        {
            Name "DepthForwardOnly"
            Tags { "LightMode" = "DepthForwardOnly" }
            ZWrite On
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            struct AttributesD { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct VaryingsD { float4 positionCS : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            VaryingsD vertDepth(AttributesD IN)
            {
                VaryingsD OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformWorldToHClip(TransformObjectToWorld(IN.positionOS.xyz));
                return OUT;
            }
            float4 fragDepth(VaryingsD IN) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            TEXTURE2D(_EchoTex);
            SAMPLER(sampler_EchoTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _EchoTex_ST;
                float4 _Phosphor;
                float _Nits;
                float _Sweep;
                float _TrailLength;
                float _EchoPersist;
                float _Rings;
                float _PingRing;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformWorldToHClip(TransformObjectToWorld(IN.positionOS.xyz));
                OUT.uv = IN.uv;
                return OUT;
            }

            // 3x5 pixel digits, row 0 = top, bit 14 = row0 col0
            static const int DIGITS[10] = { 31599, 11415, 29671, 29647, 23497, 31183, 31215, 29257, 31727, 31695 };

            float Digit(int d, float2 p)   // p in 0..1 inside a 3x5 cell (y up)
            {
                if (p.x < 0 || p.x >= 1 || p.y < 0 || p.y >= 1) return 0;
                int col = (int)floor(p.x * 3.0);
                int row = 4 - (int)floor(p.y * 5.0);
                int bit = 14 - (row * 3 + col);
                return (float)((DIGITS[d] >> bit) & 1);
            }

            // Draws a whole number (up to 3 digits) centred at c, glyph height h
            float Number(int n, float2 uv, float2 c, float h)
            {
                int count = n >= 100 ? 3 : (n >= 10 ? 2 : 1);
                float w = h * 0.6;          // glyph width
                float gap = h * 0.25;
                float total = count * w + (count - 1) * gap;
                float2 o = c - float2(total * 0.5, h * 0.5);
                float v = 0;
                int div = count == 3 ? 100 : (count == 2 ? 10 : 1);
                for (int i = 0; i < 3; i++)
                {
                    if (i >= count) break;
                    int dg = (n / div) % 10;
                    v = max(v, Digit(dg, (uv - o - float2(i * (w + gap), 0)) / float2(w, h)));
                    div /= 10;
                }
                return v;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                float2 p = IN.uv - 0.5;                    // -0.5..0.5
                const float R = 0.40;                      // radius of the scope
                float r = length(p) / R;                   // 0 centre .. 1 rim
                float ang = degrees(atan2(p.x, p.y));      // 0 = up (ahead), clockwise
                ang = ang < 0 ? ang + 360.0 : ang;
                float aa = fwidth(r) * 1.5;

                float3 col = float3(0, 0, 0);
                float glow = 0;

                // Bearing numbers around the rim (relative bearing: 0 = straight ahead)
                [unroll]
                for (int k = 0; k < 8; k++)
                {
                    float a = radians(k * 45.0);
                    float2 c = 0.5 + float2(sin(a), cos(a)) * (R + 0.055);
                    glow = max(glow, Number(k * 45, IN.uv, c, 0.032) * 0.8);
                }

                if (r <= 1.0)
                {
                    // dark phosphor background with a soft vignette
                    col += _Phosphor.rgb * 0.035 * (1.0 - r * 0.5);

                    // range rings and fine grid
                    float rings = _Rings;
                    float ringF = abs(frac(r * rings + 0.5) - 0.5) / (rings * aa + 1e-5);
                    glow = max(glow, (1.0 - saturate(ringF)) * 0.35);
                    float fine = abs(frac(r * rings * 3.0 + 0.5) - 0.5) / (rings * 3.0 * aa + 1e-5);
                    glow = max(glow, (1.0 - saturate(fine)) * 0.08);
                    // spokes every 30 deg, finer every 10 deg
                    float spokePx = r * R * 6.2831 / 360.0;              // uv per degree at this radius
                    float s30 = abs(frac(ang / 30.0 + 0.5) - 0.5) * 30.0 * spokePx / (fwidth(IN.uv.x) * 1.2);
                    glow = max(glow, (1.0 - saturate(s30)) * 0.25 * step(0.05, r));
                    float s10 = abs(frac(ang / 10.0 + 0.5) - 0.5) * 10.0 * spokePx / (fwidth(IN.uv.x) * 1.0);
                    glow = max(glow, (1.0 - saturate(s10)) * 0.07 * step(0.3, r));

                    // sweep: how far (as part of a turn) this bearing is behind the sweep line
                    float behind = frac((_Sweep - ang) / 360.0);
                    float trail = exp(-behind / max(_TrailLength * 0.35, 1e-3)) * 0.45;
                    float sweepLine = 1.0 - saturate(behind * 360.0 * spokePx / (fwidth(IN.uv.x) * 1.5));
                    glow = max(glow, trail);
                    glow = max(glow, sweepLine * 1.2);

                    float2 e = SAMPLE_TEXTURE2D(_EchoTex, sampler_EchoTex, float2(ang / 360.0, r)).rg;
                    // active echoes (already faded over time by the script)
                    glow = max(glow, e.r * 1.6);
                    // passive bearing lines: bright when the sweep has just listened there, then fade
                    float fade = saturate(1.0 - behind / _EchoPersist);
                    glow = max(glow, e.g * (0.15 + 0.85 * fade * fade));

                    // outgoing ping ring (matches the green wave in the water)
                    if (_PingRing >= 0.0)
                    {
                        float pr = 1.0 - saturate(abs(r - _PingRing) / (aa * 2.0 + 0.006));
                        glow = max(glow, pr * (1.0 - _PingRing * 0.6));
                    }

                    // own sub at the centre
                    float2 q = p / R;
                    float tri = step(abs(q.x) * 2.2 + (q.y - 0.035) * -1.0, 0.04) * step(-0.03, q.y) * step(q.y, 0.04);
                    glow = max(glow, tri);
                }
                // bright outer rim
                float rim = 1.0 - saturate(abs(r - 1.0) / (aa * 2.0 + 0.004));
                glow = max(glow, rim * 1.1);

                col += _Phosphor.rgb * glow;
                // self-lit screen, NOT scaled by exposure, so it reads the same at every depth
                col *= _Nits;
                col += float3(0.004, 0.012, 0.008) * _Nits * step(1.0, r); // dark bezel
                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
