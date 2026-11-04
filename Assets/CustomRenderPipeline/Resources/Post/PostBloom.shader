// Bloom：soft-knee 阈值 → 5~6 级降采样（13 tap）→ 逐级升采样（9 tap tent，加法混合）
// 结构照 URP 的 Bloom.shader：只有它需要多 pass，其余效果全塞进 PostUber 里。
Shader "Hidden/CustomRP/PostBloom"
{

    HLSLINCLUDE
    #include "PostCommon.hlsl"

    TEXTURE2D(_SourceTex);
    SAMPLER(sampler_SourceTex);
    float4 _SourceTex_TexelSize;

    TEXTURE2D(_BloomTex);
    SAMPLER(sampler_BloomTex);
    float4 _BloomTex_TexelSize;

    float _Threshold;
    float _SoftKnee;
    float _Clamp;
    float4 _BloomTint;

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

    // URP 的 soft-knee 阈值：threshold 以下全掐掉，threshold..threshold+knee 之间软过渡，
    // 避免"亮部一块块突然开始发光"的硬边。
    float3 ApplyBloomThreshold(float3 color)
    {
        float brightness = PostMax3(color);
        float soft = clamp(brightness - _Threshold + _SoftKnee, 0.0, 2.0 * _SoftKnee);
        soft = (soft * soft) / (4.0 * _SoftKnee + 1e-4);
        float multiplier = max(brightness - _Threshold, soft) / max(brightness, 1e-4);
        return color * multiplier;
    }

    float3 SampleSource(float2 uv) { return SAMPLE_TEXTURE2D(_SourceTex, sampler_SourceTex, uv).rgb; }
    float3 SampleBloom(float2 uv) { return SAMPLE_TEXTURE2D(_BloomTex, sampler_BloomTex, uv).rgb; }

    // CODQ 式 13 tap 降采样：中心 4 个对角 tap 权重 0.125，外围 4 组各 0.03125
    float3 Downsample13(float2 uv, float2 texel)
    {
        float3 A = SampleBloom(uv + texel * float2(-2.0,  2.0));
        float3 B = SampleBloom(uv + texel * float2( 0.0,  2.0));
        float3 C = SampleBloom(uv + texel * float2( 2.0,  2.0));
        float3 D = SampleBloom(uv + texel * float2(-2.0,  0.0));
        float3 E = SampleBloom(uv);
        float3 F = SampleBloom(uv + texel * float2( 2.0,  0.0));
        float3 G = SampleBloom(uv + texel * float2(-2.0, -2.0));
        float3 H = SampleBloom(uv + texel * float2( 0.0, -2.0));
        float3 I = SampleBloom(uv + texel * float2( 2.0, -2.0));
        float3 J = SampleBloom(uv + texel * float2(-1.0,  1.0));
        float3 K = SampleBloom(uv + texel * float2( 1.0,  1.0));
        float3 L = SampleBloom(uv + texel * float2(-1.0, -1.0));
        float3 M = SampleBloom(uv + texel * float2( 1.0, -1.0));

        float3 result = (J + K + L + M) * 0.125;
        result += (A + B + D + E) * 0.03125;
        result += (B + C + E + F) * 0.03125;
        result += (D + E + G + H) * 0.03125;
        result += (E + F + H + I) * 0.03125;
        return result;
    }

    // 9 tap tent 升采样（权重和为 1）
    float3 Upsample9(float2 uv, float2 texel)
    {
        float3 result = 0;
        result += SampleBloom(uv + texel * float2(-1.0,  1.0)) * 0.0625;
        result += SampleBloom(uv + texel * float2( 0.0,  1.0)) * 0.1250;
        result += SampleBloom(uv + texel * float2( 1.0,  1.0)) * 0.0625;
        result += SampleBloom(uv + texel * float2(-1.0,  0.0)) * 0.1250;
        result += SampleBloom(uv)                             * 0.2500;
        result += SampleBloom(uv + texel * float2( 1.0,  0.0)) * 0.1250;
        result += SampleBloom(uv + texel * float2(-1.0, -1.0)) * 0.0625;
        result += SampleBloom(uv + texel * float2( 0.0, -1.0)) * 0.1250;
        result += SampleBloom(uv + texel * float2( 1.0, -1.0)) * 0.0625;
        return result;
    }
    ENDHLSL

    SubShader
    {
        Cull Off
        ZTest Always
        ZWrite Off

        // 0: 预过滤 —— 全分辨率 → 半分辨率，取 4 个双线性 tap 后各自过阈值再平均。
        //    「先过阈值再平均」能保住孤立的亮点（先平均会把它摊平掉，高光就不闪了）。
        Pass
        {
            Name "Prefilter"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 t = _SourceTex_TexelSize.xy * 0.5;

                float3 c = ApplyBloomThreshold(SampleSource(uv + float2(-t.x, -t.y)));
                c += ApplyBloomThreshold(SampleSource(uv + float2(t.x, -t.y)));
                c += ApplyBloomThreshold(SampleSource(uv + float2(-t.x, t.y)));
                c += ApplyBloomThreshold(SampleSource(uv + float2(t.x, t.y)));
                c *= 0.25;

                // 掐掉极端 firefly（单个像素比整屏亮几个数量级时会让 bloom 抖）
                c = min(c, _Clamp);
                return float4(c * _BloomTint.rgb, 1.0);
            }
            ENDHLSL
        }

        // 1: 降采样 —— 半分辨率逐级 /2
        Pass
        {
            Name "Downsample"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 t = _BloomTex_TexelSize.xy * 0.5;
                return float4(Downsample13(input.uv, t), 1.0);
            }
            ENDHLSL
        }

        // 2: 升采样 —— 从粗级往细级叠，加法混合进目标 mip（目标里已有本级的降采样结果）
        //    _BloomScatter < 1 时越粗的级衰减越多 ⇒ 光晕更"紧"；接近 1 则更铺开。
        Pass
        {
            Name "Upsample"
            Blend One One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            float _BloomScatter;

            float4 Frag(Varyings input) : SV_Target
            {
                float2 t = _BloomTex_TexelSize.xy * 0.5;
                float3 c = Upsample9(input.uv, t);
                return float4(c * _BloomScatter, 1.0);
            }
            ENDHLSL
        }
    }
}
