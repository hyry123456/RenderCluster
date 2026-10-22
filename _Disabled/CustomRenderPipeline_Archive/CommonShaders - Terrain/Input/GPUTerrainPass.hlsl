#ifndef GPU_TERRAIN_PASS
#define GPU_TERRAIN_PASS

struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
    float4 tangentOS : TANGENT;
    uint instanceID : SV_InstanceID;
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
struct Bounds{
    float3 minPosition;
    float3 maxPosition;
};

float4 _TileData;

TEXTURE2D(_VT_Diffuse);
TEXTURE2D(_VT_Normal);
TEXTURE2D(_VT_PBRData);
TEXTURE2D(_LookUpTexture);
TEXTURE2D(_Blend);

OutVert vert(appdata v) {
    OutVert o;
    RenderPatch patch = PatchList[v.instanceID];
    float2 nodeSize = _NodeSize[patch.lod.z].xz;

    v.vertex.xyz = FixVertex(v.vertex.xyz, patch.lod, v.uv);

    v.vertex.xz *= nodeSize / 64;       //first 8 is divided into 8 pieces, second 8 is one patch 8m
    v.vertex.x += patch.position.x;
    v.vertex.z += patch.position.y;
    float2 worldUV = v.vertex.xz / _TerrainSize.xz;
    
    float heigh = SAMPLE_TEXTURE2D_LOD(_HeighMap, sampler_linear_clamp, worldUV, 0).x * _TerrainSize.y;
    v.vertex.y = heigh;
    v.vertex.xyz += _BeginPosition;

    o.pos = TransformWorldToHClip(v.vertex.xyz);
    o.worldPos = v.vertex.xyz;
    o.worldUV = worldUV;

    half3 viewDirWS = _WorldSpaceCameraPos - o.worldPos;
    viewDirWS = SafeNormalize(viewDirWS);

    // float4 vertexTangent = float4(cross(float3(0, 0, 1), v.normalOS), 1.0);
    // VertexNormalInputs normalInput = GetVertexNormalInputs(v.normalOS, vertexTangent);


    return o;
}


void frag(OutVert input,
        out float4 _GBufferRT0 : SV_Target0,	//rgb:abledo,w:metalness
        out float4 _GBufferRT1 : SV_Target1,	//normal
        out float4 _GBufferRT2 : SV_Target2,	//rgb:emissive,w:roughness
        out float4 _GBufferRT3 : SV_Target3 	//rgb:reflect,w:AO
    )
    {
        // float3 normal = TransformTangentToWorld(float3(0,0,1), half3x3(-input.tangent.xyz, input.bitangent.xyz, input.normal.xyz));
    
        float3 normal = SAMPLE_TEXTURE2D(_NormalMap, sampler_linear_repeat, input.worldUV).xyz * 2 - 1;
        // float3 normal = normalize(input.normal);
        // _GBufferRT0 = float4(normal, 1);
        _GBufferRT0 = float4(1, 1, 1, 0);
        float2 packNormal = PackNormal(normal);
        _GBufferRT1 = float4(packNormal, 0, 1);
        _GBufferRT2 = float4(0,0,0, 1);
        _GBufferRT3 = float4(0, 0, 0, 1);

        // float2 page = floor(input.worldUV * _VTFeedbackParam.x);
        // float2 uvInt = floor(input.worldUV * _VTFeedbackParam.x) / _VTFeedbackParam.x;
        // float4 pageOffset = SAMPLE_TEXTURE2D_LOD(_LookUpTexture, sampler_point_clamp, uvInt, 0) * 255.0;

        // float mipsCount = pow(2, pageOffset.z);
        // float2 pageUV = frac(input.worldUV * _VTFeedbackParam.x);
        // pageUV /= _TileData.x * mipsCount;
        // pageUV += (_TileData.y / mipsCount) * fmod(page, mipsCount);
        // float2 tileUV = pageUV + pageOffset.xy * _TileData.y;
        // float4 color = SAMPLE_TEXTURE2D_LOD(_VT_Diffuse, sampler_linear_repeat, tileUV , 0);
        // float3 normalTex = DecodeNormal(SAMPLE_TEXTURE2D_LOD(_VT_Normal, sampler_linear_repeat, tileUV , 0), 1);
        // float4 pbr_Data = SAMPLE_TEXTURE2D_LOD(_VT_PBRData, sampler_linear_repeat, tileUV , 0);
        // float4 blend = SAMPLE_TEXTURE2D_LOD(_Blend, sampler_linear_repeat, input.worldUV , 0);

        // float3 normal = SampleNormal(input.worldUV);

        // float3 finalNormal = BlendTriplanarNormal(normalTex, normal);

        // float2 normalPack = PackNormal(BlendTriplanarNormal(normalTex, finalNormal));


        // float3 refl = 0;
        // float3 viewDir = normalize(_WorldSpaceCameraPos - input.worldPos);
        // float3 reflect_dir = reflect(-viewDir, finalNormal);
        // float mip = PerceptualRoughnessToMipmapLevel(pbr_Data.g);
	    // half oneMinusReflectivity = (1 - pbr_Data.r) * 0.96;

        // // refl = SamplerReflectProbe0(reflect_dir, mip);
        // refl = ComputeIndirectSpecular(reflect_dir, input.worldPos, mip);

        // half nv = saturate(dot(normal, viewDir));

        // half3 specColor = lerp(0.2, color.xyz, pbr_Data.r);

        // half grazingTerm = saturate((1 - pbr_Data.g) + (1 - oneMinusReflectivity));
        // refl *= ComputeFresnelLerp(specColor, grazingTerm, nv);


        // _GBufferRT0 = float4(color.xyz, 0);
        // _GBufferRT1 = float4(normalPack, 0, 1);
        // _GBufferRT2 = float4(0, 0, 0, 1);
        // _GBufferRT3 = float4(refl, 1);

        // float3 worldPos = input.worldPos;
        // worldPos /= 20;
        // float3 normal = SampleNormal(input.worldUV);
        // TerrainData data = GetTerrainData(worldPos, normal, input.worldUV);

        // normal = data.normal;

        // float2 normalPack = PackNormal(normal);



        // _GBufferRT0 = float4(data.color, data.metallic);
        // _GBufferRT1 = float4(normalPack, 0, 1);
        // _GBufferRT2 = float4(0, 0, 0, data.roughness);
        // _GBufferRT3 = float4(refl, 1);
}


