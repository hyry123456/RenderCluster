Shader "Terrain/GPUTerrain"
{

	Properties
	{
	}
    SubShader
    {

        HLSLINCLUDE
            #include "../../../ShaderLibrary/Common.hlsl"
            #include "../Input/PlaneInput.hlsl"
            #include "../Input/GPUTerrainInclude.hlsl"
        ENDHLSL
        Pass{
            Name "GPU Terrain GBuffer"

            Tags {
                "LightMode" = "OutGBuffer"
            }
            Blend One Zero
            ZWrite On


            HLSLPROGRAM

                #pragma vertex vert
                #pragma fragment frag
                #pragma shader_feature _DEBUG

                #include "../Input/GPUTerrainPass.hlsl"

            ENDHLSL
        }


        // Pass{
        //     Name "Bounds Debug"

        //     Blend One Zero
        //     ZWrite On

        //     HLSLPROGRAM

        //         #pragma vertex boundsVert
        //         #pragma fragment boundsFrag

        //         #include "../Input/GPUTerrainPass.hlsl"

        //     ENDHLSL
        // }

        Pass{
			Tags { "LightMode" = "Feedback" }

            Blend One Zero
            ZWrite On

            HLSLPROGRAM

                #pragma vertex simpleVert
                #pragma fragment feedbackFrag

                #include "../Input/GPUTerrainPass.hlsl"

            ENDHLSL
        }

    }
}
