#ifndef GPU_DRAVIN_PARTICLE_FACTORY_INCLUDE
#define GPU_DRAVIN_PARTICLE_FACTORY_INCLUDE

/// 粒子工厂的粒子组，用来定义一组粒子的数据
struct FactoryNode
{
    float4x4 projectMatrix;    //旋转矩阵
    //xyz：初始速度，w:其他模式添加的力的强度
    float4 beginSpeed;  
    float arc;    //角度
    //x:大小(半径以及边长，形状靠矩阵),y:频率，z：强度，w：采样次数
    float4 size_Noises;
    //大小范围，x-y:大小范围，z-w:速度映射范围
    float4 sizeRange;
    //生命周期：x:结束时间，y:持续时间,z:释放时间
    float4 lifeTime;
    //初始化模式：x:形状, y:速度，z：是否使用重力
    int3 initlizeMode;
    //渲染设置，x:贴图编号，y:Gradient编号, z:大小曲线编号
    int3 drawSetting;
    //附加设置，x:是否跟随速度
    int3 showSetting;
};


/// 用来确定单个Gradient的数据用的结构体
struct GradientData
{
    int colorBegin;      //颜色起始位置
    int colorEnd;        //颜色结束位置
    int alphaBegin;      //透明度起始
    int alphaEnd;        //透明度终点
};

/// 用来确定单个曲线的结构体
struct CurveData
{
    int curveBegin;
    int curveEnd;
};

#include "ParticleNoiseInc.hlsl"


RWStructuredBuffer<FactoryNode> nodesBuffer;
RWStructuredBuffer<NoiseParticleData> particlesBuffer;
RWStructuredBuffer<GradientData> GradientDataBuffer;
RWStructuredBuffer<float4> colorsBuffer;
RWStructuredBuffer<float2> alphasBuffer;
RWStructuredBuffer<CurveData> curvesBuffer;
RWStructuredBuffer<float4> keyframesBuffer;
RWStructuredBuffer<int2> textureDatasBuffer;

float3 GetCubeBeginPos(float3 random, float cubeSize){
    float3 begin = -cubeSize/2.0;
    float3 end = cubeSize/2.0;
    float3 pos = lerp(begin, end, random);
    return pos;
}

//初始化组位置，也就是确定该组粒子的起始位置
void ProjectPosition(inout NoiseParticleData particle, FactoryNode node){

    //组中的粒子不需要进行矩阵的大小调整，矩阵只控制组的初始化
    switch(node.initlizeMode.x){
        case 1:
            particle.worldPos = mul(node.projectMatrix, 
                float4(GetSphereBeginPos(particle.random.yx, node.arc, node.size_Noises.x), 1)).xyz;
            return;
        case 2:
            particle.worldPos = mul(node.projectMatrix, 
                float4(GetCubeBeginPos(particle.random.yzx, node.size_Noises.x), 1)).xyz;
            return;
        default:
            particle.worldPos =  mul(node.projectMatrix, float4(0, 0, 0, 1)).xyz;
            return;
    }
}

//获得初始速度
void CaculateSpeed(inout NoiseParticleData particle, FactoryNode node){

    float speed = node.beginSpeed.w;    //根据速度大小确定一个随机速度
    float3 normal = normalize(node.beginSpeed.xyz);
    float3 direct = normalize(particle.worldPos - mul(node.projectMatrix, float4(0, 0, 0, 1)).xyz);
    switch(node.initlizeMode.y){
        case 1:     //速度是法线以及大小，在粒子位置生成一个垂直于法线且是向外的力度
            particle.nowSpeed = node.beginSpeed.xyz + normalize( (direct - normal * dot(direct, normal)) ) * speed;
            return;
        case 2:     //在粒子位置生成一个垂直于法线且是向内的力度
            particle.nowSpeed = node.beginSpeed.xyz - normalize( (direct - normal * dot(direct, normal)) ) * speed;
            return;
        case 3:     //朝向起始位置的速度，也就是往中间汇集
            particle.nowSpeed = node.beginSpeed.xyz - direct * speed;
            return;
        case 4:     //离开起始位置的速度，也就是从中间往外面跑
            particle.nowSpeed = node.beginSpeed.xyz + direct * speed;
            return;
        default: //默认模式,传入速度就是初始化速度
            particle.nowSpeed = node.beginSpeed.xyz + node.beginSpeed.xyz;
            return;
    }
}

