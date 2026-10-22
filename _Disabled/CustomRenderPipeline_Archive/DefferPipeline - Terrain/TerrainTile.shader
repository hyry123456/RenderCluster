Shader "Unlit/TerrainTile"
{
    SubShader
    {

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha 
            ZTest Off

            HLSLPROGRAM
                #include "../../ShaderLibrary/Common.hlsl"
                float4x4 _ImageMVP;
                TEXTURE2D(_AlbedoMap0);
                TEXTURE2D(_AlbedoMap1);
                TEXTURE2D(_AlbedoMap2);
                TEXTURE2D(_AlbedoMap3);
                TEXTURE2D(_NormalMap0);
                TEXTURE2D(_NormalMap1);
                TEXTURE2D(_NormalMap2);
                TEXTURE2D(_NormalMap3);
                TEXTURE2D(_MaskMap0);
                TEXTURE2D(_MaskMap1);
                TEXTURE2D(_MaskMap2);
                TEXTURE2D(_MaskMap3);

                float4 _ScaleOffset0;
                float4 _ScaleOffset1;
                float4 _ScaleOffset2;
                float4 _ScaleOffset3;
                float3 _PBRData0;       //metallic, smoothness, normalScale
                float3 _PBRData1;       //metallic, smoothness, normalScale
                float3 _PBRData2;       //metallic, smoothness, normalScale
                float3 _PBRData3;       //metallic, smoothness, normalScale

                float4 _Color0;
                float4 _Color1;
                float4 _Color2;
                float4 _Color3;
                // TEXTURE2D(_EmissionMap);
                // TEXTURE2D(_DetailMap);


                TEXTURE2D(_BlendMap);

                float _PaddingSize;

                float _RowId, _ColumnId;
                int _AlphaMapIndex;
                //tile的大小，即1/Column, 1/row
                float2 _TileScale;
                float3 _TerrainSize;
                #pragma target 4.0
                #pragma vertex vert
                #pragma fragment frag
                
                struct appdata_img {
                    float4 vertex : POSITION;
                    float2 texcoord : TEXCOORD0;
                };

                struct v2f_drawTex
                {
                    float4 pos : SV_POSITION;
                    float2 uv : TEXCOORD0;
                };

                v2f_drawTex vert(appdata_img v)
                {
                    v2f_drawTex o;
                    o.pos = mul(_ImageMVP, v.vertex);
                    o.uv = v.texcoord;

                    return o;
                }

                struct RVTTarget{
                    float4 _VisualAlbedo   : SV_Target0;
                    float4 _VisualNormal   : SV_Target1;
                    float4 _VisualPBRData  : SV_Target2;
                    float4 _VisualEmission : SV_Target3;
                    float4 _VirtualDetail : SV_Target4;	
                };

                void frag(v2f_drawTex i, out RVTTarget o)
                {
                    //01uv,包含padding了
                    i.uv = saturate((i.uv - float2(_PaddingSize, _PaddingSize)) / (1.0 - 2 * _PaddingSize));
                    float2 terrainUV = _TileScale * float2(_ColumnId, _RowId) + _TileScale * i.uv;
                    // float2 terrainUV; 
                    // terrainUV.x = _TileScale.x * _ColumnId;
                    // terrainUV.y = _TileScale.y * _RowId;
                    float4 blend = SAMPLE_TEXTURE2D(_BlendMap, sampler_linear_repeat, terrainUV);

                    // float2 worldUV = i.uv * _TerrainSize.xz * _TileScale;
                    float2 tileWorldSize = _TerrainSize.xz * _TileScale;
                    float2 tileScale0 = tileWorldSize / _ScaleOffset0.xy + _ScaleOffset0.zw,
                           tileScale1 = tileWorldSize / _ScaleOffset1.xy + _ScaleOffset1.zw,
                           tileScale2 = tileWorldSize / _ScaleOffset2.xy + _ScaleOffset2.zw,
                           tileScale3 = tileWorldSize / _ScaleOffset3.xy + _ScaleOffset3.zw;

                    // float2 worldUV = i.uv * tileWorldSize + float2(_ColumnId, _RowId) * tileWorldSize;
                    float2 uv0 = i.uv * tileScale0 + float2(_ColumnId, _RowId) * tileScale0,
                           uv1 = i.uv * tileScale1 + float2(_ColumnId, _RowId) * tileScale1,
                           uv2 = i.uv * tileScale2 + float2(_ColumnId, _RowId) * tileScale2,
                           uv3 = i.uv * tileScale3 + float2(_ColumnId, _RowId) * tileScale3;

                    float4 albedo = SAMPLE_TEXTURE2D(_AlbedoMap0, sampler_linear_repeat, uv0) * blend.x * _Color0;
                    albedo += SAMPLE_TEXTURE2D(_AlbedoMap1, sampler_linear_repeat, uv1) * blend.y * _Color1;
                    albedo += SAMPLE_TEXTURE2D(_AlbedoMap2, sampler_linear_repeat, uv2) * blend.z * _Color2;
                    albedo += SAMPLE_TEXTURE2D(_AlbedoMap3, sampler_linear_repeat, uv3) * blend.w * _Color3;

                    float3 normal = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap0, sampler_linear_repeat, uv0), _PBRData0.z) * blend.x;
                    normal += UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap1, sampler_linear_repeat, uv1), _PBRData1.z) * blend.y;
                    normal += UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap2, sampler_linear_repeat, uv2), _PBRData2.z) * blend.z;
                    normal += UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap3, sampler_linear_repeat, uv3), _PBRData3.z) * blend.w;
                    // float3 normal = SAMPLE_TEXTURE2D(_NormalMap0, sampler_linear_repeat, uv0) * blend.x;
                    // normal += SAMPLE_TEXTURE2D(_NormalMap1, sampler_linear_repeat, uv1) * blend.y;
                    // normal += SAMPLE_TEXTURE2D(_NormalMap2, sampler_linear_repeat, uv2) * blend.z;
                    // normal += SAMPLE_TEXTURE2D(_NormalMap3, sampler_linear_repeat, uv3) * blend.w;

                    normal = normal * 0.5f + 0.5;

                    float3 m_r_a = 0;
                    float4 mask = SAMPLE_TEXTURE2D(_MaskMap0, sampler_linear_repeat, uv0) * blend.x;
                    m_r_a.x += mask.x * _PBRData0.x; m_r_a.y += mask.w * _PBRData0.y; m_r_a.z += mask.y;

                    mask = SAMPLE_TEXTURE2D(_MaskMap1, sampler_linear_repeat, uv1) * blend.y;
                    m_r_a.x += mask.x * _PBRData1.x; m_r_a.y += mask.w * _PBRData1.y; m_r_a.z += mask.y;

                    mask = SAMPLE_TEXTURE2D(_MaskMap2, sampler_linear_repeat, uv2) * blend.z;
                    m_r_a.x += mask.x * _PBRData2.x; m_r_a.y += mask.w * _PBRData2.y; m_r_a.z += mask.y;

                    mask = SAMPLE_TEXTURE2D(_MaskMap3, sampler_linear_repeat, uv3) * blend.w;
                    m_r_a.x += mask.x * _PBRData3.x; m_r_a.y += mask.w * _PBRData3.y; m_r_a.z += mask.y;


                    float alpha = blend.x + blend.y + blend.z + blend.w;
                    if(_AlphaMapIndex == 0){
                        o._VisualAlbedo   = float4(albedo.xyz, 1);
                        o._VisualNormal   = float4(normal.xyz, 1);
                        o._VisualPBRData   = float4(m_r_a.xyz, 1);
                        o._VisualEmission   = 1;
                        o._VirtualDetail   = 1;
                    }
                    else{
                        o._VisualAlbedo   = float4(albedo.xyz, alpha);
                        o._VisualNormal   = float4(normal, alpha);
                        o._VisualPBRData   = float4(m_r_a, alpha);
                        o._VisualEmission   = 1;
                        o._VirtualDetail   = 1;
                    }
                        // o._VisualAlbedo   = float4(terrainUV.xy, 0, 1);
                        // o._VisualAlbedo   = float4(frac(worldUV), 0, 1);
                        // o._VisualAlbedo.yz   = frac(uv0.xy);
                }

            ENDHLSL
        }
    }
}
