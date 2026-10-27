// 【真正的 cluster 管线】用于绘制阴影的簇着色器（只写深度，不采任何贴图）。
//
// ── 矩阵来源 ──
// ⚠️ 不要在这里自己推导"世界空间 → 阴影裁剪空间"的矩阵！
//
// worldToClip 由 C#（Shadows.cs）逐级联算好，经 MaterialPropertyBlock 传入本 shader 的
// 自定义属性 _ClusterShadowW2C。其计算为：
//     gpuProj = GL.GetGPUProjectionMatrix(proj, true)   // true = 渲染进 RenderTexture
//     worldToClip = gpuProj * view
// 其中 proj/view 来自 ComputeDirectionalShadowMatricesAndCullingPrimitives。
//
// ★ 为什么必须套 GetGPUProjectionMatrix（2026-10-26 定案的关键修复）：
// 自定义 HLSLPROGRAM 在 DrawProceduralIndirect 下不走 SetViewProjectionMatrices，而后者内部会
// 对投影矩阵套 GL.GetGPUProjectionMatrix，把 OpenGL 风格 [-1,1] 的 z 换成平台期望的 [0,1]/reversed-Z
// 深度约定。裸 proj*view 在 D3D 上让 NDC z 大量落在 [0,1] 之外被裁光 ⇒ 整张阴影图全空（零报错）。
// 采样侧 ConvertToAtlasMatrix(proj*view) 已用 usesReversedZBuffer 做等价 z 取反，与这里两侧约定一致。
//
// ── 关键坑（2026-10-25 定案）──
// 自定义 HLSLPROGRAM 在 DrawProceduralIndirect 下，worldPose 参数【不会】可靠地喂进内建矩阵
// UNITY_MATRIX_M（见 Unity 官方论坛 "World transform URP + DrawProcedural"：UNITY_MATRIX_M 在
// 这类 draw 里保持单位/零矩阵 ⇒ 几何以世界坐标当裁剪坐标，全被裁光 ⇒ 整张阴影图空、零报错）。
// 因此本 shader【绝不使用 UNITY_MATRIX_M / UNITY_MATRIX_VP 等内建矩阵】，而是用自定义属性
// _ClusterShadowW2C：C# 每级联经 MaterialPropertyBlock.SetMatrix 逐 draw 显式传入 worldToClip。
// MaterialPropertyBlock 是命令级 per-draw 数据，互不污染，也不依赖内建矩阵绑定行为，100% 确定生效。
// （绝不能走 drawMat.SetMatrix / SetGlobalMatrix：共享 Material / 全局属性在命令【执行】时才读，
//  4 个级联会全拿到最后一次设置的值 ⇒ 阴影错乱。）
//
// 所以本 shader 只管 mul(_ClusterShadowW2C, worldPos)，矩阵正确性与一致性由 C# 侧保证。
//
//
// ── 数据布局（C# 侧 ClusterShadowRenderer.Rebuild 摊平）──
//   _Positions —— float3 世界空间顶点（世界坐标，烘焙时乘好）
//   索引缓冲由 compute（BuildIndices）每帧只给【可见】簇铺好，索引值就是全局顶点号：
//     pos = _Positions[SV_VertexID]
//   一次顺序读取、无任何查表。GPU 的顶点缓存因此能真正复用（一个 128 三角形的簇
//   唯一顶点只有约 66 个，此前用静态单位索引缓冲会白跑 384 次 VS，约 5.8 倍）。
Shader "Hidden/ClusterShadowCaster"
{
    SubShader
    {
        Pass
        {
            Name "ClusterShadow"
            ColorMask 0
            ZWrite On
            ZTest LEqual
            // ⚠️ 必须与 SRP 路径的 ShadowCaster 口径一致：ClusterPBR.shader 的 ShadowCaster
            // pass 没写 Cull 指令 ⇒ 默认 Cull Back。这里若写 Cull Off，等于把每个投射物的
            // 三角形再画一遍背面（光栅化 ×2），而阴影图里背面根本不参与最终深度。
            // 2026-10-27 实测：这是 Cluster 阴影比 SRP 慢一截的主要原因之一。
            // 只有场景里出现「必须双面投影」的单面片（旗帜/布/卡片植被）时才需要改回 Cull Off。
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            // 只为拿 UNITY_REVERSED_Z / UNITY_NEAR_CLIP_VALUE 宏（决定 pankaking 的 clamp 方向）。
            // 直接引 core 的平台 API 头（D3D11/Metal/Vulkan/GL*/GLES 各一个，自带 #if 链），
            // 不引工程的 ShaderLibrary/Common.hlsl —— 那个会带进 UnityInput/BRG 元数据，
            // 本 shader 只需要几何。
            #if SHADER_API_D3D11
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/D3D11.hlsl"
            #elif SHADER_API_METAL
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/Metal.hlsl"
            #elif SHADER_API_VULKAN
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/Vulkan.hlsl"
            #elif SHADER_API_GLCORE
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/GLCore.hlsl"
            #elif SHADER_API_GLES3
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/GLES3.hlsl"
            #elif SHADER_API_GLES
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/GLES2.hlsl"
            #endif

            // 引入 Unity 内建变量声明（UNITY_MATRIX_VP / unity_ObjectToWorld 等）。
            // 裸 HLSLPROGRAM 不会自动声明/绑定这些矩阵（历史曾因用旧名 unity_MatrixVP 拿到零矩阵），
            // 必须显式 include，UNITY_MATRIX_VP 才能被 SetViewProjectionMatrices 正确喂进来。
            #include "UnityShaderVariables.cginc"

            // 每簇三角形数（= indirect args indexCount 的基准）。改这个值必须同步改
            // C# 侧 ClusterShadowRenderer.TrisPerCluster，否则索引区间错位。
            #define TRIS_PER_CLUSTER 128

            StructuredBuffer<float3> _Positions;   // 世界空间顶点（烘焙时乘好世界矩阵）
            // 自定义 world→clip 矩阵（= proj*view），由 C# 经 MaterialPropertyBlock 逐 draw 显式传入。
            // ⚠️ 不用内建 UNITY_MATRIX_M/V：自定义 HLSLPROGRAM 在 DrawProceduralIndirect 下
            // 内建矩阵不被 worldPose 可靠喂入（会恒为单位/零矩阵 ⇒ 全裁光）。见 shader 顶部注释。
            float4x4 _ClusterShadowW2C;

            struct Attributes
            {
                // 索引绘制下 SV_VertexID = 索引值 = 全局顶点号（baseVertex 恒 0）
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
            };

            Varyings vert(Attributes i)
            {
                // 索引值就是全局顶点号 ⇒ 只有一次顺序读取，没有 _Visible / _Indices 的间接查表。
                float3 p = _Positions[i.vertexID];

                Varyings o;
                o.pos = mul(_ClusterShadowW2C, float4(p, 1.0));

                // Pankaking：把近平面外的几何拉到近平面上，否则它们会被近平面裁掉
                // ⇒ 那些物体就不投阴影了（贴着光源的物体会"漏阴影"）。
                // 与 SRP 路径的 ShadowInput.hlsl 用同一套写法、同一个 UNITY_NEAR_CLIP_VALUE。
                // 这里需要平台的 reversed-Z 约定宏，只引 core 的平台 API 头。
                #if UNITY_REVERSED_Z
                    o.pos.z = min(o.pos.z, o.pos.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    o.pos.z = max(o.pos.z, o.pos.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                return o;
            }

            void frag(Varyings i) { }
            ENDHLSL
        }
    }
}