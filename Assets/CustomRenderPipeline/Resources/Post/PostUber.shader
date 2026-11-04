// Uber pass：把「镜头畸变 / 色差 / Bloom 合成 / 曝光 / 色调映射 / 调色 / 暗角 / 颗粒」
// 全部塞进【一次全屏】里。这是 URP 的核心设计 —— 全屏往返越少越省带宽。
//
// 处理顺序（和 URP 基本一致，理由写在每一段旁边）：
//   1. 镜头畸变：先扭 UV，后面全部在扭过的 UV 上采样
//   2. 采样（色差：R/B 沿"指向中心"的方向各偏一点）
//   3. + Bloom（仍在 HDR 线性空间）
//   4. × 曝光
//   5. 色调映射 —— 必须在调色【之前】：对比度/饱和度作用在 [0,1] 的显示值上才符合直觉，
//      作用在 HDR 上会把高光压成一片、饱和度直接失真
//   6. 对比度 → 饱和度 → 色彩滤镜（LDR 空间）
//   7. 暗角（LDR）
//   8. 胶片颗粒（最后，模拟"胶片/传感器噪声"）
Shader "Hidden/CustomRP/PostUber"
{

    HLSLINCLUDE
    #include "PostCommon.hlsl"

    TEXTURE2D(_SourceTex);
    SAMPLER(sampler_SourceTex);
    float4 _SourceTex_TexelSize;

    TEXTURE2D(_BloomTex);
    SAMPLER(sampler_BloomTex);

    float  _BloomIntensity;
    float  _Exposure;            // 已由 C# 算成 2^EV
    float  _TonemapMode;         // 0 不映射 / 1 Neutral / 2 ACES（用 float 存，省得纠结 SetInt/SetFloat）

    float  _Contrast;            // 1 + x/100
    float  _Saturation;          // 1 + x/100
    float4 _ColorFilter;

    float4 _VignetteColor;
    float4 _VignetteCenter;      // xy = 中心（默认 0.5,0.5）
    float4 _VignetteParams;      // x intensity, y smoothness, z roundness, w aspect

    float4 _FilmGrainParams;     // x intensity, y response, z time

    float  _ChromaticAberration;

    float4 _LensDistortionParams; // x intensity, y scale

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

    float3 SampleSource(float2 uv)
    {
        return SAMPLE_TEXTURE2D(_SourceTex, sampler_SourceTex, saturate(uv)).rgb;
    }
    ENDHLSL

    SubShader
    {
        Cull Off
        ZTest Always
        ZWrite Off

        Pass
        {
            Name "Uber"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;

                // ---- 1. 镜头畸变 ----
                // f = 1 + k·r²：k>0 向外拉（枕形/广角），k<0 向内收（桶形/鱼眼）。
                // scale 只是把强度换算成更舒服的量级（URP 的 Lens Distortion 也是这个直觉）。
                float lensIntensity = _LensDistortionParams.x;
                if (abs(lensIntensity) > 1e-5)
                {
                    float2 dc = uv - 0.5;
                    float r2 = dot(dc, dc);
                    float k = lensIntensity / max(_LensDistortionParams.y, 1e-4);
                    uv = 0.5 + dc * (1.0 + k * r2);
                    uv = clamp(uv, 0.0, 1.0);   // 防止采样到边界外拖出黑边/拉伸
                }

                // ---- 2. 采样 + 色差 ----
                float3 color;
                if (abs(_ChromaticAberration) > 1e-6)
                {
                    // 沿「像素 → 画面中心」的径向偏移；中心无偏移、边缘最强，
                    // 这正是真实镜头色差的形态（而不是整体平移）。
                    float2 dir = uv - 0.5;
                    float2 off = dir * _ChromaticAberration * 0.01;
                    color.r = SampleSource(uv + off).r;
                    color.g = SampleSource(uv).g;
                    color.b = SampleSource(uv - off).b;
                }
                else
                {
                    color = SampleSource(uv);
                }

                // ---- 3. Bloom（HDR）----
                color += SAMPLE_TEXTURE2D(_BloomTex, sampler_BloomTex, uv).rgb * _BloomIntensity;

                // ---- 4. 曝光 ----
                color *= _Exposure;

                // ---- 5. 色调映射 ----
                color = PostTonemap(color, (int)_TonemapMode);

                // ---- 6. 调色（LDR）----
                if (abs(_Contrast - 1.0) > 1e-5)
                {
                    // 以 0.5 为支点的线性对比度：>1 拉开、<1 压平
                    color = (color - 0.5) * _Contrast + 0.5;
                }
                if (abs(_Saturation - 1.0) > 1e-5)
                {
                    float lum = PostLuminance(saturate(color));
                    color = lerp(lum.xxx, color, _Saturation);
                }
                color *= _ColorFilter.rgb;
                color = saturate(color);

                // ---- 7. 暗角 ----
                // dist: 中心 0 → 四角约 1。roundness 越大越"圆"（按宽高比拉回正圆）。
                float2 vd = abs(uv - _VignetteCenter.xy);
                vd.x *= lerp(_VignetteParams.w, 1.0, _VignetteParams.z);
                float dist = length(vd) * 1.41421356;
                float vMask = smoothstep(1.0 - _VignetteParams.y, 1.0, dist);
                color = lerp(color, _VignetteColor.rgb, saturate(vMask * _VignetteParams.x));

                // ---- 8. 胶片颗粒 ----
                // response 越大，亮部越不吃颗粒（真实胶片高光颗粒少）。用屏幕像素坐标做种子，
                // 否则颗粒会随相机移动而"粘在屏幕上"（其实这就是想要的：颗粒属于胶片，不属于场景）。
                if (_FilmGrainParams.x > 1e-5)
                {
                    float2 pixel = uv * _SourceTex_TexelSize.zw;   // zw = 宽高
                    float seed = _FilmGrainParams.z;
                    float n = PostHash(pixel + seed) - 0.5;
                    float lum = PostLuminance(color);
                    float resp = lerp(1.0, saturate(lum), _FilmGrainParams.y);
                    color += n * _FilmGrainParams.x * resp;
                }

                return float4(saturate(color), 1.0);
            }
            ENDHLSL
        }
    }
}
