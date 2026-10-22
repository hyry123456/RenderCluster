#ifndef GPU_TERRAIN_PASS
#define GPU_TERRAIN_PASS

#include "../../DeffererLibrary/DeffererInput.hlsl"

struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
    uint instanceID : SV_InstanceID;
    half4 color : COLOR;
};

struct OutVert {
    float4 pos : SV_POSITION;
    float3 worldPos : VAR_WORLDPOS;
    float2 worldUV : UV;
    float4 normal                   : TEXCOORD3;    // xyz: normal, w: viewDir.x
    float4 tangent                  : TEXCOORD4;    // xyz: tangent, w: viewDir.y
    float4 bitangent                : TEXCOORD5;    // xyz: bitangent, w: viewDir.z
};

struct LookData
{
    uint2 tilePos;
    uint mipsLevel;
};

struct SimpleVert{
    float4 pos : SV_POSITION;
    float3 worldPos : TEXCOORD0;
    float2 worldUV : UV;
};




SimpleVert boundsVert(appdata v){
    SimpleVert o;
    Bounds bounds = BoundsList[v.instanceID];

    // o.pos = TransformWorldToHClip(v.vertex.xyz);
    v.vertex.xyz = lerp(bounds.minPosition, bounds.maxPosition, v.vertex.xyz);
    o.pos = TransformWorldToHClip(v.vertex.xyz);
    o.worldPos = v.vertex.xyz;
    o.worldUV = 0;
    
    return o;
}

float4 boundsFrag(SimpleVert i) : SV_TARGET{
    return 0.3f;
}

SimpleVert simpleVert(appdata v){
    SimpleVert o;
    uint4 node = _Nodes[v.instanceID];
    float4 nodeSize = _NodeSize[node.z];

    CaculatePositionOS(v.vertex.xyz, node, v.color);
    v.vertex.xz /= _PointCount;
    v.vertex.xz = nodeSize * (node.xy + v.vertex.xz);

    float2 worldUV = v.vertex.xz / (_NodeSize[4].xy * 5);
    
    float heigh = SAMPLE_TEXTURE2D_LOD(_HeighMap, sampler_linear_clamp, worldUV, 0).x * nodeSize.w;
    v.vertex.y = heigh;
    v.vertex.xyz += _BeginPosition.xyz;

    o.worldPos = v.vertex.xyz;
    o.pos = TransformWorldToHClip(v.vertex.xyz);
    o.worldUV = worldUV;
    // o.worldUV = v.uv;
    return o;
}
uint _ColumnCount, _RowCount;
StructuredBuffer<uint> _TerrainIndexs;

void gbufferFrag(SimpleVert input,
        out DefferTarget target){
    // return float4(input.worldUV, 0, 1);
    uint columnId = _ColumnCount * input.worldUV.x, rowId = _RowCount * input.worldUV.y;
    uint index = _TerrainIndexs[rowId * _ColumnCount + columnId];

    // return index / 255.0f;

	float2 uv = input.worldUV * _VTFeedbackParam.y;
	float2 dx = ddx(uv);
	float2 dy = ddy(uv);

	uint minMip = (uint)clamp(_VTFeedbackParam.x * log2(max(dot(dx, dx), dot(dy, dy))) - 
		_VTFeedbackParam.x + _VTFeedbackParam.w, 0.0, _VTFeedbackParam.z);

	TextureNode nodeMin = _TextureNodesBuffer[_SumTextureCount * minMip + index];
    float2 tileUV = frac(input.worldUV * uint2(_ColumnCount, _RowCount));
    // float2 tileUV = frac(input.worldUV * uint2(_RowCount, _ColumnCount));

    TerrainColors colors = GetColor(tileUV, nodeMin);
    // return colors.albedo;
    float3 normal = SampleNormal(input.worldUV);

    float3 sh = GetIndirectLight(normal).xyz * colors.mask.z;

    normal = GetWorldNormal(colors.normal.xyz, normal.xyz);
    // normal = normalize(normal);

    float4 base = colors.albedo;
    float metallic = colors.mask.x, roughness = 1.0f - colors.mask.y;

	half oneMinusReflectivity = (1 - metallic) * 0.96;
	//计算漫反射率，实际上F0时的漫反射系数，也就是确定漫反射颜色，如果金属度高，那么就说明漫反射很弱，直接就是值为0
	half3 diffColor = base.rgb * oneMinusReflectivity;
	sh *= diffColor;
	// emissive += sh;		//计算过OA的间接光数据，之后直接加上去就行了

    float3 positionWS = input.worldPos;

	float3 refl = 0;
	float3 viewDir = normalize(_WorldSpaceCameraPos - positionWS);
	float3 reflect_dir = reflect(-viewDir, normal);
	float mip = PerceptualRoughnessToMipmapLevel(roughness);
    refl = SamplerReflectProbe(reflect_dir, mip);

	float2 normalPack = PackNormal(normal);
    target._GBufferRT0 = float4(base.xyz, metallic);
    // target._GBufferRT0 = float4(colors.normal.xyz, metallic);
	target._GBufferRT1 = float4(normalPack, 1, 1);
	target._GBufferRT2 = float4(sh, roughness);
	target._GBufferRT3 = float4(refl, 1);
    // return float4(normal, 1);
    // return float4(tileUV, 0, 1);
}

// float4 _VTFeedbackParam;


