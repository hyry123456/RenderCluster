#ifndef CLUSTER_INCLUDE
#define CLUSTER_INCLUDE

struct v2g
{
    uint vertexId   : VertexID;
    uint instanceId : InstanceID;
};

struct g2f
{
    float4  vertex      : SV_POSITION;
    float2  uv          : VAR_UV;
    float2  detailUV    : VAR_DETAIL_UV;
    float2  lightMapuv  : VAR_LIGHT_UV;
    uint2   indexs      : VAR_RENDER_ID;      //x:renderIndex, y:textureId

	float4 TtoW0 : VAR_TT0;
	float4 TtoW1 : VAR_TT1;
	float4 TtoW2 : VAR_TT2;
};

/// <summary>        /// 传递数据到几何着色器中，一个点对应一个四边形        /// </summary>
/// <param name="vertexId">顶点编号</param>
/// <param name="instanceId">簇编号</param>
/// <returns>传输簇编号和顶点编号</returns>
v2g vert(uint vertexId : SV_VertexID, 
    uint instanceId : SV_InstanceID)
{
    v2g o;
    o.vertexId = vertexId;
    // o.instanceId = instanceId;
    o.instanceId = _AppendBuffer[instanceId];
    // o.instanceId = 0;
    return o;
}

g2f CreateWorldPosPoint(uint pointIndex, MeshRenderData data, int renderIndex){
    // ClustersIndicate cluster = _ClustersBuffer[clusterId];
    ClusterPointsData _point = _PointsBuffer[pointIndex];
    // MeshRenderData _render = _RenderDatasBuffer[cluster.renderIndex];

	float3 positionWS = mul(data.objectToWorld, float4(_point.pos.xyz, 1)).xyz;
	float3 normalWS = mul((float3x3)data.objectToWorld, _point.normal.xyz);
	float3 tangentWS = mul((float3x3)data.objectToWorld, _point.tangle.xyz);
	float3 binormalWS = cross(normalWS, tangentWS) * _point.tangle.w;

    g2f o;
    o.vertex = TransformWorldToHClip(positionWS);
    o.uv = _point.uv01.xy * data.uvOffsetScale.zw + data.uvOffsetScale.xy;
    o.detailUV = _point.uv01.xy * data.detailUVOffsetScale.zw + data.detailUVOffsetScale.xy;
    o.indexs = uint2(renderIndex, data.textureId);

    o.lightMapuv = _point.uv01.zw * data.lightMapScaleOffset.xy + data.lightMapScaleOffset.zw;
    // o.lightMapuv = _point.uv1;
    // o.cluIndex = clusterId;

	o.TtoW0 = float4(tangentWS.x, binormalWS.x, normalWS.x, positionWS.x);
	o.TtoW1 = float4(tangentWS.y, binormalWS.y, normalWS.y, positionWS.y);
	o.TtoW2 = float4(tangentWS.z, binormalWS.z, normalWS.z, positionWS.z);

    return o;
}

g2f CreateWorldPosPoint_Shadow(uint pointIndex, MeshRenderData data){
    // ClustersIndicate cluster = _ClustersBuffer[clusterId];
    ClusterPointsData _point = _PointsBuffer[pointIndex];
    // MeshRenderData _render = _RenderDatasBuffer[cluster.renderIndex];

	float3 positionWS = mul(data.objectToWorld, float4(_point.pos.xyz, 1)).xyz;

    g2f o = (g2f)0;
    o.vertex = TransformWorldToHClip(positionWS);

    //在直接光处理时，将超过近平面的直接贴到近平面
#ifdef _DIRECTION_NEAR
    o.vertex.z = min(o.vertex.z, 1);
#endif


    return o;
}

void Insert(inout TriangleStream<g2f> tristream, uint pointBegin, int renderIndex, uint triangleBegin,
    MeshRenderData data){
    uint    offset0 = _TrianglesBuffer[triangleBegin + 0],
            offset1 = _TrianglesBuffer[triangleBegin + 1],
            offset2 = _TrianglesBuffer[triangleBegin + 2];
    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset0, data, renderIndex)
        );
    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset1, data, renderIndex)
        );
    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset2, data, renderIndex)
        );
    tristream.RestartStrip();

    offset0 = _TrianglesBuffer[triangleBegin + 3];
    offset1 = _TrianglesBuffer[triangleBegin + 4];
    offset2 = _TrianglesBuffer[triangleBegin + 5];

    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset0, data, renderIndex)
        );
    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset1, data, renderIndex)
        );
    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset2, data, renderIndex)
        );
    tristream.RestartStrip();
}

