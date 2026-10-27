Shader "Unlit/RVTShader"
{

    SubShader
    {


        //Down Mips 0
        Pass{
            Blend One Zero
            ZTest Off
            ZWrite Off
            HLSLPROGRAM
            #include "../ShaderLibrary/Common.hlsl"
            #include "ClusterLibrary/ClusterData.hlsl"
            #include "ClusterLibrary/ClusterInclude.hlsl"
            #pragma target 4.0
            #pragma vertex BlitPassSimpleVertex
            #pragma fragment frag


            
            float4 frag (Varyings i) : SV_Target
            {
				return GetMaxFeedback(i.screenUV, 2);
            }
            ENDHLSL
        }

        //Down Mips 1
        Pass{
            Blend One Zero
            ZTest Off
            ZWrite Off
            HLSLPROGRAM
            #include "../ShaderLibrary/Common.hlsl"
            #include "ClusterLibrary/ClusterData.hlsl"
            #include "ClusterLibrary/ClusterInclude.hlsl"
            #pragma target 4.0
            #pragma vertex BlitPassSimpleVertex
            #pragma fragment frag


            
            float4 frag (Varyings i) : SV_Target
            {
				return GetMaxFeedback(i.screenUV, 4);
                // float4 col = tex2D(_MainTex, i.screenUV);
                // return col;
            }
            ENDHLSL
        }

        //Down Mips 2
        Pass{
            Blend One Zero
            ZTest Off
            ZWrite Off
            HLSLPROGRAM
            #include "../ShaderLibrary/Common.hlsl"
            #include "ClusterLibrary/ClusterData.hlsl"
            #include "ClusterLibrary/ClusterInclude.hlsl"
            #pragma target 4.0
            #pragma vertex BlitPassSimpleVertex
            #pragma fragment frag


            
            float4 frag (Varyings i) : SV_Target
            {
				return GetMaxFeedback(i.screenUV, 8);
                // float4 col = tex2D(_MainTex, i.screenUV);
                // return col;
            }
            ENDHLSL
        }
        //3
        Pass{

            HLSLPROGRAM
                #include "../ShaderLibrary/Common.hlsl"
                float4x4 _ImageMVP;
                // sampler2D _MainTex;
                // 全部加 _RVT*Src 前缀：这些属性是给 MaterialPropertyBlock 喂的，
                // 用 Unity 已知名（_NormalMap/_EmissionMap/_DetailMap 等）会被静默跳过，
                // 采样落到默认白图且不报错——曾因 _NormalMap 踩过，法线整片失效。
                TEXTURE2D(_RVTAlbedoSrc);
                TEXTURE2D(_RVTNormalSrc);
                TEXTURE2D(_RVTPBRDataSrc);
                TEXTURE2D(_RVTEmissionSrc);

                float _PaddingSize;
                // Tile 粒度虚拟贴图：把源贴图的采样范围从“整张”缩小到“某个 tile 的子矩形”。
                // _TileOrigin = (tileX, tileY) / T，_TileScale = (1/T, 1/T)；
                // T=1 时 origin=0、scale=1，退化为采样整张源贴图（旧路径）。
                float4 _TileOrigin;
                float4 _TileScale;
                // float4 _MainTex_TexelSize;
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

                // 3 个渲染目标，通道打包见 TileTable.cs 的布局说明。
                struct RVTTarget{
                    float4 _VisualAlbedo : SV_Target0;   // rgb=albedo, a=emission.r
                    float4 _VisualNormal : SV_Target1;   // rg=normal.xy, b=detailMask, a=roughness
                    float4 _VisualMask   : SV_Target2;   // r=metallic, g=AO, b=emission.g, a=emission.b
                };

                void frag(v2f_drawTex i, out RVTTarget o)
                {
                    //quad 的 uv 覆盖整个 cell（含 padding 区）。分两步做映射：
                    //  1) 先按 padding 把 cell 的 [pad, 1-pad] 内区拉伸回 [0,1] ——
                    //     区间外的部分交给 sampler_linear_clamp 采样，天然把边缘像素外扩进 padding，
                    //     这是 padding 真正起作用的机制（防相邻 cell 双线性串色）。
                    //  2) 再把 [0,1] 映射到本 tile 在源贴图里的子矩形：origin + uv * (1/T)。
                    //【注意】原写法是 (_TileOrigin + uv*_TileScale - pad)/(1-2pad)：把 -pad 混在
                    //「源 UV 空间」里做、再整体除 (1-2pad)，只有 T=1 时才等价（origin=0）。
                    //T>1 时每个 tile 的内容会整体偏移/缩放 ~padding 那么大（mip0 约 8 texel），
                    //相邻 tile 拼不回原图 —— 正是"开了 Tile 之后法线错乱/接缝"的来源。
                    float2 cellUV = (i.uv - float2(_PaddingSize, _PaddingSize)) / (1.0 - 2.0 * _PaddingSize);
                    i.uv = _TileOrigin.xy + cellUV * _TileScale.xy;

                    float4 albedoSrc = SAMPLE_TEXTURE2D(_RVTAlbedoSrc, sampler_linear_clamp, i.uv);
                    float4 normalSrc = SAMPLE_TEXTURE2D(_RVTNormalSrc, sampler_linear_clamp, i.uv);
                    float4 maskSrc   = SAMPLE_TEXTURE2D(_RVTPBRDataSrc, sampler_linear_clamp, i.uv);
                    float4 emiSrc    = SAMPLE_TEXTURE2D(_RVTEmissionSrc, sampler_linear_clamp, i.uv);

                    // 源法线可能是 RG 或 AG(DXT5nm) 编码 -> 先解成方向，再按 xy*0.5+0.5 存两通道。
                    // 读取侧只做 RG 解码 + z 重建；绝不能再对图集样本调 UnpackNormalmapRGorAG（会二次解码）。
                    float3 n = DecodeNormal(normalSrc, 1.0);
                    float2 nxy = n.xy * 0.5 + 0.5;

                    o._VisualAlbedo = float4(albedoSrc.rgb, emiSrc.r);
                    o._VisualNormal = float4(nxy, maskSrc.b, maskSrc.a);
                    o._VisualMask   = float4(maskSrc.r, maskSrc.g, emiSrc.g, emiSrc.b);
                }

            ENDHLSL
        }
    }
}