float4 feedbackFrag(SimpleVert input) : SV_TARGET{
    //    return float4(input.worldUV, 0, 1);
    float2 uv = input.worldUV * _VTFeedbackParam.y;
	float2 dx = ddx(uv);
	float2 dy = ddy(uv);

	//w是偏差值，用来调整Mips的选择情况，z是mips1的最大等级-1
	// uint mip = clamp(uint(_VTFeedbackParam.x * log2(max(dot(dx, dx), dot(dy, dy))) + 1.0 - 
	// 	_VTFeedbackParam.x + _VTFeedbackParam.w), 0, _VTFeedbackParam.z);
	uint mip = clamp(uint(_VTFeedbackParam.x * log2(max(dot(dx, dx), dot(dy, dy))) - 
		_VTFeedbackParam.x + _VTFeedbackParam.w), 0, _VTFeedbackParam.z);

	// uint index = INPUT_PROP(_MapId);
    uint columnId = _ColumnCount * input.worldUV.x, rowId = _RowCount * input.worldUV.y;
    uint index = _TerrainIndexs[rowId * _ColumnCount + columnId];
#ifdef _FEED_BACK_DEBUG
	return 1.0 - ((float)mip / _VTFeedbackParam.z);
#else
	uint x = index / 256, y = index % 256;
	return float4(x / 255.0, y / 255.0, mip / 255.0, 1);
#endif

}


struct ShadowVert{
    float4 pos : SV_POSITION;
};
bool _ShadowPancaking;

ShadowVert shadowVert(appdata v){
    ShadowVert o;
    uint4 node = _Nodes[v.instanceID];
    float4 nodeSize = _NodeSize[node.z];

    CaculatePositionOS(v.vertex.xyz, node, v.color);
    v.vertex.xz /= _PointCount;
    v.vertex.xz = nodeSize * (node.xy + v.vertex.xz);

    float2 worldUV = v.vertex.xz / (_NodeSize[4].xy * 5);
    
    float heigh = SAMPLE_TEXTURE2D_LOD(_HeighMap, sampler_linear_clamp, worldUV, 0).x * nodeSize.w;
    v.vertex.y = heigh;
    v.vertex.xyz += _BeginPosition.xyz;
    
    o.pos = TransformWorldToHClip(v.vertex.xyz);

	if (_ShadowPancaking) {
		#if UNITY_REVERSED_Z
			o.pos.z = min(
				// output.positionCS_SS.z, output.positionCS_SS.w * UNITY_NEAR_CLIP_VALUE
				o.pos.z, UNITY_NEAR_CLIP_VALUE
			);
		#else
			o.pos.z = max(
				o.pos.z, UNITY_NEAR_CLIP_VALUE
				// output.positionCS_SS.z, output.positionCS_SS.w * UNITY_NEAR_CLIP_VALUE
			);
		#endif
	}

    return o;
}
void shadowFrag(){
}

float4 gbufferFragEditor(SimpleVert input) : SV_TARGET{
    float3 positionWS = input.worldPos;
    float alpha;
    TerrainColors colors = DirectGetColor(input.worldUV, positionWS, alpha);

    float3 normal = SampleNormal(input.worldUV);

    float3 sh = GetIndirectLight(normal).xyz * colors.mask.z;

    normal = GetWorldNormal(colors.normal.xyz, normal.xyz);
    // normal = normalize(normal);

    float4 base = colors.albedo;
    float metallic = colors.mask.x, roughness = 1.0f - colors.mask.y;

	half oneMinusReflectivity = (1 - metallic) * 0.96;
	//计算漫反射率，实际上F0时的漫反射系数，也就是确定漫反射颜色，如果金属度高，那么就说明漫反射很弱，直接就是值为0
	half3 diffColor = base.rgb * oneMinusReflectivity;
	sh *= diffColor;
	// emissive += sh;		//计算过OA的间接光数据，之后直接加上去就行了

	float3 refl = 0;
	float3 viewDir = normalize(_WorldSpaceCameraPos - positionWS);
	float3 reflect_dir = reflect(-viewDir, normal);
	float mip = PerceptualRoughnessToMipmapLevel(roughness);
    refl = SamplerReflectProbe(reflect_dir, mip);


	Surface surface = (Surface)0;
	surface.position = positionWS;
	surface.normal = normal;
	surface.viewDirection = normalize(_WorldSpaceCameraPos - positionWS);
	surface.depth = -TransformWorldToView(positionWS).z;
	surface.color = base.rgb;
	surface.alpha = alpha;
	surface.metallic = metallic;
	surface.roughness = roughness;
	// surface.ambientOcclusion = colors.mask.z;
	surface.ambientOcclusion = 1;
	surface.dither = InterleavedGradientNoise(input.pos.xy, 0);

	float3 color = GetLight(surface);
	// sh = GetBakeDate(GI_FRAGMENT_DATA(input), input.positionWS, surface.normal) * surface.ambientOcclusion;

    float3 specColor = lerp(0.219, surface.color, surface.metallic);
    half grazingTerm = saturate((1 - roughness) + (1 - oneMinusReflectivity));

    refl *= ComputeFresnelLerp(specColor, grazingTerm, saturate(dot(surface.normal, -surface.viewDirection)));
	color.xyz += refl * surface.ambientOcclusion + sh;

    return float4(color.xyz, alpha);
	// float2 normalPack = PackNormal(normal);
    // target._GBufferRT0 = float4(base.xyz, metallic);
	// target._GBufferRT1 = float4(normalPack, 0, 1);
	// target._GBufferRT2 = float4(sh, roughness);
	// target._GBufferRT3 = float4(refl, 1);
    // return float4(normal, 1);
    // return float4(tileUV, 0, 1);
}

#endif