//反向插入，渲染背部
void Insert_INV(inout TriangleStream<g2f> tristream, uint pointBegin, int renderIndex, uint triangleBegin,
    MeshRenderData data){
    uint    offset0 = _TrianglesBuffer[triangleBegin + 2],
            offset1 = _TrianglesBuffer[triangleBegin + 1],
            offset2 = _TrianglesBuffer[triangleBegin + 0];
    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset0, data, renderIndex)
        );
    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset1, data, renderIndex)
        );
    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset2, data, renderIndex)
        );
    tristream.RestartStrip();

    offset0 = _TrianglesBuffer[triangleBegin + 5];
    offset1 = _TrianglesBuffer[triangleBegin + 4];
    offset2 = _TrianglesBuffer[triangleBegin + 3];

    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset0, data, renderIndex)
        );
    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset1, data, renderIndex)
        );
    tristream.Append(
        CreateWorldPosPoint(pointBegin + offset2, data, renderIndex)
        );
    tristream.RestartStrip();
}



[maxvertexcount(12)]
void geom(point v2g IN[1], inout TriangleStream<g2f> tristream)
{
    g2f o; 
    uint clusterId = IN[0].instanceId;
    ClustersIndicate cluster = _ClustersBuffer[clusterId];
    // if(cluster.isUsing == 0)
    //     return;
    MeshRenderData data = _RenderDatasBuffer[cluster.renderIndex];
    
    uint triangleIndex = cluster.triangleIndex;
    // uint pointIndex = cluster.pointIndex;
    uint triangleBegin = IN[0].vertexId * 6 + triangleIndex;     //根据顶点编号确定对应指针序列
    Insert(tristream, cluster.pointIndex, cluster.renderIndex, triangleBegin, data);
    if(data.mask.z == 1)
        Insert_INV(tristream, cluster.pointIndex, cluster.renderIndex, triangleBegin, data);
}

void Insert_Shadow(inout TriangleStream<g2f> tristream, uint pointBegin, int renderIndex, uint triangleBegin,
    MeshRenderData data){
    uint    offset0 = _TrianglesBuffer[triangleBegin + 0],
            offset1 = _TrianglesBuffer[triangleBegin + 1],
            offset2 = _TrianglesBuffer[triangleBegin + 2];
    tristream.Append(
        CreateWorldPosPoint_Shadow(pointBegin + offset0, data)
        );
    tristream.Append(
        CreateWorldPosPoint_Shadow(pointBegin + offset1, data)
        );
    tristream.Append(
        CreateWorldPosPoint_Shadow(pointBegin + offset2, data)
        );
    tristream.RestartStrip();

    offset0 = _TrianglesBuffer[triangleBegin + 3];
    offset1 = _TrianglesBuffer[triangleBegin + 4];
    offset2 = _TrianglesBuffer[triangleBegin + 5];

    tristream.Append(
        CreateWorldPosPoint_Shadow(pointBegin + offset0, data)
        );
    tristream.Append(
        CreateWorldPosPoint_Shadow(pointBegin + offset1, data)
        );
    tristream.Append(
        CreateWorldPosPoint_Shadow(pointBegin + offset2, data)
        );
}

// void INV_Insert_Shadow(inout TriangleStream<g2f> tristream, uint clusterId, uint pointIndex, uint triangleBegin){
//     tristream.Append(
//         CreateWorldPosPoint_Shadow(clusterId,
//         pointIndex + _TrianglesBuffer[triangleBegin + 2])
//         );
//     tristream.Append(
//         CreateWorldPosPoint_Shadow(clusterId,
//         pointIndex + _TrianglesBuffer[triangleBegin + 1])
//         );
//     tristream.Append(
//         CreateWorldPosPoint_Shadow(clusterId,
//         pointIndex + _TrianglesBuffer[triangleBegin + 0])
//         );
//     tristream.RestartStrip();


//     tristream.Append(
//         CreateWorldPosPoint_Shadow(clusterId,
//         pointIndex + _TrianglesBuffer[triangleBegin + 5])
//         );
//     tristream.Append(
//         CreateWorldPosPoint_Shadow(clusterId,
//         pointIndex + _TrianglesBuffer[triangleBegin + 4])
//         );
//     tristream.Append(
//         CreateWorldPosPoint_Shadow(clusterId,
//         pointIndex + _TrianglesBuffer[triangleBegin + 3])
//         );
// }

