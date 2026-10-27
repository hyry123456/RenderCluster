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
//   _Positions —— float3 世界空间顶点
//   索引缓冲是【静态单位缓冲 [0..N]】（drawIdxBuf），SV_VertexID = vid = 0..N；
//   VS 用 vid/384 取 _Visible 槽位、vid%384 取簇内角点：
//     cluster = _Visible[_CascadeBase + vid/384]  → 真实簇号
//     gvid    = _Indices[cluster*384 + vid%384]   → 全局顶点号
//     pos     = _Positions[gvid]
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
            // 关背面剔除：场景里可能有单面片/薄片，剔背面容易漏阴影；
            // 多出来的光栅化开销远小于顶点拉取本身的开销。
            Cull Off

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
            StructuredBuffer<int>   _Indices;      // 全局顶点号（源码），每簇固定 384 个（= 128 三角形×3）
            StructuredBuffer<uint>  _Visible;      // [cascadeBase + slot] = 可见簇下标（compute 产出的紧凑列表）
            // 自定义 world→clip 矩阵（= proj*view），由 C# 经 MaterialPropertyBlock 逐 draw 显式传入。
            // ⚠️ 不用内建 UNITY_MATRIX_M/V：自定义 HLSLPROGRAM 在 DrawProceduralIndirect 下
            // 内建矩阵不被 worldPose 可靠喂入（会恒为单位/零矩阵 ⇒ 全裁光）。见 shader 顶部注释。
            float4x4 _ClusterShadowW2C;
            uint     _CascadeBase;                 // = 级联下标 × 簇数，定位 _Visible 里本联的紧凑段

            struct Attributes
            {
                // 单位索引导下 SV_VertexID = 0,1,2,...,N（索引缓冲是 [0..N] 的静态单位缓冲）。
                // 用它推出"第几个可见簇 + 簇内第几个角点"，再查 _Visible + _Indices 得全局顶点号。
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
            };

            Varyings vert(Attributes i)
            {
                // 顶点还原（替代原"索引缓冲预拼全局顶点号"的做法）：
                //   本方案把"拼索引"从 compute 移到 VS，彻底去掉 Raw→Index 跨类型拷贝这座桥。
                //   vid     = 0..可见簇数×384-1
                //   slot    = vid / 384        → 第几个可见簇（_Visible 里的紧凑槽位）
                //   corner  = vid % 384        → 簇内第几个角点
                //   cluster = _Visible[cascadeBase + slot]   → 真实簇号
                //   gvid    = _Indices[cluster*384 + corner]  → 全局顶点号
                //   pos     = _Positions[gvid]
                uint perCluster = (uint)TRIS_PER_CLUSTER * 3u;
                uint vid    = i.vertexID;
                uint slot   = vid / perCluster;
                uint corner = vid % perCluster;
                uint clusterID = _Visible[_CascadeBase + slot];
                uint gvid   = (uint)_Indices[(int)(clusterID * perCluster + corner)];
                float3 p = _Positions[gvid];

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