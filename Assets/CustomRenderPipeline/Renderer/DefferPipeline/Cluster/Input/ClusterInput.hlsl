#ifndef CLUSTER_INPUT
#define CLUSTER_INPUT


//双路径属性声明：
//· DOTS_INSTANCING_ON 变体（BRG 绘制时 Unity 自动启用该关键字）：
//    - 材质属性本体放在 UnityPerMaterial cbuffer（SRP Batcher 要求名字恰好是
//      UnityPerMaterial，从材质实例填充，与 DrawRenderers 路径一致）；
//    - 同时再声明一份 DOTS 实例元数据（MaterialPropertyMetadata cbuffer），目的是
//      让 Unity 生成并保持 UnityInstancingDOTS_InstanceVisibility cbuffer 存活
//      （否则 BRG 在 Submit 时报 “Missing DOTS_INSTANCING_ON variant” —— 见本文件
//      末尾注释）；
//    - 只有变换矩阵（unity_ObjectToWorld / unity_WorldToObject）走 DOTS metadata
//      加载（见 Common.hlsl）。
//  INPUT_PROP 走 UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT：编译期会引用
//  unity_DOTSVisibleInstances（保持 InstanceVisibility cbuffer 存活）；运行期因
//  材质属性未 override（BRG 没注册它们的 per-instance metadata，高位置 0）→ 回退到
//  UnityPerMaterial 里的全局值，画面与 DrawRenderers 一致。
//· 普通变体（MeshRenderer 路径）：维持原来的 UnityPerMaterial 实例化缓冲写法
//
// ★ UnityPerMaterial 的声明【统一放在 ClusterDOTSMaterial.hlsl】（两个分支都声明）。
//   以前非 DOTS 分支在这里单独声明，导致：ClusterPBR.shader 的 ShadowCaster / WriteDeep pass
//   （它们只 include ClusterDOTSMaterial.hlsl）拿不到这个 cbuffer ⇒ 同一 SubShader 内
//   各 pass 的 UnityPerMaterial 不一致 ⇒ **整个 shader 不是 SRP Batcher 兼容材质** ⇒
//   阴影投射物不能走 `Shadows.DrawSRP Batcher`，1219 个投射物逐个 draw（实测 0.45ms → 2.39ms）。
#include "ClusterDOTSMaterial.hlsl"






// float4 _VTFeedbackParam;
// //int _MaxTextureCount;		//最高级的一层的最大贴图数量
// int _SumTextureCount;		//数组中每一层具有的贴图结点数量
//float _PaddingSizes[4];		//4级Mips中每一级的padding占整张贴图的大小




// StructuredBuffer<TextureNode> _TextureNodesBuffer;		//4级Mips合并得到的数组

float2 TransformBaseUV (float2 baseUV) {
	float4 baseST = INPUT_PROP(float4, _MainTex_ST);
	return baseUV * baseST.xy + baseST.zw;
}

float2 TransformDetailUV(float2 baseUV){
	float4 baseST = INPUT_PROP(float4, _DetailMap_ST);
	return baseUV * baseST.xy + baseST.zw;
}

struct RVTColors{
    float4 albedo;
    float4 normal;
    float4 mask;
    float4 emission;
    float4 detail;
};

//float4 _QuarterRange[4];


RVTColors GetColor(float2 uv, uint texId, uint mip, float2 detailUV){

	//pageId = mip * _SumTextureCount + texId * (T*T) + tileIndex，与 Feedback 编码完全一致
	int T = _TilesPerAxis;
	int Tsq = T * T;
	//先 Wrap 再切 tile：uv 已经过 _MainTex_ST 变换，tiling>1 时会 >1；
	//直接 *T 后 clamp 会把所有重复段压到最后一个 tile（整片拉伸、法线方向全错）。
	//T=1 时与旧写法等价（后续本来就要 frac）。
	float2 wrappedUV = frac(uv);
	float2 tileUV = wrappedUV * (float)T;
	int tileX = clamp((int)floor(tileUV.x), 0, T - 1);
	int tileY = clamp((int)floor(tileUV.y), 0, T - 1);
	float2 localUV = frac(tileUV);
	float2 localDetailUV = frac(detailUV * (float)T);
	int tileIndex = tileY * T + tileX;
	int pageId = (int)mip * _SumTextureCount + (int)texId * Tsq + tileIndex;
	TextureNode node = _TextureNodesBuffer[pageId];

	float4 levelData = _LevelData[node.mipsLevel],
		levelPadding = _LevelPadding[node.mipsLevel];
    localUV = frac(localUV); 													//限制uv范围，默认都是Repeat
    localDetailUV = frac(localDetailUV);


	//第一个是offset，然后加到目标位置，再加Padding
	float2 tileBegin = float2(levelData.x, 0) + levelData.yz * float2(node.width, node.height) + levelPadding.xy;
	//贴图值起点加上uv值
	localUV = tileBegin + levelPadding.zw * localUV;
    localDetailUV = tileBegin + levelPadding.zw * localDetailUV;

#if UNITY_UV_STARTS_AT_TOP
    localUV.y = 1.0 - localUV.y;
    localDetailUV.y = 1.0 - localDetailUV.y;
#endif
	
	RVTColors colors;
	//每张图集用自己的采样器（跟随纹理 anisoLevel），不要用全局 sampler_linear_repeat —— 见 UnityInput.hlsl 的说明
	colors.albedo   = SAMPLE_TEXTURE2D_LOD(_VirtualAlbedo,     sampler_VirtualAlbedo,   localUV, 0);
    colors.normal   = SAMPLE_TEXTURE2D_LOD(_VirtualNormal,     sampler_VirtualNormal,   localUV, 0);
    // 【mask 白回退】与 DefferStandard 的 GetMask(c) 逐字对齐：没有 _MASK_MAP keyword 时
    // mask 恒为白(1) —— 否则 metallic/roughness/AO 会被无关贴图整层带偏
    //（本工程 5 个资产的 pbrData 填的是高光图，g 通道≈0 ⇒ AO≈0 ⇒ 物体在延迟管线里直接变纯黑）。
    // 同时【跳过这次取样】：没有 mask map 时图集里那份 mask 本来就不该用。
    colors.mask     = (INPUT_PROP(float, _UseMask) > 0.5)
        ? SAMPLE_TEXTURE2D_LOD(_VirtualMask, sampler_VirtualMask, localUV, 0)
        : float4(1.0, 1.0, 1.0, 1.0);
    colors.emission = (INPUT_PROP(float, _UseEmission) > 0.5)
        ? SAMPLE_TEXTURE2D_LOD(_VirtualEmission, sampler_VirtualEmission, localUV, 0)
        : float4(0.0, 0.0, 0.0, 0.0);
	colors.detail   = SAMPLE_TEXTURE2D_LOD(_VirtualDetail,     sampler_VirtualDetail,   localDetailUV, 0);
	return colors;
}