[maxvertexcount(12)]
void geomShadow(point v2g IN[1], inout TriangleStream<g2f> tristream)
{
    g2f o; 
    uint clusterId = IN[0].instanceId;
    ClustersIndicate cluster = _ClustersBuffer[clusterId];
    // if(cluster.isUsing == 0)
    //     return;
    MeshRenderData data = _RenderDatasBuffer[cluster.renderIndex];
    
    uint triangleIndex = cluster.triangleIndex;
    // uint pointIndex = cluster.pointIndex;
    uint triangleBegin = IN[0].vertexId * 6 + triangleIndex;     //根据顶点编号确定对应指针序列

    Insert_Shadow(tristream, cluster.pointIndex, cluster.renderIndex, triangleBegin, data);
    // if(data.mask.z == 1)
    //     Insert_Shadow(tristream, cluster.pointIndex, cluster.renderIndex, triangleBegin, data);

}

struct RVTColors{
    float4 albedo;
    float4 normal;
    float4 pbrData;
    float4 emission;
    float4 detail;
    float4 lightMap;
};

RVTColors GetColor(float2 uv, TextureNode node, float2 detailUV, float2 lightMapUV){
    uint texCount = _RVTTextureData.x * pow(2, node.mipsLevel);
    uv = frac(uv); detailUV = frac(detailUV);
    //首先缩小到一个Tile的大小，然后缩放到数据部分，在加上偏移值
    uv = uv / texCount * _RVTTextureData.z + (float2)_PaddingSizes[node.mipsLevel];                 //得到包含Padding的uv,且还未偏移
    detailUV = detailUV / texCount * _RVTTextureData.z + (float2)_PaddingSizes[node.mipsLevel]; 
    lightMapUV = lightMapUV / texCount * _RVTTextureData.z + (float2)_PaddingSizes[node.mipsLevel]; 

    float tileSize = 1.0f / texCount;

    float2 offset = float2(node.width * tileSize, node.height * tileSize);
    uv = offset + uv;
    detailUV = offset + detailUV;
    lightMapUV = offset + lightMapUV;

#if UNITY_UV_STARTS_AT_TOP
    uv.y = 1.0 - uv.y;
    detailUV.y = 1.0 - detailUV.y;
    lightMapUV.y = 1.0 - lightMapUV.y;
#endif
    RVTColors colors = (RVTColors)1;

    switch(node.mipsLevel){
        case 0:
            colors.albedo   = SAMPLE_TEXTURE2D_LOD(_RVTAlbedo0,     sampler_linear_repeat, uv, 0);
            colors.normal   = SAMPLE_TEXTURE2D_LOD(_RVTNormal0,     sampler_linear_repeat, uv, 0);
            colors.pbrData  = SAMPLE_TEXTURE2D_LOD(_RVTPbrData0,    sampler_linear_repeat, uv, 0);
            colors.emission = SAMPLE_TEXTURE2D_LOD(_RVTEmission0,   sampler_linear_repeat, uv, 0);
            colors.detail   = SAMPLE_TEXTURE2D_LOD(_RVTDetail0,     sampler_linear_repeat, detailUV, 0);
            colors.lightMap   = SAMPLE_TEXTURE2D_LOD(_RVTLightMap0,     sampler_linear_repeat, lightMapUV, 0);

            return colors;
        case 1:
            colors.albedo   = SAMPLE_TEXTURE2D_LOD(_RVTAlbedo1,     sampler_linear_repeat, uv, 0);
            colors.normal   = SAMPLE_TEXTURE2D_LOD(_RVTNormal1,     sampler_linear_repeat, uv, 0);
            colors.pbrData  = SAMPLE_TEXTURE2D_LOD(_RVTPbrData1,    sampler_linear_repeat, uv, 0);
            colors.emission = SAMPLE_TEXTURE2D_LOD(_RVTEmission1,   sampler_linear_repeat, uv, 0);
            colors.detail   = SAMPLE_TEXTURE2D_LOD(_RVTDetail1,     sampler_linear_repeat, detailUV, 0);
            colors.lightMap   = SAMPLE_TEXTURE2D_LOD(_RVTLightMap1,     sampler_linear_repeat, lightMapUV, 0);

            return colors;
        case 2:
            colors.albedo   = SAMPLE_TEXTURE2D_LOD(_RVTAlbedo2,     sampler_linear_repeat, uv, 0);
            colors.normal   = SAMPLE_TEXTURE2D_LOD(_RVTNormal2,     sampler_linear_repeat, uv, 0);
            colors.pbrData  = SAMPLE_TEXTURE2D_LOD(_RVTPbrData2,    sampler_linear_repeat, uv, 0);
            colors.emission = SAMPLE_TEXTURE2D_LOD(_RVTEmission2,   sampler_linear_repeat, uv, 0);
            colors.detail   = SAMPLE_TEXTURE2D_LOD(_RVTDetail2,     sampler_linear_repeat, detailUV, 0);
            colors.lightMap   = SAMPLE_TEXTURE2D_LOD(_RVTLightMap2,     sampler_linear_repeat, lightMapUV, 0);

            return colors;
        case 3:
            colors.albedo   = SAMPLE_TEXTURE2D_LOD(_RVTAlbedo3,     sampler_linear_repeat, uv, 0);
            colors.normal   = SAMPLE_TEXTURE2D_LOD(_RVTNormal3,     sampler_linear_repeat, uv, 0);
            colors.pbrData  = SAMPLE_TEXTURE2D_LOD(_RVTPbrData3,    sampler_linear_repeat, uv, 0);
            colors.emission = SAMPLE_TEXTURE2D_LOD(_RVTEmission3,   sampler_linear_repeat, uv, 0);
            colors.detail   = SAMPLE_TEXTURE2D_LOD(_RVTDetail3,     sampler_linear_repeat, detailUV, 0);
            colors.lightMap   = SAMPLE_TEXTURE2D_LOD(_RVTLightMap3,     sampler_linear_repeat, lightMapUV, 0);

            return colors;
        case 4:
            colors.albedo   = SAMPLE_TEXTURE2D_LOD(_RVTAlbedo4,     sampler_linear_repeat, uv, 0);
            colors.normal   = SAMPLE_TEXTURE2D_LOD(_RVTNormal4,     sampler_linear_repeat, uv, 0);
            colors.pbrData  = SAMPLE_TEXTURE2D_LOD(_RVTPbrData4,    sampler_linear_repeat, uv, 0);
            colors.emission = SAMPLE_TEXTURE2D_LOD(_RVTEmission4,   sampler_linear_repeat, uv, 0);
            colors.detail   = SAMPLE_TEXTURE2D_LOD(_RVTDetail4,     sampler_linear_repeat, detailUV, 0);
            colors.lightMap   = SAMPLE_TEXTURE2D_LOD(_RVTLightMap4,     sampler_linear_repeat, lightMapUV, 0);
            return colors;
        case 5:
            colors.albedo   = SAMPLE_TEXTURE2D_LOD(_RVTAlbedo5,     sampler_linear_repeat, uv, 0);
            colors.normal   = SAMPLE_TEXTURE2D_LOD(_RVTNormal5,     sampler_linear_repeat, uv, 0);
            colors.pbrData  = SAMPLE_TEXTURE2D_LOD(_RVTPbrData5,    sampler_linear_repeat, uv, 0);
            colors.emission = SAMPLE_TEXTURE2D_LOD(_RVTEmission5,   sampler_linear_repeat, uv, 0);
            colors.detail   = SAMPLE_TEXTURE2D_LOD(_RVTDetail5,     sampler_linear_repeat, detailUV, 0);
            colors.lightMap   = SAMPLE_TEXTURE2D_LOD(_RVTLightMap5,     sampler_linear_repeat, lightMapUV, 0);
            return colors;
        case 6:
            colors.albedo   = SAMPLE_TEXTURE2D_LOD(_RVTAlbedo6,     sampler_linear_repeat, uv, 0);
            colors.normal   = SAMPLE_TEXTURE2D_LOD(_RVTNormal6,     sampler_linear_repeat, uv, 0);
            colors.pbrData  = SAMPLE_TEXTURE2D_LOD(_RVTPbrData6,    sampler_linear_repeat, uv, 0);
            colors.emission = SAMPLE_TEXTURE2D_LOD(_RVTEmission6,   sampler_linear_repeat, uv, 0);
            colors.detail   = SAMPLE_TEXTURE2D_LOD(_RVTDetail6,     sampler_linear_repeat, detailUV, 0);
            colors.lightMap   = SAMPLE_TEXTURE2D_LOD(_RVTLightMap6,     sampler_linear_repeat, lightMapUV, 0);
            return colors;
        case 7:
            colors.albedo   = SAMPLE_TEXTURE2D_LOD(_RVTAlbedo7,     sampler_linear_repeat, uv, 0);
            colors.normal   = SAMPLE_TEXTURE2D_LOD(_RVTNormal7,     sampler_linear_repeat, uv, 0);
            colors.pbrData  = SAMPLE_TEXTURE2D_LOD(_RVTPbrData7,    sampler_linear_repeat, uv, 0);
            colors.emission = SAMPLE_TEXTURE2D_LOD(_RVTEmission7,   sampler_linear_repeat, uv, 0);
            colors.detail   = SAMPLE_TEXTURE2D_LOD(_RVTDetail7,     sampler_linear_repeat, detailUV, 0);
            colors.lightMap   = SAMPLE_TEXTURE2D_LOD(_RVTLightMap7,     sampler_linear_repeat, lightMapUV, 0);
            return colors;

        case 8:
            colors.albedo   = SAMPLE_TEXTURE2D_LOD(_RVTAlbedo8,     sampler_linear_repeat, uv, 0);
            colors.normal   = SAMPLE_TEXTURE2D_LOD(_RVTNormal8,     sampler_linear_repeat, uv, 0);
            colors.pbrData  = SAMPLE_TEXTURE2D_LOD(_RVTPbrData8,    sampler_linear_repeat, uv, 0);
            colors.emission = SAMPLE_TEXTURE2D_LOD(_RVTEmission8,   sampler_linear_repeat, uv, 0);
            colors.detail   = SAMPLE_TEXTURE2D_LOD(_RVTDetail8,     sampler_linear_repeat, detailUV, 0);
            colors.lightMap   = SAMPLE_TEXTURE2D_LOD(_RVTLightMap8,     sampler_linear_repeat, lightMapUV, 0);
            return colors;

        // case 9:
        //     colors.albedo   = SAMPLE_TEXTURE2D_LOD(_RVTAlbedo9,     sampler_linear_repeat, uv, 0);
        //     colors.normal   = SAMPLE_TEXTURE2D_LOD(_RVTNormal9,     sampler_linear_repeat, uv, 0);
        //     colors.pbrData  = SAMPLE_TEXTURE2D_LOD(_RVTPbrData9,    sampler_linear_repeat, uv, 0);
        //     colors.emission = SAMPLE_TEXTURE2D_LOD(_RVTEmission9,   sampler_linear_repeat, uv, 0);
        //     colors.detail   = SAMPLE_TEXTURE2D_LOD(_RVTDetail9,     sampler_linear_repeat, detailUV, 0);
        //     return colors;
        default:
            return colors;
    }
}

