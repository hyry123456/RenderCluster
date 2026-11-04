// FXAA 3.11（console/quality 那一支的经典实现）
// 必须放在【色调映射之后】的 LDR 图上做：在 HDR 上算亮度对比会把高光边缘全判成"边缘"，
// 结果整片高光被糊掉。
Shader "Hidden/CustomRP/PostFXAA"
{

    HLSLINCLUDE
    #include "PostCommon.hlsl"

    TEXTURE2D(_SourceTex);
    SAMPLER(sampler_SourceTex);
    float4 _SourceTex_TexelSize;

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    Varyings Vert(uint vertexID : SV_VertexID)
    {
        Varyings o;
        o.positionCS = PostFullscreenVertexPosition(vertexID);
        o.uv = PostFullscreenTexCoord(vertexID);
        return o;
    }

    float3 Fetch(float2 uv)
    {
        return SAMPLE_TEXTURE2D(_SourceTex, sampler_SourceTex, saturate(uv)).rgb;
    }

    // FXAA 常用亮度权重：绿色主导 + 蓝色少量，比纯 Rec.709 更贴近人眼对边缘的敏感度
    float Luma(float3 c) { return c.g * (0.587 / 0.299) + c.r; }

    #define FXAA_SPAN_MAX   8.0
    #define FXAA_REDUCE_MUL (1.0 / 8.0)
    #define FXAA_REDUCE_MIN (1.0 / 128.0)
    ENDHLSL

    SubShader
    {
        Cull Off
        ZTest Always
        ZWrite Off

        Pass
        {
            Name "FXAA"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 texel = _SourceTex_TexelSize.xy;

                float3 rgbNW = Fetch(uv + float2(-1.0, -1.0) * texel);
                float3 rgbNE = Fetch(uv + float2( 1.0, -1.0) * texel);
                float3 rgbSW = Fetch(uv + float2(-1.0,  1.0) * texel);
                float3 rgbSE = Fetch(uv + float2( 1.0,  1.0) * texel);
                float3 rgbM  = Fetch(uv);

                float lumaNW = Luma(rgbNW);
                float lumaNE = Luma(rgbNE);
                float lumaSW = Luma(rgbSW);
                float lumaSE = Luma(rgbSE);
                float lumaM  = Luma(rgbM);

                float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
                float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

                // 对比度太低的区域不需要处理直接返回（大量天空/纯色区域省下 4 次采样）
                if (lumaMax - lumaMin < max(0.0312, lumaMax * 0.125)) return float4(rgbM, 1.0);

                // 用 4 个角的亮度梯度估边缘方向
                float2 dir;
                dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
                dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

                float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * FXAA_REDUCE_MUL),
                                      FXAA_REDUCE_MIN);
                float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
                dir = min(float2(FXAA_SPAN_MAX, FXAA_SPAN_MAX),
                          max(float2(-FXAA_SPAN_MAX, -FXAA_SPAN_MAX), dir * rcpDirMin)) * texel;

                // 沿边方向做 2 次 + 2 次采样，取平均值抹平锯齿
                float3 rgbA = 0.5 * (Fetch(uv + dir * (1.0 / 3.0 - 0.5)) +
                                     Fetch(uv + dir * (2.0 / 3.0 - 0.5)));
                float3 rgbB = rgbA * 0.5 + 0.25 * (Fetch(uv + dir * -0.5) +
                                                   Fetch(uv + dir *  0.5));

                // 结果 B 落回局部亮度区间就用它（更平滑），否则说明采到别的物体上了，退回 A
                float lumaB = Luma(rgbB);
                return float4((lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB, 1.0);
            }
            ENDHLSL
        }
    }
}
