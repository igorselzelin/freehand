// Freehand Color Orb - the orb's surfaces, unlit and opaque (cheap on
// Quest: no transparency, no lighting, a handful of ALU per pixel).
//
//   _Mode 0  the orb core: every point of the sphere shows the color the
//            orb gives there - hue around the axis that faces the player,
//            saturation from the centre (white) to the rim (pure) - at
//            brightness _Value. ColorOrb picks with the same mapping, so
//            what the player points at is the color they get. On top: a
//            luminous core, slow pigment flow, a fresnel rim, a fake
//            highlight, the pointer's glint and a confirm pulse.
//   _Mode 1  a solid bead (presets, cursor, value knob); _Stripes draws the
//            cleaner's hazard stripes.
//   _Mode 2  the value arc: brightness of the current hue along uv.x.
//
// _Dissolve (0-1) eats the surface away through noise, for summon/dismiss.
Shader "Freehand/ColorOrb"
{
    Properties
    {
        _Mode ("Mode (0 orb, 1 solid, 2 value arc)", Float) = 0
        _Value ("Value", Range(0, 1)) = 1
        _SolidColor ("Solid Color", Color) = (1, 1, 1, 1)
        _Stripes ("Stripes", Float) = 0
        _ArcHue ("Arc Hue", Range(0, 1)) = 0
        _ArcSaturation ("Arc Saturation", Range(0, 1)) = 1
        _Glow ("Glow", Range(0, 2)) = 0
        _Pulse ("Pulse", Range(0, 1)) = 0
        _PulseColor ("Pulse Color", Color) = (1, 1, 1, 1)
        _HoverDir ("Hover Direction (object space)", Vector) = (0, 0, 1, 0)
        _Hover ("Hover", Range(0, 1)) = 0
        _Dissolve ("Dissolve", Range(0, 1)) = 0
        _Flow ("Flow Speed", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ColorOrb"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Mode;
                float _Value;
                half4 _SolidColor;
                float _Stripes;
                float _ArcHue;
                float _ArcSaturation;
                float _Glow;
                float _Pulse;
                half4 _PulseColor;
                float4 _HoverDir;
                float _Hover;
                float _Dissolve;
                float _Flow;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 viewWS : TEXCOORD2;
                float2 uv : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionOS = input.positionOS.xyz;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewWS = GetWorldSpaceViewDir(positionWS);
                output.uv = input.uv;
                return output;
            }

            // Same as Unity's Color.HSVToRGB.
            float3 HsvToRgb(float h, float s, float v)
            {
                float3 k = saturate(abs(frac(h + float3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0);
                return v * lerp(float3(1.0, 1.0, 1.0), k, s);
            }

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float ValueNoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(lerp(Hash(i), Hash(i + float3(1, 0, 0)), f.x), lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), f.x), f.y),
                    lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), f.x), lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), f.x), f.y),
                    f.z);
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 d = normalize(input.positionOS);
                float3 n = normalize(input.normalWS);
                float3 v = normalize(input.viewWS);
                float fresnel = pow(1.0 - saturate(dot(n, v)), 3.0);
                float t = _Time.y * _Flow;

                // Dissolve: the surface forms out of (or breaks into) noise,
                // with a bright seam at the edge.
                float grain = ValueNoise(d * 9.0 + t * 0.3);
                clip(grain - _Dissolve * 1.05 + 0.02);
                float seam = saturate(1.0 - (grain - _Dissolve * 1.05) * 12.0) * step(0.001, _Dissolve);

                float3 rgb;
                if (_Mode < 0.5)
                {
                    float hue = frac(atan2(d.y, d.x) / 6.2831853 + 1.0);
                    float sat = saturate(length(d.xy));
                    rgb = HsvToRgb(hue, sat, _Value);

                    // Luminous core: the white heart of the wheel glows.
                    float core = pow(1.0 - sat, 3.0);
                    rgb += core * (0.25 + 0.35 * _Value);

                    // Pigment flow: slow bands of light moving through it.
                    float flow = ValueNoise(d * 3.5 + float3(t * 0.35, -t * 0.22, t * 0.17));
                    rgb *= 0.9 + flow * 0.22;

                    // Rim light, pulled toward white, and a fixed highlight
                    // that sells the glassy surface.
                    rgb += fresnel * (0.35 + 0.6 * _Glow) * lerp(rgb, float3(1, 1, 1), 0.55);
                    float3 lightDir = normalize(float3(-0.45, 0.6, 0.65));
                    rgb += pow(saturate(dot(d, lightDir)), 70.0) * 0.8;

                    // The pointer's glint: a soft ring where it aims.
                    float aim = distance(d, normalize(_HoverDir.xyz));
                    float glint = saturate(1.0 - abs(aim - 0.09) * 30.0) * _Hover;
                    rgb = lerp(rgb, float3(1, 1, 1), glint * 0.85);
                }
                else if (_Mode < 1.5)
                {
                    rgb = _SolidColor.rgb;
                    if (_Stripes > 0.5)
                    {
                        float stripe = step(0.5, frac((d.x + d.y) * 3.0 + t * 0.4));
                        rgb = lerp(rgb, float3(0.95, 0.97, 1.0), stripe * 0.75);
                    }

                    rgb *= 0.85 + 0.25 * saturate(dot(d, normalize(float3(-0.4, 0.6, 0.7))));
                    rgb += fresnel * (0.3 + 0.7 * _Glow) * lerp(rgb, float3(1, 1, 1), 0.6);
                }
                else
                {
                    rgb = HsvToRgb(_ArcHue, _ArcSaturation, input.uv.x);
                    rgb += fresnel * 0.25;
                }

                rgb = lerp(rgb, _PulseColor.rgb * 1.6 + 0.2, _Pulse * 0.55);
                rgb += seam * float3(0.9, 0.95, 1.0);
                return half4(rgb, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