float GetShadowAlpha(float2 uv, TextureNode node){
    uint texCount = _RVTTextureData.x * pow(2, node.mipsLevel);
    uv = frac(uv); 
    uv = uv / texCount * _RVTTextureData.z + (float2)_PaddingSizes[node.mipsLevel];                 //得到包含Padding的uv,且还未偏移

    float tileSize = 1.0f / texCount;

    uv = float2(node.width * tileSize, node.height * tileSize) + uv;

#if UNITY_UV_STARTS_AT_TOP
    uv.y = 1.0 - uv.y;
#endif
    switch(node.mipsLevel){
        case 0:
            return SAMPLE_TEXTURE2D_LOD(_RVTAlbedo0,     sampler_linear_repeat, uv, 0).w;
        case 1:
            return SAMPLE_TEXTURE2D_LOD(_RVTAlbedo1,     sampler_linear_repeat, uv, 0).w;

        case 2:
            return SAMPLE_TEXTURE2D_LOD(_RVTAlbedo2,     sampler_linear_repeat, uv, 0).w;

        case 3:
            return SAMPLE_TEXTURE2D_LOD(_RVTAlbedo3,     sampler_linear_repeat, uv, 0).w;

        case 4:
            return SAMPLE_TEXTURE2D_LOD(_RVTAlbedo4,     sampler_linear_repeat, uv, 0).w;

        case 5:
            return SAMPLE_TEXTURE2D_LOD(_RVTAlbedo5,     sampler_linear_repeat, uv, 0).w;

        case 6:
            return SAMPLE_TEXTURE2D_LOD(_RVTAlbedo6,     sampler_linear_repeat, uv, 0).w;

        case 7:
            return SAMPLE_TEXTURE2D_LOD(_RVTAlbedo7,     sampler_linear_repeat, uv, 0).w;
        case 8:
            return SAMPLE_TEXTURE2D_LOD(_RVTAlbedo8,     sampler_linear_repeat, uv, 0).w;
        // case 9:
        //     return SAMPLE_TEXTURE2D_LOD(_RVTAlbedo9,     sampler_linear_repeat, uv, 0).w;
        default:
            return 0;
    }
}


