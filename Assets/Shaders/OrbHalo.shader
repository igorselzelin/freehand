// Freehand Color Orb - the soft glow around the orb: one additive shell,
// bright at its silhouette (fresnel) and empty in the middle, so the orb
// reads as giving off light without any real transparency sorting or
// post-processing. The one transparent layer the orb has.
Shader "Freehand/OrbHalo"
{
    Properties
    {
        _Color ("Color", Color) = (0.7, 0.85, 1, 1)
        _Intensity ("Intensity", Range(0, 3)) = 1
        _Power ("Rim Power", Range(0.5, 8)) = 2.5
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "OrbHalo"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Intensity;
                float _Power;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewWS = GetWorldSpaceViewDir(positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float facing = saturate(dot(normalize(input.normalWS), normalize(input.viewWS)));
                // Bright near the silhouette, fading out toward both the
                // centre and the outer edge of the shell.
                float rim = pow(1.0 - facing, _Power) * smoothstep(0.0, 0.25, facing);
                return half4(_Color.rgb * rim * _Intensity, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
