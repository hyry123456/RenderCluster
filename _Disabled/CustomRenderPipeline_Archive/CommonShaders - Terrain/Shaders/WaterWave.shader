Shader "Unlit/WaterWave"
{
    SubShader
    {

        Pass
        {
            //将上一个Wave的数据通过坐标转化，投影到下一个Wave的位置
            Name "Transfer Wave"
            HLSLINCLUDE
            #include "../../ShaderLibrary/Common.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            TEXTURE2D(_PreWave);
            float3 _PreWavePos;     //上一个贴图的中心点
            float3 _CurWavePos;     //当前要映射到的中心点
            float _WaveSize;        //上一个贴图映射到的世界坐标大小

			struct Attributes
			{
				float4 positionOS   : POSITION;
				float2 uv           : TEXCOORD0;

			};

			struct Varyings
			{
                float2 uv              : TEXCOORD1;
				float4 positionHCS	   : SV_POSITION;
			};

            Varyings vert(Attributes IN)
			{
				Varyings o ;
                o.positionHCS = TransformWorldToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
				return OUT;
			}

            float frag(Varyings i) : SV_TARGET{
                float2 curPos = _CurWavePos.xz + (i.uv - float2(0.5))* _WaveSize;   //这个像素的世界位置
                float2 preUv = (curPos - _PreWavePos.xz) / _WaveSize + float2(0.5);
                float color = SAMPLE_TEXTURE2D(_PreWave, sampler_linear_clamp, preUv).r;
                return color;
            }


            ENDHLSL
        }
    }
}
