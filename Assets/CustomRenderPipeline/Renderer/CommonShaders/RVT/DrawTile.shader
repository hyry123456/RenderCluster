Shader "Unlit/DrawTile"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {

        HLSLINCLUDE
            #include "../../ShaderLibrary/Common.hlsl"
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

            float4x4 _TileMVP;

            TEXTURE2D(_Blend);
            TEXTURE2D(_Diffuse0);
            TEXTURE2D(_Normal0);
            TEXTURE2D(_Mask0);
            float4 _Data0;      //r:metallic, g:roughness
            float4 _Color0;      //Albedo Color

            TEXTURE2D(_Diffuse1);
            TEXTURE2D(_Normal1);
            TEXTURE2D(_Mask1);
            float4 _Data1;      //r:metallic, g:roughness
            float4 _Color1;      //Albedo Color

            TEXTURE2D(_Diffuse2);
            TEXTURE2D(_Normal2);
            TEXTURE2D(_Mask2);
            float4 _Data2;      //r:metallic, g:roughness
            float4 _Color2;      //Albedo Color

            TEXTURE2D(_Diffuse3);
            TEXTURE2D(_Normal3);
            TEXTURE2D(_Mask3);
            float4 _Data3;      //r:metallic, g:roughness
            float4 _Color3;      //Albedo Color

            float4 _TileOffset0;
            float4 _TileOffset1;
            float4 _TileOffset2;
            float4 _TileOffset3;
            float4 _BlendData;

			Varyings vert(Attributes IN)
			{
				Varyings OUT ;
				float4x4 mat = UNITY_MATRIX_M;
                mat = _TileMVP;
				float2 pos = saturate(mul(mat, IN.positionOS).xy);
				pos.y = 1 - pos.y;
				OUT.positionHCS = float4(2.0 * pos  - 1,0.5,1);
                OUT.uv = IN.uv;
				return OUT;
			}

            void frag (Varyings i, 
                out float4 Albedo : SV_Target0,
                out float4 Normal : SV_Target1,
                out float4 PBR_Data : SV_Target2     //r:metallic, g:roughness, b:ao
            )
            {

                float2 blendUV = i.uv * _BlendData.xy + _BlendData.zw;
                float4 blend = SAMPLE_TEXTURE2D_LOD(_Blend, sampler_linear_repeat, blendUV, 0);

                float2 tileUV = i.uv * _TileOffset0.xy + _TileOffset0.zw;
                float4 col1 = SAMPLE_TEXTURE2D(_Diffuse0, sampler_linear_repeat, tileUV) * _Color0;
                float3 normal1 = DecodeNormal(SAMPLE_TEXTURE2D(_Normal0, sampler_linear_repeat, tileUV), _Data0.z);
                float4 mask1 = SAMPLE_TEXTURE2D(_Mask0, sampler_linear_repeat, tileUV);

                tileUV = i.uv * _TileOffset1.xy + _TileOffset1.zw;
                float4 col2 = SAMPLE_TEXTURE2D(_Diffuse1, sampler_linear_repeat, tileUV) * _Color1;
                float3 normal2 = DecodeNormal(SAMPLE_TEXTURE2D(_Normal1, sampler_linear_repeat, tileUV), _Data1.z);
                float4 mask2 = SAMPLE_TEXTURE2D(_Mask1, sampler_linear_repeat, tileUV);

                tileUV = i.uv * _TileOffset2.xy + _TileOffset2.zw;
                float4 col3 = SAMPLE_TEXTURE2D(_Diffuse2, sampler_linear_repeat, tileUV) * _Color2;
                float3 normal3 = DecodeNormal(SAMPLE_TEXTURE2D(_Normal2, sampler_linear_repeat, tileUV), _Data2.z);
                float4 mask3 = SAMPLE_TEXTURE2D(_Mask2, sampler_linear_repeat, tileUV);

                tileUV = i.uv * _TileOffset3.xy + _TileOffset3.zw;
                float4 col4 = SAMPLE_TEXTURE2D(_Diffuse3, sampler_linear_repeat, tileUV) * _Color3;
                float3 normal4 = DecodeNormal(SAMPLE_TEXTURE2D(_Normal3, sampler_linear_repeat, tileUV), _Data3.z);
                float4 mask4 = SAMPLE_TEXTURE2D(_Mask3, sampler_linear_repeat, tileUV);


                float4 col = blend.r * col1 + blend.g * col2 + blend.b * col3 + blend.a * col4;
                float3 normal = blend.r * normal1 + blend.g * normal2 + blend.b * normal3 + blend.a * normal4;

                float metallic = blend.r * _Data0.x * mask1.r + blend.g * _Data1.x * mask2.r +
                    blend.b * _Data2.x * mask3.r + blend.a * _Data3.x * mask4.r;
                
                float roughness = blend.r * _Data0.y * mask1.g + blend.g * _Data1.y * mask2.g +
                    blend.b * _Data2.y * mask3.g + blend.a * _Data3.y * mask4.g;

                // float2 metallicAndRoughness = blend.r * _Data0.xy * mask1.rg + blend.g * _Data1.xy + mask2.rg +
                //     blend.b * _Data2.xy * mask3.rg + blend.a * _Data3.xy * mask4.rg;

                float ao = blend.r * mask1.a + blend.g * mask2.a + blend.b * mask2.a + blend.a * mask2.a;

                Albedo = col;
                Normal = float4(normal, col.w);
                // PBR_Data = float4(metallicAndRoughness, ao, col.w);
                // PBR_Data = float4(metallic, roughness, ao, col.w);
                PBR_Data = float4(metallic, roughness, ao, col.w);
                
                // return col;
            }

        ENDHLSL
		ZTest Off
		Cull Front

        Pass
        {
            // Blend SrcAlpha OneMinusSrcAlpha
            Blend One Zero
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag


            ENDHLSL
        }

        Pass
        {
            // Blend SrcAlpha One
            Blend One One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }

    }
}
