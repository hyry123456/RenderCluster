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
// 几何数据：顶点按【每簇（每个摆放的每个簇）】去重后烘成【世界空间】，每顶点约 52B。
//
// 为什么不再「局部空间 + 每簇矩阵」：
//   · 索引绘制让 GPU 顶点缓存真正复用（VS 调用从 384/簇 降到约 66/簇），代价是
//     索引值必须能唯一标识「某个摆放的某个顶点」⇒ 顶点必须按摆放展开。
//   · 既然已经展开了，就顺手把世界变换 / 法线逆转置 / Gram-Schmidt / 切线符号全部烘死，
//     VS 里只剩 5 次缓冲读取、零矩阵乘。
//   · 顶点量 = 簇数 × 簇内去重顶点数（约 66），本工程约 20 万级、十几 MB ——
//     和「按唯一 mesh 一份」同量级，不会像「整份 mesh 按摆放复制」那样爆掉。
// ⚠️ 索引值就是这里的全局顶点号：`_CornerGvid[簇 × 384 + corner]`，重建期烘死。
// ---------------------------------------------------------------------------
StructuredBuffer<float3> _Positions;      // 世界空间
StructuredBuffer<float3> _Normals;        // 世界空间、已归一化
StructuredBuffer<float4> _Tangents;       // xyz=世界切线（已与法线正交、归一化） w=副切线符号
StructuredBuffer<float2> _UVs;            // 原始 UV（_MainTex_ST 在 VS 里乘）
StructuredBuffer<uint>   _MatIndex;       // 每顶点所属的材质槽位

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

// 索引绘制下 vertexID 已经是全局顶点号，五个缓冲各取一次即可 ——
// 没有任何间接查表和矩阵运算（全部在重建期烘死了）。
void ClusterFetch(uint vertexID, out uint matIndex,
	out float3 posW, out float3 nrmW, out float4 tanW, out float2 uv)
{
	int gi = (int)vertexID;
	posW = _Positions[gi];
	nrmW = _Normals[gi];
	tanW = _Tangents[gi];
	uv   = _UVs[gi];
	matIndex = _MatIndex[gi];
}

#endif