void InitializeParticle(inout NoiseParticleData particle, FactoryNode node){
    ProjectPosition(particle, node);    //确定位置
    CaculateSpeed(particle, node);
    particle.index.y = 1;
    particle.random.w = 0;
}

void CommonUpdate(inout NoiseParticleData particle){
    particle.worldPos += particle.nowSpeed * _Time.y;
    particle.random.w += _Time.y;
}

float3 CaculateColor(float time, int begin, int end){
    for(int i = begin + 1; i < end; i++){
        if(colorsBuffer[i].w >= time){
            float radio = smoothstep(colorsBuffer[i - 1].w, colorsBuffer[i].w, time);
            return lerp(colorsBuffer[i - 1].xyz, colorsBuffer[i].xyz, radio);
        }
    }
    return 0;
}

float CaculateAlpha(float time, int begin, int end){
    for(int i = begin + 1; i < end; i++){
        if(alphasBuffer[i].y >= time){
            float radio = smoothstep(alphasBuffer[i - 1].y, alphasBuffer[i].y, time);
            return lerp(alphasBuffer[i - 1].x, alphasBuffer[i].x, radio);
        }
    }
    return 0;
}

//时间控制函数，用来读取Curve中的值
float CaculateSize(float time, int begin, int end) {
    //有数据才循环
    for (int i = begin + 1; i < end; i++) {
        //找到在范围中的
        if (time <= keyframesBuffer[i].x) {
            //Unity的Curve的曲线本质上是一个三次多项式插值，公式为：y = ax^3 + bx^2 + cx +d
            float a = (keyframesBuffer[i - 1].w + keyframesBuffer[i].z) * (keyframesBuffer[i].x - keyframesBuffer[i - 1].x)
                - 2 * (keyframesBuffer[i].y - keyframesBuffer[i - 1].y);
            float b = (-2 * keyframesBuffer[i - 1].w - keyframesBuffer[i].z) *
                (keyframesBuffer[i].x - keyframesBuffer[i - 1].x) + 3 * (keyframesBuffer[i].y - keyframesBuffer[i - 1].y);
            float c = keyframesBuffer[i - 1].w * (keyframesBuffer[i].x - keyframesBuffer[i - 1].x);
            float d = keyframesBuffer[i - 1].y;

            float trueTime = (time - keyframesBuffer[i - 1].x) / (keyframesBuffer[i].x - keyframesBuffer[i - 1].x);
            return a * pow(trueTime, 3) + b * pow(trueTime, 2) + c * trueTime + d;
        }
    }
    return 0;
}

AnimateUVData AnimateUV(float time_01, int row, int col) {
    float sumTime = row * col;
    float midTime = time_01 * sumTime;

    float bottomTime = floor(midTime);
    float topTime = bottomTime + 1.0;
    float interpolation = (midTime - bottomTime) / (topTime - bottomTime);
    float bottomRow = floor(bottomTime / row);
    float bottomColumn = floor(bottomTime - bottomRow * col);
    float topRow = floor(topTime / row);
    float topColumn = floor(topTime - topRow * col);

    AnimateUVData animateUV;
    animateUV.uvData = float4(bottomColumn, -bottomRow, topColumn, -topRow);
    animateUV.interpolation = interpolation;
    return animateUV;
}

//计算粒子颜色等数据
void CaculateSurface(inout NoiseParticleData particle, FactoryNode node){
    float time_01 = particle.random.w / node.lifeTime.y;
    GradientData gradient = GradientDataBuffer[node.drawSetting.y];
    particle.color = float4(CaculateColor(time_01, gradient.colorBegin, gradient.colorEnd),
        CaculateAlpha(time_01, gradient.alphaBegin, gradient.alphaEnd));
    CurveData curve = curvesBuffer[node.drawSetting.z];
    particle.size = lerp(node.sizeRange.x, node.sizeRange.y, CaculateSize(time_01, curve.curveBegin, curve.curveEnd));

    int2 textureData = textureDatasBuffer[node.drawSetting.x];
    AnimateUVData animateUV = AnimateUV(time_01, textureData.x, textureData.y);
    particle.uvTransData = animateUV.uvData;
    particle.interpolation = animateUV.interpolation;
}

