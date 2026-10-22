#ifndef PARTICLE_DATA_INPUT
#define PARTICLE_DATA_INPUT

#define _PI 3.1415926
#define random(seed) sin(seed * 641.5467987313875 + 1.943856175)

float4 _Time;           //x=time, y=deltaTime, z=fixDeltaTime
uint _Row, _Column;     //uv动画需要的行和列


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

struct IndexInput{
    uint3 id            : SV_DispatchThreadID;
    uint3 groupId       : SV_GroupID;
    uint3 groupThreadId : SV_GroupThreadID;
};




/// <summary> /// 输入到粒子中的碰撞器 /// </summary>
struct CollsionStruct
{
    float radius;        //用正负判断碰撞类型
    float3 offset;      //碰撞偏移，只从中间往四周偏移
    int mode;           //碰撞器形状模式
    float4x4 localToWorld;
    float4x4 worldToLocal;
};

StructuredBuffer<CollsionStruct> _CollsionBuffer;       //碰撞体数据存储buffer
uint _CollsionCount;                                    //碰撞体数量
float _CollsionScale;                                   //碰撞影响强度

//计算UV偏移的结构体
struct AnimateUVData {
    float4 uvData;			
    float interpolation;
};

//计算uv偏移数据
AnimateUVData AnimateUV(float time_01) {
    float sumTime = _Row * _Column;
    float midTime = time_01 * sumTime;

    float bottomTime = floor(midTime);
    float topTime = bottomTime + 1.0;
    float interpolation = (midTime - bottomTime) / (topTime - bottomTime);
    float bottomRow = floor(bottomTime / _Row);
    float bottomColumn = floor(bottomTime - bottomRow * _Column);
    float topRow = floor(topTime /_Row);
    float topColumn = floor(topTime - topRow * _Column);

    AnimateUVData animateUV;
    animateUV.uvData = float4(bottomColumn, -bottomRow, topColumn, -topRow);
    animateUV.interpolation = interpolation;
    return animateUV;
}


bool CheckCollsion(inout ParticleData input){
    bool re = false;
    for(uint i = 0; i < _CollsionCount; i++){
        CollsionStruct collider = _CollsionBuffer[i];
        switch(collider.mode){
            case 1:     //球型模式
                float3 duration = mul(collider.worldToLocal, float4(input.worldPos, 1)).xyz;
                float len = length(duration);
                if(len <= collider.radius){
                    float3 forceDir = normalize(duration);
                    float3 objSpeed = mul((float3x3)collider.worldToLocal, input.nowSpeed);
                    objSpeed = dot(-forceDir, objSpeed) * forceDir;
                    input.nowSpeed += mul((float3x3)collider.localToWorld, objSpeed) * _CollsionScale;
                    float3 objPos = normalize(duration) * collider.radius;
                    input.worldPos = mul(collider.localToWorld, float4(objPos, 1)).xyz;
                    re = true;
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

                re = true;

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
                        speed.x = -speed.x * _CollsionScale;
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
                        speed.z = -speed.z * _CollsionScale;
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
                        speed.y = -speed.y * _CollsionScale;
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
                        speed.z = -speed.z * _CollsionScale;
                        input.nowSpeed = mul((float3x3)collider.localToWorld, speed);
                    }
                }
                break;
        }

    }
    return re;
}

#endif
