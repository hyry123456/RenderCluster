#ifndef CLUSTER_GBUFFER_INPUT
#define CLUSTER_GBUFFER_INPUT

// ============================================================================
// 簇实例化绘制（Cluster 画 GBuffer / RVT Feedback）的取数层。
//
// 目的：让 ClusterInput.hlsl / ClusterPass.hlsl / FeedbackPass.hlsl 这三份既有代码
// 【一行都不用改】就实现「一次 draw 画许多簇、每簇用自己的材质参数」——
// 做法是把 INPUT_PROP 这个宏重定向到「每簇材质常量缓冲」。
//
// ClusterDOTSMaterial.hlsl 被本文件用 CLUSTER_INSTANCED_MATERIAL 屏蔽掉，
// 所以不会有 UnityPerMaterial 参与；材质参数全部来自 _ClusterMaterials。
// ============================================================================

// 每簇材质参数。顺序/大小必须与 C# 的 ClusterGBufferRenderer.ClusterMaterialData 逐字一致
// （7 个 float4 = 112 字节）。
struct ClusterMaterialData
{
	uint   _MapId;
	float  _NormalScale;
	float  _Metallic;
	float  _Roughness;

	float4 _Color;
	float4 _EmissionColor;
	float4 _MainTex_ST;
	float4 _DetailMap_ST;

	float  _DetailAlbedo;
	float  _DetailNormal;
	float  _DetailRoughness;
	float  _CullOff;

	float  _UseMask;
	float  _UseNormal;
	float  _UseEmission;
	float  _Pad;
};

StructuredBuffer<ClusterMaterialData> _ClusterMaterials;

// 当前片元所属簇的【材质槽位】（= _MatIndex[顶点号]，不是簇号本身！）。
// 簇号逐【片元】变化（一次 draw 覆盖许多簇），所以不能当 uniform —— 由 VS 查缓冲取到，
// 经 nointerpolation varying 传下来，片元第一件事写进这个 static 变量。
// HLSL 的全局 static 是「着色器内部局部量」，每个线程各有一份，写完再读是安全的。
// ⚠️ 写成簇号会越界读 _ClusterMaterials（材质数 ≈ 上千，簇数 ≈ 三万）⇒ 参数全 0 ⇒ 整片黑。
static uint _ClusterMatIndex;

// 屏蔽 ClusterDOTSMaterial.hlsl 的 UnityPerMaterial 声明，并接管 INPUT_PROP。
// ⚠️ 必须在 include ClusterInput.hlsl 之前定义：那些函数的函数体在 include 时就地展开宏。
#define CLUSTER_INSTANCED_MATERIAL 1
#define INPUT_PROP(type, name) _ClusterMaterials[_ClusterMatIndex].name

#include "ClusterInput.hlsl"

// ---------------------------------------------------------------------------
// 几何数据：顶点按【每簇（每个摆放的每个簇）】去重后烘成【世界空间】，
// 每顶点打包成 2 个 uint4 = 32B（此前是 5 个独立缓冲共 52B）：
//   e0 = (pos.x, pos.y, pos.z, matIndex | sign<<31)   ← 位置用 asuint 原样透传，零精度损失
//   e1 = (nrmOct, tanOct, uv.x, uv.y)                 ← 法线/切线八面体编码(2×unorm16)，UV 仍是完整 float
// 好处：两个 uint4 地址连续（同一次缓存行往返就能拿全属性）、VS 里 2 次 load 而不是 5 次，
//       显存也从 52B/顶点 降到 32B/顶点。
//
// 为什么顶点要按摆放展开：索引绘制要求「同一顶点用同一个索引值」才能命中 GPU 顶点缓存，
// 而不同摆放的同名顶点变换不同 ⇒ 只能按摆放（= 簇记录）展开。
// 展开后每簇只存去重后的约 66 个顶点（不是 384 个角点），总量与「按唯一 mesh 一份」同量级。
// 既然展开了，就把世界变换 / 法线逆转置 / Gram-Schmidt 一起烘死，VS 里零矩阵运算。
// ⚠️ 索引值就是这里的全局顶点号：`_CornerGvid[簇 × 384 + corner]`，重建期烘死。
// ---------------------------------------------------------------------------
// 存的是 float4，但每个分量都是【按位】塞进去的 uint ⇒ shader 侧 asuint 取回，零精度损失。
StructuredBuffer<float4> _Vertices;       // 每顶点 2 个 float4（见上）

// 八面体解码：与 C# 的 OctEncode 逐位对应（存 0..65535，这里 *1/65535 再 *2-1）。
float3 OctDecode(uint u)
{
	float2 e = float2(u & 0xFFFFu, u >> 16) * (1.0 / 65535.0);
	e = e * 2.0 - 1.0;
	float3 n = float3(e.x, e.y, 1.0 - abs(e.x) - abs(e.y));
	if (n.z < 0.0)
		n.xy = (1.0 - abs(n.yx)) * (n.xy >= 0.0 ? 1.0 : -1.0);
	return normalize(n);
}

// 每簇三角形数。改这个值必须同步改 C# 的 ClusterGBufferRenderer.TrisPerCluster 与 compute。
#define TRIS_PER_CLUSTER 128

// 世界→裁剪矩阵。自定义 HLSLPROGRAM 在 DrawProceduralIndirect 下拿不到内建
// unity_MatrixVP（worldPose 参数不喂内建矩阵，恒单位矩阵 ⇒ 全裁光），
// 所以和阴影路径一样，逐 draw 经 MaterialPropertyBlock 传进来。
float4x4 _ClusterGBufferW2C;

struct ClusterAttributes
{
	// 索引绘制下 SV_VertexID = 索引值 = 全局顶点号（baseVertex 恒 0）
	uint vertexID : SV_VertexID;
};

// 索引绘制下 vertexID 已经是全局顶点号：两次 uint4 读取拿全属性，
// 再解两个八面体。没有任何间接查表，也没有矩阵运算（全部在重建期烘死了）。
void ClusterFetch(uint vertexID, out uint matIndex,
	out float3 posW, out float3 nrmW, out float4 tanW, out float2 uv)
{
	uint2 idx = uint2(vertexID * 2u, vertexID * 2u + 1u);
	uint4 e0 = asuint(_Vertices[idx.x]);
	uint4 e1 = asuint(_Vertices[idx.y]);

	posW = float3(asfloat(e0.x), asfloat(e0.y), asfloat(e0.z));

	// 材质槽位与副切线符号共用最后一个 uint（matIndex < 2^23，高位留 1 bit 给符号）
	matIndex = e0.w & 0x7FFFFFFFu;
	float sign = (e0.w & 0x80000000u) != 0u ? -1.0 : 1.0;

	nrmW = OctDecode(e1.x);
	// 16 bit 量化后法线/切线不再严格正交，做一次正交化；
	// 退化时 normalize(0)=NaN ⇒ 整面法线 NaN ⇒ 全黑，故兜底。
	float3 t = OctDecode(e1.y);
	t -= nrmW * dot(nrmW, t);
	float tt = dot(t, t);
	t = tt > 1e-12 ? t * rsqrt(tt) : normalize(cross(nrmW, float3(0, 1, 0)));

	tanW = float4(t, sign);
	uv   = float2(asfloat(e1.z), asfloat(e1.w));
}

#endif
