#ifndef PARTICLE_SYSTEM_INPUT_SHADER
#define PARTICLE_SYSTEM_INPUT_SHADER


//单个粒子需要的数据
struct ParticleData {
    float4 random;          //xyz是随机数，w是目前存活时间
    uint4 index;            //状态标记，x是运行标记(0未运行，1运行)，yzw可以拓展
    float3 worldPos;        //当前位置
    float4 uvTransData;     //uv动画需要的数据
    float interpolation;    //插值需要的数据
    float4 color;           //颜色值，包含透明度
    float size;             //粒子大小
    float3 nowSpeed;        //xyz是当前速度以及方向
};

StructuredBuffer<ParticleData> _ParticlesBuffer;
// StructuredBuffer<uint> _RuntimeParticleIndexBuffer;           //运行中的粒子的编号Buffer
StructuredBuffer<int> _ParticlesIndexsBuffer;

struct PlaneData{
    float3 positionWS[4];
    float4 positionCS[4];
};


//根据粒子位置直接生成一个平面的4个点，顺序是：左下、右下、左上、右上
//particle:粒子数据,  aspectRatio:宽高比
PlaneData PointToPlane(ParticleData particle, float aspectRatio)
{
    // float3 o[4] = (float3[4])0;
    PlaneData plane = (PlaneData)0;

    float3 worldVer = particle.worldPos;
    float paritcleLen = particle.size;

    float3 worldPos = worldVer + unity_MatrixV[0].xyz * -paritcleLen + unity_MatrixV[1].xyz * -paritcleLen * aspectRatio;
    plane.positionWS[0] = worldPos;
    plane.positionCS[0] = mul(UNITY_MATRIX_VP, float4(worldPos, 1));

    worldPos = worldVer + UNITY_MATRIX_V[0].xyz * -paritcleLen + UNITY_MATRIX_V[1].xyz * paritcleLen * aspectRatio;
    plane.positionWS[1] = worldPos;
    plane.positionCS[1] = mul(UNITY_MATRIX_VP, float4(worldPos, 1));

    worldPos = worldVer + UNITY_MATRIX_V[0].xyz * paritcleLen + UNITY_MATRIX_V[1].xyz * -paritcleLen * aspectRatio;
    plane.positionWS[2] = worldPos;
    plane.positionCS[2] = mul(UNITY_MATRIX_VP, float4(worldPos, 1));

    worldPos = worldVer + UNITY_MATRIX_V[0].xyz * paritcleLen + UNITY_MATRIX_V[1].xyz * paritcleLen * aspectRatio;
    plane.positionWS[3] = worldPos;
    plane.positionCS[3] = mul(UNITY_MATRIX_VP, float4(worldPos, 1));
    return plane;
}


//同上，生成1个面，但是这个面的方向跟随速度
PlaneData PointToPlane_FollowSpeed(ParticleData particle, float aspectRatio) 
{
    PlaneData plane = (PlaneData)0;

    float3 worldVer = particle.worldPos;
    float paritcleLen = particle.size;

    float3 viewDir = normalize( _WorldSpaceCameraPos - worldVer );
    float3 upDir = normalize(particle.nowSpeed), particleNormal = cross(viewDir, upDir);
    //左下
    float3 worldPos = worldVer + -upDir * paritcleLen + -particleNormal * paritcleLen * aspectRatio;
    plane.positionWS[0] = worldPos;
    plane.positionCS[0] = TransformWorldToHClip(worldPos);

    worldPos = worldVer + -upDir * paritcleLen + particleNormal * paritcleLen * aspectRatio;
    plane.positionWS[1] = worldPos;
    plane.positionCS[1] = TransformWorldToHClip(worldPos);

    worldPos = worldVer + upDir * paritcleLen + -particleNormal * paritcleLen * aspectRatio;
    plane.positionWS[2] = worldPos;
    plane.positionCS[2] = TransformWorldToHClip(worldPos);

    worldPos = worldVer + upDir * paritcleLen + particleNormal * paritcleLen * aspectRatio;
    plane.positionWS[3] = worldPos;
    plane.positionCS[3] = TransformWorldToHClip(worldPos);
    return plane;
}

//计算uv，包含uv动画
float4 GetUV(float2 uv, float4 uvTransData, uint rowCount, uint colCount) {
    float4 reUV;
    reUV.xy = uv + uvTransData.xy;
    reUV.zw = uv + uvTransData.zw;

    reUV.xz /= rowCount;
    reUV.yw /= colCount;
    return reUV;
}


struct FragInput
{
    float4 color : VAR_COLOR;
    float4 pos : SV_POSITION;
    float4 uv : TEXCOORD0;
    float interpolation : UV_INTERPELATION;
    float3 positionWS : VAR_POSITION;
};


//封装点生成面
void PointToPlane(inout TriangleStream<FragInput> tristream, ParticleData particle, float texAspectRatio)
{
    FragInput o[4] = (FragInput[4])0;

    float3 worldVer = particle.worldPos;
    float paritcleLen = particle.size;

    float3 worldPos = worldVer + -unity_MatrixV[0].xyz * paritcleLen + -unity_MatrixV[1].xyz * paritcleLen * texAspectRatio;
    o[0].pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1));
    o[0].color = particle.color;
    // o[0].uv = GetUV(float2(0, 0), particle.uvTransData);
    o[0].interpolation = particle.interpolation;
    o[0].positionWS = worldPos;

    worldPos = worldVer + UNITY_MATRIX_V[0].xyz * -paritcleLen
        + UNITY_MATRIX_V[1].xyz * paritcleLen * texAspectRatio;
    o[1].pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1));
    o[1].color = particle.color;
    // o[1].uv = GetUV(float2(1, 0), particle.uvTransData);
    o[1].interpolation = particle.interpolation;
    o[1].positionWS = worldPos;

    worldPos = worldVer + UNITY_MATRIX_V[0].xyz * paritcleLen
        + UNITY_MATRIX_V[1].xyz * -paritcleLen * texAspectRatio;
    o[2].pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1));
    o[2].color = particle.color;
    // o[2].uv = GetUV(float2(0, 1), particle.uvTransData);
    o[2].interpolation = particle.interpolation;
    o[2].positionWS = worldPos;

    worldPos = worldVer + UNITY_MATRIX_V[0].xyz * paritcleLen
        + UNITY_MATRIX_V[1].xyz * paritcleLen * texAspectRatio;
    o[3].pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1));
    o[3].color = particle.color;
    // o[3].uv = GetUV(float2(1, 1), particle.uvTransData);
    o[3].interpolation = particle.interpolation;
    o[3].positionWS = worldPos;

    tristream.Append(o[1]);
    tristream.Append(o[2]);
    tristream.Append(o[0]);
    tristream.RestartStrip();

    tristream.Append(o[1]);
    tristream.Append(o[3]);
    tristream.Append(o[2]);
    tristream.RestartStrip();
}



#endif