// ShadowVaryings shadowVert(appdata v){
//     ShadowVaryings o;
//     RenderPatch patch = PatchList[v.instanceID];
//     float2 nodeSize = _NodeSize[patch.lod.z].xz;

//     v.vertex.xyz = FixVertex(v.vertex.xyz, patch.lod, v.uv);

//     v.vertex.xz *= nodeSize / 64;       //first 8 is divided into 8 pieces, second 8 is one patch 8m
//     v.vertex.x += patch.position.x;
//     v.vertex.z += patch.position.y;
//     float2 worldUV = v.vertex.xz / _TerrainSize.xz;
    
//     float heigh = SAMPLE_TEXTURE2D_LOD(_HeighMap, sampler_linear_clamp, worldUV, 0).x * _TerrainSize.y;
//     v.vertex.y = heigh;
//     v.vertex.xyz += _BeginPosition;

//     o.pos = TransformWorldToHClip(v.vertex.xyz);
//     return o;
// }

// void shadowFrag(ShadowVaryings i){

// }

SimpleVert boundsVert(appdata v){
    SimpleVert o;
    // Bounds bounds = BoundsList[v.instanceID];

    // float3 size = bounds.maxPosition - bounds.minPosition;
    // v.vertex.xyz *= size;
    // v.vertex.xyz += bounds.minPosition;

    o.pos = TransformWorldToHClip(v.vertex.xyz);
    o.worldPos = v.vertex.xyz;
    
    return o;
}

float4 boundsFrag(SimpleVert i) : SV_TARGET{
    return 1;
}

SimpleVert simpleVert(appdata v){
    SimpleVert o;
    RenderPatch patch = PatchList[v.instanceID];
    float2 nodeSize = _NodeSize[patch.lod.z].xz;

    v.vertex.xyz = FixVertex(v.vertex.xyz, patch.lod, v.uv);

    v.vertex.xz *= nodeSize / 64;       //first 8 is divided into 8 pieces, second 8 is one patch 8m
    v.vertex.x += patch.position.x;
    v.vertex.z += patch.position.y;
    float2 worldUV = v.vertex.xz / _TerrainSize.xz;
    
    float heigh = SAMPLE_TEXTURE2D_LOD(_HeighMap, sampler_linear_clamp, worldUV, 0).x * _TerrainSize.y;
    v.vertex.y = heigh;
    v.vertex.xyz += _BeginPosition;

    o.worldPos = v.vertex.xyz;
    o.pos = TransformWorldToHClip(v.vertex.xyz);
    o.worldUV = worldUV;
    return o;
}


float4 feedbackFrag(SimpleVert input) : SV_TARGET{
    float2 page = floor(input.worldUV * _VTFeedbackParam.x);
    // float2 page = floor(input.worldUV;

	float2 uv = input.worldUV * _VTFeedbackParam.y;
	float2 dx = ddx(uv);
	float2 dy = ddy(uv);
	int mip = clamp(int(0.5 * log2(max(dot(dx, dx), dot(dy, dy))) + 0.5 + _VTFeedbackParam.w), 0, _VTFeedbackParam.z);

	return float4(page / 255.0, mip / 255.0, 1);

	// return float4(page / _VTFeedbackParam.x, mip / 255.0, 1);
}

#endif