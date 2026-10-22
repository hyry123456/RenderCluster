Shader "CustomRP/Untility"
{
    SubShader
    {
        Cull Off
		ZTest Always
		ZWrite Off
		
		HLSLINCLUDE
            #include "../../ShaderLibrary/Common.hlsl"
            #include "../../CommonInput/CommonInput.hlsl"
            #include "Input/UntilityPass.hlsl"
		ENDHLSL

		Pass {	//0
			Name "Blur Horizontal"
			
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment BloomHorizontalPassFragment
			ENDHLSL
		}

		Pass {	//1
			Name "Blur Vertical"
			
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment BloomVerticalPassFragment
			ENDHLSL
		}

		Pass{	//2
			Name "Write Deep"
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment WriteDeepth
			ENDHLSL
		}

    }
}
