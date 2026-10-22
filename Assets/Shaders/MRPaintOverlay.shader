// Freehand MR - paint laid over the real wall seen through passthrough, and the
// faint frame that marks a recognised wall.
//
// The texture is PREMULTIPLIED (colour already scaled by its opacity - see
// PaintSurface.UploadPremultiplied), so the blend is One / OneMinusSrcAlpha:
// bare texels add nothing, a coat of paint covers the passthrough behind it by
// exactly its opacity, and the colour and opacity left in the eye buffer are
// what the Quest compositor (premultiplied-alpha blend with the passthrough
// layer) expects. Unlit: a real wall is lit by the real room, not by our lamps.
// Tested against depth (the can, the hands and the tools draw over it), never
// written to it.
Shader "Freehand/MRPaintOverlay"
{
    Properties
    {
        [MainTexture] _BaseMap ("Paint (premultiplied)", 2D) = "white" {}
        [MainColor] _BaseColor ("Tint (premultiplied)", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "MRPaintOverlay"
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
