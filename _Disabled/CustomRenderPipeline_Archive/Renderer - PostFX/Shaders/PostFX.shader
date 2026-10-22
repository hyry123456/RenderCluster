Shader "CustomRP/PostFX"
{
    SubShader
    {
        Cull Off
		ZTest Always
		ZWrite Off
		
		HLSLINCLUDE
            #include "../../ShaderLibrary/Common.hlsl"
            #include "../Input/PostFXPass.hlsl"
		ENDHLSL

        Pass {	//0
			Name "Bloom Add"
			
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment BloomAddPassFragment
			ENDHLSL
		}
		
		Pass {	//1
			Name "Blur Horizontal"
			
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment BloomHorizontalPassFragment
			ENDHLSL
		}

		Pass {	//2
			Name "Blur Vertical"
			
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment BloomVerticalPassFragment
			ENDHLSL
		}

		Pass {	//3
			Name "Bloom Prefilter"
			
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment BloomPrefilterPassFragment
			ENDHLSL
		}
		
		Pass {	//4
			Name "Bloom Prefilter Fireflies"
			
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment BloomPrefilterFirefliesPassFragment
			ENDHLSL
		}
		
		Pass {	//5
			Name "Bloom Scatter"
			
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment BloomScatterPassFragment
			ENDHLSL
		}
		
		Pass {	//6
			Name "Bloom Scatter Final"
			
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment BloomScatterFinalPassFragment
			ENDHLSL
		}
		


		Pass {	//7
			Name "Color Grading None"
			
			HLSLPROGRAM


				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment ColorGradingNonePassFragment
			ENDHLSL
		}

		Pass {	//8
			Name "Color Grading ACES"
			
			HLSLPROGRAM
				#pragma multi_compile _ _AUTO_EXPOSURE
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment ColorGradingACESPassFragment
			ENDHLSL
		}

		Pass {	//9
			Name "Color Grading Neutral"
			
			HLSLPROGRAM
				#pragma multi_compile _ _AUTO_EXPOSURE
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment ColorGradingNeutralPassFragment
			ENDHLSL
		}
		
		Pass {	//10
			Name "Color Grading Reinhard"
			
			HLSLPROGRAM
				#pragma multi_compile _ _AUTO_EXPOSURE
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment ColorGradingReinhardPassFragment
			ENDHLSL
		}

		Pass {	//11
			Name "Final"
			
			HLSLPROGRAM
				#pragma target 3.5
				#pragma vertex DefaultPassVertex
				#pragma fragment FinalPassFragment
			ENDHLSL
		}

		Pass	//12
		{
			Name "CaculateGray"
            HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment CaculateGray

            ENDHLSL
		}

		Pass	//13
		{
			Name "FXAA"
            HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment FXAAFragment

			#pragma multi_compile _ LUMINANCE_GREEN
			#pragma multi_compile _ LOW_QUALITY

            ENDHLSL

		}

		Pass	//14
        {
			Name "Blend Bulk"

            HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment BlendBulkLightFragment

            ENDHLSL
        }


		Pass{	//15
			Name "Circle Of Confusion"
			HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment CircleOfConfusionFragment
            ENDHLSL
		}

		Pass{	//16
			Name "PreFilter"
			HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment PreFilterFragment
            ENDHLSL
		}

		Pass{	//17
			Name "Bokeh"
			HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment BokehFragment
            ENDHLSL
		}

		Pass{	//18
			Name "Post Filter"
			HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment PostFilterFragment
            ENDHLSL
		}

		Pass{	//19
			Name "Combine"
			HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment CombineFragment
            ENDHLSL
		}

		Pass{	//20
			Name "Dymaic Blur"
			HLSLPROGRAM
            #pragma vertex BlitPassVertex
            #pragma fragment DynamicBlurFragment
            ENDHLSL
		}


		Pass{	//21
			Name "Auto Exprosure"
			HLSLPROGRAM
            #pragma vertex DefaultPassVertex
            #pragma fragment AutoExprosure
            ENDHLSL
		}
    }
}
