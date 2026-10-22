Shader "Terrain/GPUTerrain"
{

	Properties
	{
	}
    SubShader
    {

        HLSLINCLUDE
            #include "../../ShaderLibrary/Common.hlsl"
            #include "Input/PlaneInput.hlsl"
            #include "Input/GPUTerrainInclude.hlsl"
        ENDHLSL


        Pass{
			Name "Terrain Draw"

            Blend One Zero
            ZWrite On

            HLSLPROGRAM

                #pragma vertex simpleVert
                #pragma fragment gbufferFrag

                #include "Input/GPUTerrainPass.hlsl"

            ENDHLSL
        }

        Pass{
            Name "Shadow"

            Blend One Zero
            ZWrite On

            HLSLPROGRAM

			    #pragma multi_compile _ LOD_FADE_CROSSFADE
                #pragma vertex shadowVert
                #pragma fragment shadowFrag

                #include "Input/GPUTerrainPass.hlsl"

            ENDHLSL
        }

        Pass{
            Name "Bounds Debug"

            Blend OneMinusSrcALpha SrcAlpha
            ZWrite Off

            HLSLPROGRAM

                #pragma vertex boundsVert
                #pragma fragment boundsFrag

                #include "Input/GPUTerrainPass.hlsl"

            ENDHLSL
        }

        Pass{
			Name "Feedback Draw"

            Blend One Zero
            ZWrite On

            HLSLPROGRAM

                #pragma vertex simpleVert
                #pragma fragment feedbackFrag
			    #pragma multi_compile _ _FEED_BACK_DEBUG

                #include "Input/GPUTerrainPass.hlsl"

            ENDHLSL
        }

        Pass{
			Name "Terrain Draw Editor 0"

            Blend SrcAlpha Zero 
            ZWrite On

            HLSLPROGRAM

			#pragma shader_feature _RENDER_LIGHTS

			#pragma multi_compile _ _DIRECTIONAL_PCF3 _DIRECTIONAL_PCF5 _DIRECTIONAL_PCF7
			#pragma multi_compile _ _OTHER_PCF3 _OTHER_PCF5 _OTHER_PCF7
			#pragma multi_compile _ _CASCADE_BLEND_SOFT _CASCADE_BLEND_DITHER
			#pragma multi_compile _ LOD_FADE_CROSSFADE

                #pragma vertex simpleVert
                #pragma fragment gbufferFragEditor

                #include "Input/GPUTerrainPass.hlsl"

            ENDHLSL
        }

        Pass{
            Name "Terrain Draw Editor 1"

            Blend SrcAlpha One 
            ZWrite On

            HLSLPROGRAM

			#pragma shader_feature _RENDER_LIGHTS

			#pragma multi_compile _ _DIRECTIONAL_PCF3 _DIRECTIONAL_PCF5 _DIRECTIONAL_PCF7
			#pragma multi_compile _ _OTHER_PCF3 _OTHER_PCF5 _OTHER_PCF7
			#pragma multi_compile _ _CASCADE_BLEND_SOFT _CASCADE_BLEND_DITHER
			#pragma multi_compile _ LOD_FADE_CROSSFADE

                #pragma vertex simpleVert
                #pragma fragment gbufferFragEditor

                #include "Input/GPUTerrainPass.hlsl"

            ENDHLSL
        }
    }
}
