Shader "CustomRP/Defferer/DefferCamera"
{

    SubShader
    {

        Cull Off
		ZTest Always
		ZWrite Off
        HLSLINCLUDE
            #include "../../../ShaderLibrary/Common.hlsl"
            #include "../../DeffererLibrary/DeffererInput.hlsl"
            #include "../Input/DefferCameraInput.hlsl"
            #include "../Input/DefferCameraPass.hlsl"
		ENDHLSL

        //0
        Pass
        {
			Name "Copy Color"
            HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment CopyFragment
            ENDHLSL
        }

        //1
        Pass
        {
            ZWrite On
    		ZTest Off

			Name "Copy Depth"
            HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment CopyDepthPassFragment
            ENDHLSL
        }

        //2
        Pass
        {
			Name "Blit Depth"
            HLSLPROGRAM
            #pragma vertex BlitPassSimpleVertex
            #pragma fragment CopyDepthPassFragment
            ENDHLSL
        }

		//3
		Pass
        {
			Name "GBuffer Draw"

            HLSLPROGRAM
            #pragma vertex BlitPassRayVertex
            #pragma fragment DrawGBufferColorFragment
			#pragma multi_compile _ _USE_CLUSTER
            #pragma multi_compile _ _ACCURACY_NORMAL
			#pragma multi_compile _ _DIRECTIONAL_PCF3 _DIRECTIONAL_PCF5 _DIRECTIONAL_PCF7
			#pragma multi_compile _ _OTHER_PCF3 _OTHER_PCF5 _OTHER_PCF7
			#pragma multi_compile _ _CASCADE_BLEND_SOFT _CASCADE_BLEND_DITHER
			#pragma multi_compile _ _MY_LIGHTING


            ENDHLSL
        }

		//4
		Pass
        {
			Name "Add Reflect"
            Blend One One

            HLSLPROGRAM
            #pragma vertex BlitPassRayVertex
            #pragma fragment AddReflectFragment
            ENDHLSL
        }

		//5
        Pass{
            Name "Debug Normal"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment DebugNormal
            ENDHLSL
        }

		//6
        Pass{
            Name "Debug Albedo"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment DebugAlbedo
            ENDHLSL
        }

		//7
        Pass{
            Name "Debug PBR"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment DebugPBR
            ENDHLSL
        }

    }
}
