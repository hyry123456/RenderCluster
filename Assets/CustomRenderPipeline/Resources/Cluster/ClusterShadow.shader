// 【真正的 cluster 管线】用于绘制阴影的簇着色器（只写深度，不采任何贴图）。
//
// ── 矩阵来源（2026-10-21 修正）──
// ⚠️ 不要在这里自己推导"世界空间 → 阴影裁剪空间"的矩阵！
//
// Shadows.RenderDirectionalShadows 里已经调了
//     buffer.SetViewProjectionMatrices(m_cascadeView[i], m_cascadeProj[i]);
// 它设置的就是 unity_MatrixVP。而 SRP 路径（context.DrawShadows）画的 ShadowCaster
// pass 用的正是 TransformWorldToHClip = mul(unity_MatrixVP, float4(posWS,1))。
//
// 所以本 shader 只要【原样】用 unity_MatrixVP，就与 SRP 路径逐位相同 ——
// 深度值、深度方向（reversed-Z）、近平面裁剪行为全部一致，
// 写进 shadow map 的值必然等于采样侧 CompareToAtlasMatrix 读出来比较的值。
//
// ❌ 之前踩的坑：手工把 proj*view 的 z 行改写成 0.5*w行 - 0.5*z行。
//    那是【推测性推导】—— ComputeDirectionalShadowMatricesAndCullingPrimitives
//    返回的矩阵本来就是给 SetViewProjectionMatrices 用的，引擎自己在
//    context.DrawShadows 内部处理平台 Z 约定。手推的结果既没验证、又与引擎行为
//    不一致（把 z 映射到 [0,1] 但 w 行没跟着变）⇒ 阴影位置/深度全错。
//
// ── 数据布局（C# 侧 ClusterShadowRenderer.Rebuild 摊平）──
//   _Positions —— float3，世界空间顶点（静态场景，烘焙时就乘好了世界矩阵）
//   _Indices  —— int32，【全局顶点号】。每簇固定 384 个（= 128 三角形 × 3），
//                簇 c 的索引区间 = [c * 384, c * 384 + 384)。
//                ⇒ 顶点拉取就是：vi = _Indices[c*384 + vertexID]; p = _Positions[vi];
//   _Visible  —— uint，GPU 剔除后的可见簇列表。索引从 _ClusterBase 起。
//
// ⚠️ 矩阵/基址必须是【全局属性】（buffer.SetGlobalXxx），不能用 drawMat.SetMatrix：
//    Material 是共享对象，材质属性在命令【执行】时才读 ⇒ 4 个级联的 draw 全都拿到
//    最后一次设置的值（第 4 级的矩阵），阴影会是错的。全局属性是命令，顺序有保证。
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

            // 每簇顶点数。必须与 C# 侧 ClusterShadowRenderer.VertsPerCluster 保持一致！
            #define VERTS_PER_CLUSTER 384

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<int>    _Indices;   // 全局顶点号（不是簇内局部号）
            StructuredBuffer<uint>   _Visible;   // GPU 剔除后的可见簇 id

            // unity_MatrixVP 由 CommandBuffer.SetViewProjectionMatrices 设置，
            // 与 SRP 路径的 TransformWorldToHClip 同一个东西。
            float4x4 unity_MatrixVP;

            // 该级联在 _Visible 里的起始下标。
            // ⚠️ 必须声明成 float：CommandBuffer.SetGlobalInt 内部是按 float 存的（Unity 老坑），
            //    声明成 uint 会读到 float 的位模式。取值 < 2^24 时 float 精确，强转安全。
            float    _ClusterBase;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
            };

            Varyings vert(Attributes i)
            {
                // instanceID → 可见列表里的第几个 → 真实簇号
                uint c = _Visible[(uint)_ClusterBase + i.instanceID];
                int vi = _Indices[c * VERTS_PER_CLUSTER + i.vertexID];
                float3 p = _Positions[vi];

                Varyings o;
                o.pos = mul(unity_MatrixVP, float4(p, 1.0));

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