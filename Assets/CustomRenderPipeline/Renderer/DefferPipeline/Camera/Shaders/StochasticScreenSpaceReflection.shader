Shader "Hidden/StochasticScreenSpaceReflection" {

	CGINCLUDE
		#include "../Input/SSRPass.cginc"
	ENDCG

	SubShader {
		ZTest Always 
		ZWrite Off
		Cull Front
		
		//0
		Pass 
		{
			Name"Pass_HierarchicalZBuffer_Pass"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment Hierarchical_ZBuffer
			ENDCG
		}

		//1
		Pass 
		{
			Name"Pass_Linear_2DTrace_SingleSampler"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment Linear_2DTrace_SingleSPP
			ENDCG
		} 

		//2
		Pass 
		{
			Name"Pass_Hierarchical_ZTrace_SingleSampler"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment Hierarchical_ZTrace_SingleSPP
				//#pragma fragment TestSSGi_SingleSPP
			ENDCG
		} 

		//3
		Pass 
		{
			Name"Pass_Linear_2DTrace_MultiSampler"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment Linear_2DTrace_MultiSPP
			ENDCG
		} 

		//4
		Pass 
		{
			Name"Pass_Hierarchical_ZTrace_MultiSampler"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment Hierarchical_ZTrace_MultiSPP
				//#pragma fragment TestSSGi_MultiSPP
			ENDCG
		} 

		//5
		Pass 
		{
			Name"Pass_Spatiofilter_SingleSampler"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment Spatiofilter_SingleSPP
			ENDCG
		} 

		//6
		Pass 
		{
			Name"Pass_Spatiofilter_MultiSampler"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment Spatiofilter_MultiSPP
			ENDCG
		} 


		//7
		Pass 
		{
			Name"Pass_Temporalfilter_SingleSampler"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment Temporalfilter_SingleSPP
			ENDCG
		} 

		//8
		Pass 
		{
			Name"Pass_Temporalfilter_MultiSampler"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment Temporalfilter_MultiSPP
			ENDCG
		} 

		//9
		Pass 
		{
			Name"Pass_CombineReflection"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment CombineReflectionColor
			ENDCG
		}

		//10
		Pass 
		{
			Name"Pass_DeBug_SSRColor"
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment DeBug_SSRColor
			ENDCG
		}
		
	}
}
