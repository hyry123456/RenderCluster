// 簇实例化绘制：一次间接绘制把「同一 detail 贴图组」的所有可见簇画进 GBuffer。
//
// 用法（C# ClusterGBufferRenderer）：
//   cmd.DrawProceduralIndirect(Matrix4x4.identity, mat, pass, MeshTopology.Triangles, argsBuf, offset, mpb)
//   每组的 visible 段基址 / 世界→裁剪矩阵 / detail 两张贴图都经 MaterialPropertyBlock 逐 draw 传。
//
// 与 Unlit/ClusterPBR 的关系：片元代码【完全共用】（ClusterInput.hlsl + ClusterPass.hlsl 的
// ClusterGBufferShade / FeedbackColor），差别只有
//   ① 材质参数来自每簇常量缓冲（INPUT_PROP 被重定向，见 ClusterGBufferInput.hlsl）；
//   ② 顶点来自 StructuredBuffer（SV_VertexID 还原），不绑顶点缓冲。
Shader "Cluster/ClusterGBuffer"
{
    Properties
    {
        // detail 是唯一还绑在图上的「每材质」资源，按组逐 draw 由 MPB 绑。
        _DetailMap("Details", 2D) = "linearGrey" {}
        _DetailNormalMap("Detail Normals", 2D) = "bump" {}
        // RVT 图集声明成材质属性是为了让采样器沿用纹理自己的 anisoLevel
        //（纯全局纹理 + 默认采样器 aniso 恒为 1，掠射角会明显糊）。由 ClusterObject 回填。
        [HideInInspector] _VirtualAlbedo("RVT Albedo", 2D) = "white" {}
        [HideInInspector] _VirtualNormal("RVT Normal", 2D) = "white" {}
        [HideInInspector] _VirtualMask("RVT Mask", 2D) = "white" {}
    }
    SubShader
    {
        // 两个 pass 共用同一套库；ClusterGBufferPass.hlsl 里再决定用哪部分。
        HLSLINCLUDE
            #include "../../ShaderLibrary/Common.hlsl"
            #include "../../ShaderLibrary/GI.hlsl"
            #include "../../ShaderLibrary/Surface.hlsl"
            #include "../../ShaderLibrary/Shadows.hlsl"
            #include "../../ShaderLibrary/Light.hlsl"
            #include "../../ShaderLibrary/BRDF.hlsl"
            #include "../../ShaderLibrary/Lighting.hlsl"
            #include "../DeffererLibrary/DeffererInput.hlsl"
        ENDHLSL

        // ---------- pass 0：GBuffer ----------
        Pass
        {
            Name "ClusterGBuffer"
            Tags { "Queue" = "Geometry" "LightMode" = "ClusterGBuffer" }

            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ClusterGBufferVertex
            #pragma fragment ClusterGBufferFragment

            #include "Input/ClusterGBufferPass.hlsl"
            ENDHLSL
        }

        // ---------- pass 1：RVT Feedback ----------
        Pass
        {
            Name "ClusterFeedback"
            Tags { "Queue" = "Geometry" "LightMode" = "ClusterFeedback" }

            // 反馈目标是纯色彩 RT（没有深度附件），必须关掉深度测试/写入，
            // 否则依赖未绑定的深度缓冲，行为不确定。
            ZWrite Off
            ZTest Always
            Blend One Zero

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ClusterFeedbackVertex
            #pragma fragment ClusterFeedbackFragment
            #pragma multi_compile _ _FEED_BACK_DEBUG

            #include "Input/ClusterGBufferPass.hlsl"
            ENDHLSL
        }
    }
}