bool CheckFactoryCollsion(inout NoiseParticleData input){
    for(uint i = 0; i < _CollsionData; i++){
        CollsionStruct collider = _CollsionBuffer[i];
        switch(collider.mode){
            case 1:     //球型模式
                float3 duration = mul(collider.worldToLocal, float4(input.worldPos, 1)).xyz;
                float len = length(duration);
                if(len <= collider.radius){
                    float3 forceDir = normalize(duration);
                    float3 objSpeed = mul((float3x3)collider.worldToLocal, input.nowSpeed);
                    objSpeed = dot(forceDir, objSpeed);
                    input.nowSpeed = mul((float3x3)collider.localToWorld, objSpeed);
                    float3 objPos = normalize(duration) *  collider.radius;
                    input.worldPos = mul(collider.localToWorld, float4(objPos, 1));
                    return true;
                }
                break;
            default:    //盒子碰撞
                //判断是否超过盒子范围
                float3 boxMax = collider.offset;
                float3 boxMin = -collider.offset;
                float3 currentPos = mul(collider.worldToLocal, float4(input.worldPos, 1)).xyz;
                float3 absDir = 0;
                if(currentPos.x < boxMax.x){
                    if(currentPos.x < boxMin.x)
                        break;
                    absDir.x = collider.offset.x - abs(currentPos.x);
                }
                else break;
                if(currentPos.y < boxMax.y){
                    if(currentPos.y < boxMin.y)
                        break;
                    absDir.y = collider.offset.y - abs(currentPos.y);
                }
                else break;
                if(currentPos.z < boxMax.z){
                    if(currentPos.z < boxMin.z)
                        break;
                    absDir.z = collider.offset.z - abs(currentPos.z);
                }
                else break;

                if(absDir.x < absDir.y){        //x小于y
                    if(absDir.x < absDir.z){    //x小于z
                        if(currentPos.x < 0){
                            currentPos.x = boxMin.x;
                        }
                        else{
                            currentPos.x = boxMax.x;
                        }
                        input.worldPos = mul(collider.localToWorld, float4(currentPos, 1)).xyz;
                        float3 speed = mul((float3x3)collider.worldToLocal, input.nowSpeed.xyz);
                        speed.x = -speed.x;
                        input.nowSpeed = mul((float3x3)collider.localToWorld, speed);
                    }
                    else{               //z小于x
                        if(currentPos.z < 0){
                            currentPos.z = boxMin.z;
                        }
                        else{
                            currentPos.z = boxMax.z;
                        }
                        input.worldPos = mul(collider.localToWorld, float4(currentPos, 1)).xyz;

                        float3 speed = mul((float3x3)collider.worldToLocal, input.nowSpeed.xyz);
                        speed.z = -speed.z;
                        input.nowSpeed = mul((float3x3)collider.localToWorld, speed);
                    }
                }
                else{           //y小于x
                    if(absDir.y < absDir.z){    //y小于z
                        if(currentPos.y < 0){
                            currentPos.y = boxMin.y;
                        }
                        else{
                            currentPos.y = boxMax.y;
                        }
                        input.worldPos = mul(collider.localToWorld, float4(currentPos, 1)).xyz;

                        float3 speed = mul((float3x3)collider.worldToLocal, input.nowSpeed.xyz);
                        speed.y = -speed.y;
                        input.nowSpeed = mul((float3x3)collider.localToWorld, speed);
                    }
                    else{               //z小于y
                        if(currentPos.z < 0){
                            currentPos.z = boxMin.z;
                        }
                        else{
                            currentPos.z = boxMax.z;
                        }
                        input.worldPos = mul(collider.localToWorld, float4(currentPos, 1)).xyz;
                        float3 speed = mul((float3x3)collider.worldToLocal, input.nowSpeed.xyz);
                        speed.z = -speed.z;
                        input.nowSpeed = mul((float3x3)collider.localToWorld, speed);
                    }
                }
                return true;
        }
    }
    return false;
}


void FixFrameUpdate(inout NoiseParticleData particle, FactoryNode node){
    particle.worldPos += particle.nowSpeed * _Time.z;
    particle.nowSpeed += CurlNoise3D(particle.worldPos * particle.random.xyz * node.size_Noises.y, (int)node.size_Noises.w) * node.size_Noises.z * _Time.z;
    CheckFactoryCollsion(particle);
}

#endif