// Sonar ping you can SEE travelling through the world: a green shell expanding from the submarine.
// Runs as an HDRP full-screen Custom Pass (injection: Before Post Process), so it works in VR too.
// Wherever the expanding shell touches terrain or creatures it lights them up green, and leaves a short
// fading "echo" behind. Driven by global shader values set by SonarScanEffect.cs.
Shader "FullScreen/SonarScan"
{
    HLSLINCLUDE

    #pragma vertex Vert
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    float4 _SonarPingOrigin;     // xyz = absolute world position of the ping, w = 1 while a ping is alive
    float4 _SonarPingParams;     // x = current radius (m), y = max range (m), z = front width (m), w = echo trail (m)
    float4 _SonarPingColor;      // rgb = colour, a = brightness (screen units, same at every depth)

    float4 FullScreenPass(Varyings varyings) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        if (_SonarPingOrigin.w < 0.5) return float4(0, 0, 0, 0);

        float depth = LoadCameraDepth(varyings.positionCS.xy);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE) return float4(0, 0, 0, 0);   // open water: nothing to echo
        PositionInputs posInput = GetPositionInput(varyings.positionCS.xy, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
        float3 absWS = GetAbsolutePositionWS(posInput.positionWS);

        float d = distance(absWS, _SonarPingOrigin.xyz);
        float radius = _SonarPingParams.x;
        float range = _SonarPingParams.y;
        if (d > range) return float4(0, 0, 0, 0);

        float width = _SonarPingParams.z;
        float behind = radius - d;                                   // >0: the front already passed this point
        float front = exp(-(behind * behind) / (width * width));     // bright leading edge
        float echo = behind > 0 ? exp(-behind / _SonarPingParams.w) * 0.35 : 0;
        // faint contour lines every 5 m inside the echo, to read the shape of the seabed
        float contour = behind > 0 ? (1.0 - saturate(abs(frac(d / 5.0) - 0.5) * 2.0 / 0.08)) * echo * 0.8 : 0;
        float nearFade = smoothstep(4.0, 10.0, d);                   // don't light up our own hull
        float energy = 1.0 - saturate(radius / range);               // the ping weakens as it spreads
        energy *= energy;

        float a = saturate((front + echo + contour) * (0.25 + 0.75 * energy)) * nearFade;
        // NOT scaled by exposure: the ping should read the same in bright shallows and the black abyss
        float3 col = _SonarPingColor.rgb * _SonarPingColor.a * a;
        return float4(col, 0);
    }

    ENDHLSL

    SubShader
    {
        Tags{ "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "Sonar Scan"
            ZWrite Off
            ZTest Always
            Blend One One
            Cull Off

            HLSLPROGRAM
                #pragma fragment FullScreenPass
            ENDHLSL
        }
    }
}