RVTColors LerpColors(RVTColors color1, RVTColors color2, float radio){
    // radio = saturate(radio);
    // color1.albedo   = color1.albedo     * radio + color2.albedo     * (1.0 - radio);
    // color1.normal   = color1.normal     * radio + color2.normal     * (1.0 - radio);
    // color1.pbrData  = color1.pbrData    * radio + color2.pbrData    * (1.0 - radio);
    // color1.emission = color1.emission   * radio + color2.emission   * (1.0 - radio);
    // color1.detail   = color1.detail     * radio + color2.detail     * (1.0 - radio);


    color1.albedo   = color1.albedo     * (1.0 - radio) + color2.albedo     * radio;
    color1.normal   = color1.normal     * (1.0 - radio) + color2.normal     * radio;
    color1.pbrData  = color1.pbrData    * (1.0 - radio) + color2.pbrData    * radio;
    color1.emission = color1.emission   * (1.0 - radio) + color2.emission   * radio;
    color1.detail   = color1.detail     * (1.0 - radio) + color2.detail     * radio;
    color1.lightMap = color1.lightMap   * (1.0 - radio) + color2.lightMap   * radio;

    // color1.albedo   = radio;
    // color1.normal   = radio;
    // color1.pbrData  = radio;
    // color1.emission = radio;
    // color1.detail   = radio;
    return color1;
}

