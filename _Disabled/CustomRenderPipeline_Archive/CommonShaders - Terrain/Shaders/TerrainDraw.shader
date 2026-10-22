Shader "Terrain/TerrainEditorDraw"
{
    Properties
    {
    }
    SubShader
    {
		HLSLINCLUDE
            #include "../../../ShaderLibrary/Common.hlsl"
            #include "../Input/TerrainDrawInput.hlsl"
		ENDHLSL

        Pass
        {
            Name "Terrain Begin"
			Blend One Zero
			ZWrite On
			HLSLPROGRAM

            #pragma target 4.6

            #pragma vertex tessvert
            #pragma hull hull_Distance
            #pragma domain domain
            #pragma require geometry
            #pragma geometry TerrainGeom
            #pragma fragment TerrainFragment


            #include "../Input/TerrainDrawPass.hlsl"


            ENDHLSL
        }


        Pass
        {
            Name "Terrain Blend"
			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off
			HLSLPROGRAM

            #pragma target 4.6

            #pragma vertex tessvert
            #pragma hull hull_Distance
            #pragma domain domain
            #pragma require geometry
            #pragma geometry TerrainGeom
            #pragma fragment TerrainFragment


            #include "../Input/TerrainDrawPass.hlsl"


            ENDHLSL
        }

		// Pass {
		// 	Tags {
		// 		"LightMode" = "ShadowCaster"
		// 	}

		// 	ColorMask 0
			
		// 	HLSLPROGRAM
		// 	#pragma target 3.5
		// 	#pragma shader_feature _ _SHADOWS_CLIP _SHADOWS_DITHER
		// 	#pragma multi_compile _ LOD_FADE_CROSSFADE
		// 	#pragma vertex tessVert
        //     #pragma require tessellation
        //     #pragma hull hull
        //     #pragma domain domain
        //     #pragma fragment TerrainFragment_Shadow
        //     #pragma require geometry
        //     #pragma geometry TerrainGeom_Shadow

		// 	#include "HLSL/TerrainShadowPass.hlsl"
		// 	ENDHLSL
		// }

    }
}
