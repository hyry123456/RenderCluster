Shader "CustomRP/Defferer/DefferSSR"
{
    SubShader
	{
		HLSLINCLUDE
			#include "../../../ShaderLibrary/Common.hlsl"
			// #include "../../../CommonInput/CommonInput.hlsl"
			#include "../Input/DefferSSRPass.hlsl"
		ENDHLSL
		//0
		Pass{
			Name "RayCast"
			ZWrite Off
			ZTest Off
			Cull Off

			HLSLPROGRAM
            #pragma multi_compile _ _ACCURACY_NORMAL
			#pragma multi_compile _ _SSR_BACK_CLIP
            #pragma vertex DefaultPassVertex
            #pragma fragment rayCast
            ENDHLSL
		}

		//1
		Pass{
			Name "Resovle"
			ZWrite Off
			ZTest Off
			Cull Off

			HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment resolve
            ENDHLSL
		}

		//2
		Pass{
			Name "Temporal"
			ZWrite Off
			ZTest Off
			Cull Off

			HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment temporal
            ENDHLSL
		}

		//3
		Pass{
			Name "CombineSSR"
			ZWrite Off
			ZTest Off
			Cull Off

			HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment combine
            ENDHLSL
		}

    }
}