float4 GetAlbedo(RVTColors colors, float3 detailData, float4 mainColor){
    float4 map = colors.albedo;
	float4 color = mainColor;

    float detail = colors.detail.x * detailData.x * colors.pbrData.z;   //detail albedo值 * detail albedo设置值 * detail mask贴图值
    // float mask = colors.pbrData.z;
    map.rgb = map.rgb * (1.0 - detail) + map.rgb * map.rgb * detail;

	return map * color;
}

float3 GetNormalTS (RVTColors colors, float3 detailData, float normalScale) {
	float4 map = colors.normal;
	// RVT 图集法线是「法线贴图原样拷贝」，用 DecodeNormal 解码。
	float3 normal = DecodeNormal(map, normalScale);
	normal.y *= -1.0;

	// detail 法线链路【禁用】——同 ClusterInput 的说明（fallback 数据让 RNM 除零 NaN）
    //map = float4(colors.detail.w, colors.detail.y, 1, 1);
    //normalScale = detailData.y * colors.pbrData.z;
    //float3 detail = DecodeNormal(map, normalScale);
    return saturate(normal);
}

//这个部分估计有问题，但是比较少用，没影响就行
float GetRoughness (RVTColors colors, float3 detailData, float smooth) {
	float roughness = 1.0 - smooth * colors.pbrData.w;

    float detailRou = 1.0 - colors.detail.z;    //detail贴图的粗糙度
    float detail = detailData.z * colors.pbrData.z; //detial 光滑度的影响程度 * detail mask贴图值
    roughness = roughness * (1.0 - detail) + detail * detailRou;

	return roughness;
}

inline half3 SamplerBaseReflect(half3 refDir, float mip_Level)
{
	float4 environment = SAMPLE_TEXTURECUBE_LOD(
		_BaseCubeMap, sampler_BaseCubeMap, refDir, mip_Level
	);
	return environment.xyz;
}

struct Varyings {
    float4 positionCS_SS : SV_POSITION;
    float2 screenUV : VAR_SCREEN_UV;
};

struct Attributes{
	float3 positionOS : POSITION;
	float2 baseUV : TEXCOORD0;
};


Varyings BlitPassSimpleVertex(Attributes input){
    Varyings output = (Varyings)0;
    output.positionCS_SS = TransformObjectToHClip(input.positionOS);
    output.screenUV = input.baseUV;
    return output;
}

sampler2D _MainTex;
float4 _MainTex_TexelSize;

float4 GetMaxFeedback(float2 uv, int count)
{
	float4 col = float4(1, 1, 1, 1);
	for (int y = 0; y < count; y++)
	{
		for (int x = 0; x < count; x++)
		{
			float4 col1 = tex2D(_MainTex, uv + float2(_MainTex_TexelSize.x * x, _MainTex_TexelSize.y * y));
			col = lerp(col, col1, step(col1.b, col.b));
		}
	}
	return col;
}



#endif
