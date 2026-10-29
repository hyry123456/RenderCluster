#ifndef CLUSTER_PASS
#define CLUSTER_PASS


struct Attributes {
	float3 positionOS : POSITION;
	float3 normalOS : NORMAL;
	float4 tangentOS : TANGENT;
	float2 baseUV : TEXCOORD0;
	GI_ATTRIBUTE_DATA
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings {
	float4 positionCS_SS : SV_POSITION;
	float4 TtoW0 : VAR_TT0;
	float4 TtoW1 : VAR_TT1;
	float4 TtoW2 : VAR_TT2;
	float2 baseUV : VAR_BASE_UV;
	float2 detailUV : VAR_DETAIL_UV;
	GI_VARYINGS_DATA
	UNITY_VERTEX_INPUT_INSTANCE_ID
	// 簇实例化路径专用：当前片元该用哪个【材质槽位】（每簇常量缓冲的下标）。
	// SRP 路径不读它，编译期整段消失。
	#if defined(CLUSTER_INSTANCED)
		nointerpolation uint matIdx : VAR_MAT_IDX;
	#endif
};

Varyings LitPassVertex (Attributes input) {
	Varyings output;
	UNITY_SETUP_INSTANCE_ID(input);
	UNITY_TRANSFER_INSTANCE_ID(input, output);
	TRANSFER_GI_DATA(input, output);
	float3 positionWS = TransformObjectToWorld(input.positionOS);
	float3 normalWS = TransformObjectToWorldNormal( input.normalOS );
	float3 tangentWS = TransformObjectToWorldDir(input.tangentOS.xyz);
	float3 binormalWS = cross(normalWS, tangentWS) * input.tangentOS.w;

	output.positionCS_SS = TransformWorldToHClip(positionWS);

	output.TtoW0 = float4(tangentWS.x, binormalWS.x, normalWS.x, positionWS.x);
	output.TtoW1 = float4(tangentWS.y, binormalWS.y, normalWS.y, positionWS.y);
	output.TtoW2 = float4(tangentWS.z, binormalWS.z, normalWS.z, positionWS.z);

	output.baseUV = TransformBaseUV(input.baseUV);
	output.detailUV = TransformDetailUV(input.baseUV);
	return output;
}

// 片元主体。抽成独立函数是为了让「MeshRenderer 走 SRP」和「簇实例化绘制」两条路
// 共用同一份着色代码（INPUT_PROP 在簇路径里被重定向到每簇常量缓冲，见 ClusterGBufferInput.hlsl）。
void ClusterGBufferShade (Varyings input,
        out DefferTarget target
    ) 
	{
	
	UNITY_SETUP_INSTANCE_ID(input);
	// InputConfig config = GetInputConfig(input.baseUV);
	ClipLOD(input.positionCS_SS.xy, unity_LODFade.x);
	// bool useMask
	
	//mip 按虚拟贴图总分辨率算：CellSize × T（与 FeedbackPass 逐字一致，否则请求的页 ≠ 采样的页）
	//【原公式多出一个常数项 +1.0 - _VTFeedbackParam.x，已删除】——它让 1:1 距离算出 0.873，
	//三线性后 95% 权重落在半分辨率的 level1（永久糊一级）。
	//_VTFeedbackParam.x = 斜率（0.25，等效标准 0.5；因 log2 内是 dot(dx,dx)=|dx|²）
	//_VTFeedbackParam.w = mipsOffset，默认 0
	float V = _VTFeedbackParam.y * (float)max(1, _TilesPerAxis);
	float2 uv = input.baseUV * V;
	float2 dx = ddx(uv);
	float2 dy = ddy(uv);

	// ---- 手动各向异性 ----
	//RVT 图集没有 mipmap（页表平铺布局不能加 mipmap，tile 之间会串页），
	//而各向异性过滤本质是「沿长轴取粗 mip、沿短轴取细 mip」的多次采样 —— 没有 mip 链，
	//Texture.anisoLevel 完全无效。所以：mip 用【短轴】导数选（保住该方向的高频），
	//再沿【长轴】做 2~4 次偏移采样求和。原写法用 max(dx,dy) + 单次采样，
	//圆柱侧面/地面这类掠射角会整体糊掉一级以上。
	float lenX = length(dx), lenY = length(dy);
	float majorLen = max(lenX, lenY), minorLen = min(lenX, lenY);
	float anisoRatio = majorLen / max(minorLen, 1e-6);
	float2 majorDir = (lenX >= lenY ? dx : dy) / max(majorLen, 1e-6);
	float2 minorVec = (lenX < lenY) ? dx : dy;

	float mipRadio = clamp(_VTFeedbackParam.x * log2(max(dot(minorVec, minorVec), 1e-12)) +
		_VTFeedbackParam.w, 0.0, _VTFeedbackParam.z);

	float maxRadio = ceil(mipRadio), minRadio = floor(mipRadio);
	uint maxMip = (uint)maxRadio, 
		minMip = (uint)minRadio;
	uint index = INPUT_PROP(uint, _MapId);

	float2 majorStepUV = majorDir * (majorLen / V);
	int taps = (anisoRatio > 6.0) ? 4 : ((anisoRatio > 2.0) ? 2 : 1);

	RVTColors colors;
	colors.albedo = 0; colors.normal = 0; colors.mask = 0;
	colors.emission = 0; colors.detail = 0;

	for (int ti = 0; ti < taps; ti++)
	{
		float t = (taps > 1) ? ((float)ti / (float)(taps - 1) - 0.5) : 0.0;
		float2 uvTap = input.baseUV + majorStepUV * t;

		//Tile 粒度虚拟贴图：把 (texId, mip) 交给 GetColor，由它内部切 tile 查 pageId。
		//三线性：在粗细两级之间过渡。必须防 minRadio == maxRadio（smoothstep 内部 0/0 → NaN → 整面黑）。
		RVTColors colorsMin = GetColor(uvTap, index, minMip, input.detailUV);
		RVTColors c;
		if (maxRadio > minRadio)
		{
			RVTColors colorsMax = GetColor(uvTap, index, maxMip, input.detailUV);
			c = LerpColors(colorsMin, colorsMax, smoothstep(minRadio, maxRadio, mipRadio));
		}
		else
		{
			c = colorsMin;
		}
		colors = AddColors(colors, c);
	}
	colors = ScaleColors(colors, 1.0 / (float)taps);


	// float4 albedo = colors.albedo * _render.mainColor;

	// detail 直采（不走 RVT 图集，与 BRG 路径逐字对齐）：
	// RVT 只有一张 detail 图集、平铺语义与分页冲突会串页，detail 层改用硬件 mipmap + 各向异性直采。
	// 三个强度（_DetailAlbedo / _DetailNormal / _DetailRoughness）由 C# 侧在没对应贴图时置 0，
	// 分支既是性能优化也是 NaN 安全闸门。
	float4 detailMap = float4(0.5, 0.5, 0.5, 1.0);
	float4 detailNormalMap = float4(0.5, 0.5, 1.0, 1.0);
	if (INPUT_PROP(float, _DetailAlbedo) > 0.001 || INPUT_PROP(float, _DetailRoughness) > 0.001)
		detailMap = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, input.detailUV);
	if (INPUT_PROP(float, _DetailNormal) > 0.001)
		detailNormalMap = SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailMap, input.detailUV);

	float4 albedo = GetBase(colors, detailMap);
	// Alpha Cutout【只对 _CLIPPING 材质生效】，与 DefferStandard 逐字对齐：
	//   #if defined(_CLIPPING) clip(base.a - GetCutoff()); #endif
	// 之前是无条件 clip —— _CullOff 每个材质都有值(0.5)，不透明材质的贴图 alpha
	// 一旦低于 0.5 就会被整片 discard（表现为凭空出现的黑洞/缺块）。
	#if defined(_CLIPPING)
		clip(albedo.w - INPUT_PROP(float, _CullOff));
	#endif

	float3 preNormal = float3(input.TtoW0.z, input.TtoW1.z, input.TtoW2.z);
	float3 normal = GetNormalTS(colors, detailNormalMap);
	normal = normalize(float3(dot(input.TtoW0.xyz, normal), dot(input.TtoW1.xyz, normal), dot(input.TtoW2.xyz, normal)));

	float2 normalPack = PackNormal(normal);

	float3 positionWS = float3(input.TtoW0.w, input.TtoW1.w, input.TtoW2.w);

	float metallic = colors.mask.x * INPUT_PROP(float, _Metallic);
	half oneMinusReflectivity = (1 - metallic) * 0.96;


	float roughness = GetRoughness(colors, detailMap);
	float ambientOcclusion = colors.mask.y;
	float3 sh = GetBakeDate(GI_FRAGMENT_DATA(input), positionWS, preNormal) * ambientOcclusion;

	//计算漫反射率，实际上F0时的漫反射系数，也就是确定漫反射颜色，如果金属度高，那么就说明漫反射很弱，直接就是值为0
	half3 diffColor = albedo.rgb * oneMinusReflectivity;
	// ★ 间接漫反射（SH9）**不在这里烘焙进 emissive**。
	//   延迟最终合成阶段（DefferCameraPass.hlsl:138）会用 _Sh9 统一算一次间接漫反射并叠加
	//   （DefferStandard / BRG 路径都是这么做的）。这里再烘一遍就是【双重叠加】⇒ 画面偏亮。
	//   （B 档「太亮」就是这个原因。）
	// 乘 _EmissionColor（HDR）。DefferStandard 的 GetEmission 就是 map.rgb * color.rgb；
	// 之前漏了这一步，火把这类自发光物体会直接用贴图原值（偏暗 / 强度不对）。
	float3 emissive = colors.emission.xyz * INPUT_PROP(float4, _EmissionColor).rgb;

	float3 normalRT = float3(normalPack, 1);

	float3 refl = 0;
	float3 viewDir = normalize(_WorldSpaceCameraPos - positionWS);
	float3 reflect_dir = reflect(-viewDir, normal);
	// ❌ 原来这里是 ComputeIndirectSpecular(...) —— 读 unity_SpecCube0。
	//   unity_SpecCube0 / _HDR / _ProbePosition / _BoxMin / _BoxMax 都是 Unity 在【绑定每个
	//   Renderer】时设的全局量；簇走 DrawProceduralIndirect 没有 Renderer ⇒ 读到的是上一个
	//   SRP 物体留下的【陈旧值】。表现：反光强度随相机位置/距离剧变（相机附近刚画的探针亮就暴亮，
	//   换个地方就"反光消失"），盒投影的中心/包围盒也是错的 ⇒ 反射方向乱飞。
	// ✅ 改成与 GBufferPass.hlsl（DefferStandard / BRG）逐字一致：从工程自己的天空盒 _MySkybox 采样
	//   （GPUDravinSystem 每帧 SetGlobalTexture，和 _Sh9 同源），并按粗糙度衰减非金属的反射强度。
	float reflMip = clamp(roughness * 7.0, 0.0, 8.0);
	refl = SamplerReflectProbe(reflect_dir, reflMip);
	float reflIntensity = lerp(saturate(0.05 + (1.0 - roughness) * 0.6), 1.0, metallic);
	refl *= reflIntensity;

	target._GBufferRT0 = float4(albedo.xyz, metallic);
	// target._GBufferRT0 = smoothstep(minRadio, maxRadio, mipRadio);
	target._GBufferRT1 = float4(normalRT, 1);
	target._GBufferRT2 = float4(emissive, roughness);
	target._GBufferRT3 = float4(refl, ambientOcclusion);     //暂时没有反射
}

// MeshRenderer（SRP）路径的片元入口：材质参数走 UnityPerMaterial。
void LitPassFragment (Varyings input, out DefferTarget target)
{
	ClusterGBufferShade(input, target);
}



#endif