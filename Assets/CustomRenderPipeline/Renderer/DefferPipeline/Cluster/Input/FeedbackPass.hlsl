#ifndef FEED_BACK_PASS
#define FEED_BACK_PASS


struct Attributes_Simple {
	float3 positionOS : POSITION;
	float2 baseUV : TEXCOORD0;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings_Simple {
	float4 positionCS_SS : SV_POSITION;
	float2 baseUV : VAR_BASE_UV;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings_Simple VertexSimple (Attributes_Simple input) {
	Varyings_Simple output;
	UNITY_SETUP_INSTANCE_ID(input);
	UNITY_TRANSFER_INSTANCE_ID(input, output);

	output.positionCS_SS = TransformObjectToHClip(input.positionOS);

	output.baseUV = TransformBaseUV(input.baseUV);
	return output;
}

float4 fragSimple (Varyings_Simple input) : SV_Target
{
	// float2 uv = i.uv;
	UNITY_SETUP_INSTANCE_ID(input);
	//【必须乘 T】mip 选择按【虚拟贴图总分辨率】算 = CellSize × 每轴 tile 数。
	//只用 CellSize：T>1 时 texel 密度被低估 T 倍 → 少算 log2(T) 级 mip
	//（T=4 少算 2 级）→ 每帧都为中远距离像素申请最高分辨率页，
	//把本就按 T² 缩水的页池打爆 → LRU 抖动 → 整体变糊（"开了 Tile 反而看不清"的真因之一）。
	//T=1 时本式与旧写法完全等价，零回归。
	float2 uv = input.baseUV * (_VTFeedbackParam.y * (float)max(1, _TilesPerAxis));
	float2 dx = ddx(uv);
	float2 dy = ddy(uv);

	//w是偏差值，用来调整Mips的选择情况，z是mips1的最大等级-1
	// uint mip = clamp(uint(_VTFeedbackParam.x * log2(max(dot(dx, dx), dot(dy, dy))) + 1.0 - 
	// 	_VTFeedbackParam.x + _VTFeedbackParam.w), 0, _VTFeedbackParam.z);
	//w是偏差值（mipsOffset，默认0），z是最大mip等级-1
	//【已删除原公式的常数项 +1.0 - _VTFeedbackParam.x】——它让 1:1 距离算出 0.873，
	//使 95% 权重落在半分辨率的 level1（永久糊一级）。详见 ClusterPass.hlsl 的说明。
	//【必须用短轴(min)】ClusterPass 现在按短轴选 mip 再做长轴多 tap；这里若还用 max，
	//就变成「请求粗页、采样细页」→ 采到未分配的页，画面串色/黑块。两处绑定必须完全一致。
	float lenX = length(dx), lenY = length(dy);
	float2 minorVec = (lenX < lenY) ? dx : dy;
	uint mip = clamp(uint(_VTFeedbackParam.x * log2(max(dot(minorVec, minorVec), 1e-12)) +
		_VTFeedbackParam.w), 0, _VTFeedbackParam.z);

	uint index = INPUT_PROP(uint, _MapId);
#ifdef _FEED_BACK_DEBUG
	return 1.0 - ((float)mip / _VTFeedbackParam.z);
#else
	//Tile 粒度虚拟贴图：把反馈编码从 (texId, mip) 升级为 32-bit pageId。
	//pageId = mip * _SumTextureCount + texId * (T*T) + tileIndex
	//  tileIndex = tileY * T + tileX，其中 tile 由被 tiling 后的 baseUV 推导。
	//T=1 时 Tsq=1、tileIndex=0，pageId 退化为 mip*_SumTextureCount + texId，
	//与旧路径完全等价。RGBA8 每通道 8bit 拼出 32bit pageId。
	uint T = (uint)_TilesPerAxis;
	uint Tsq = T * T;
	//先 Wrap 再切 tile：_MainTex_ST 带 tiling 时 baseUV 会 >1（甚至为负），
	//若直接 *T 再 clamp(0,T-1)，所有重复段都会被压到最后一个 tile ——
	//整片被一张 tile 拉伸铺满（法线尤其明显：整面朝向由这一张 tile 决定，看起来"方向全错"）。
	//frac 对负数同样返回 [0,1)（x-floor(x)），所以 tiling 偏移也安全。
	//T=1 时 frac(baseUV)*1 再 frac 与原写法完全等价，零回归。
	float2 wrappedBaseUV = frac(input.baseUV);
	float2 tileUV = wrappedBaseUV * (float)T;
	uint tileX = (uint)clamp(floor(tileUV.x), 0.0, (float)(T - 1u));
	uint tileY = (uint)clamp(floor(tileUV.y), 0.0, (float)(T - 1u));
	uint tileIndex = tileY * T + tileX;
	uint pageId = (uint)mip * (uint)_SumTextureCount + index * Tsq + tileIndex;
	uint r = pageId & 255u;
	uint g = (pageId >> 8) & 255u;
	uint b = (pageId >> 16) & 255u;
	uint a = (pageId >> 24) & 255u;
	return float4((float)r / 255.0, (float)g / 255.0, (float)b / 255.0, (float)a / 255.0);
#endif
	// return float4(0, 0, 0, 1);
}

#endif