RVTColors LerpColors(RVTColors color1, RVTColors color2, float radio){


    color1.albedo   = color1.albedo     * (1.0 - radio) + color2.albedo     * radio;
    color1.normal   = color1.normal     * (1.0 - radio) + color2.normal     * radio;
    color1.mask     = color1.mask       * (1.0 - radio) + color2.mask       * radio;
    color1.emission = color1.emission   * (1.0 - radio) + color2.emission   * radio;
    color1.detail   = color1.detail     * (1.0 - radio) + color2.detail     * radio;

    return color1;
}

// 供「手动各向异性」的多次采样累加 / 求平均使用。
// RVT 图集没有 mipmap（页表平铺布局也不能加，会串页），所以 Texture.anisoLevel 无效，
// 各向异性只能在 shader 里用多次偏移采样模拟 —— 见 ClusterPass.hlsl 的说明。
RVTColors AddColors(RVTColors a, RVTColors b)
{
    a.albedo += b.albedo; a.normal += b.normal; a.mask += b.mask;
    a.emission += b.emission; a.detail += b.detail;
    return a;
}

RVTColors ScaleColors(RVTColors c, float s)
{
    c.albedo *= s; c.normal *= s; c.mask *= s; c.emission *= s; c.detail *= s;
    return c;
}

float4 GetBase (RVTColors colors) {
	float4 map = colors.albedo;
	float4 color = INPUT_PROP(float4, _Color);

    float detail = (colors.detail.r * 2.0 - 1.0) * INPUT_PROP(float, _DetailAlbedo);
	float mask = colors.mask.b;
	// return map * (1.0 - detailV) + map * map * detailV;
	// return map * map * detailV;
	// return map * (1.0 - detailV);
    map.xyz = lerp(sqrt(map.xyz), detail < 0.0? 0.0 : 1.0, abs(detail) * mask);
	map.rgb *= map.rgb;
	return map * color;
}

float3 GetNormalTS (RVTColors colors) {
	float4 map = colors.normal;
    float normalScale = INPUT_PROP(float, _NormalScale);
	// RVT 图集法线是「法线贴图原样拷贝」，用 DecodeNormal（UnpackNormalmapRGorAG）解码。
	float3 normal = DecodeNormalSafe(map, normalScale);
	// 法线贴图绿通道为 DirectX 约定（Y 朝下），Unity 切线空间是 OpenGL 约定（Y 朝上）→ 必须翻转 Y。
	// 这不是调试开关：flipNormalY=1 时场景法线才是对的，删掉后整场景法线方向反、光照全错。
	normal.y *= -1.0;

	// detail 法线链路【禁用】——这是"RVT 下法线失效"的根因：
	// RVT 只有一张 detail 贴图（与 DefferStandard 的 _DetailMap/_DetailNormalMap 两张约定不同），
	// 资产 detail=null 时走 grayTexture fallback，解出的 detail.z=0 会让 BlendNormalRNM 除零产生 NaN，
	// 而 Barrel_01 的 _DetailNormal=1、mask.b=1 → detailIntensity 恒为 1 → lerp 直接返回 NaN，
	// 把正常法线整个污染掉（表面看起来"没有法线"）。标准路径因 _DETAIL_MAP 未开而幸免。
	// 需要 detail 时按 DefferStandard 的两张贴图约定重新接入。
	//float3 unpackNormal = UnpackNormal(float3(colors.detail.y, colors.detail.w, 1));
	//float3 detail = DecodeNormal(float4(unpackNormal, 1.0f), 1.0f);
	//float detailIntensity = INPUT_PROP(float, _DetailNormal) * colors.mask.b;
	//float3 blendNor = BlendNormalRNM(normal, detail);
	return normal;

}

float GetRoughness (RVTColors colors) {
	float roughness = INPUT_PROP(float, _Roughness);
	roughness *= colors.mask.w;

    roughness = lerp(roughness, 1.0f, INPUT_PROP(float, _DetailRoughness) * colors.detail.z * colors.mask.b);
	// if (c.useDetail) {
	// 	float detail = GetDetail(c).b * ;
	// 	float mask = GetMask(c).g;
	// 	roughness =
	// 		lerp(roughness, detail < 0.0 ? 0.0 : 1.0, abs(detail) * mask);
	// }
	
	return roughness;
}


#endif
