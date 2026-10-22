#ifndef PARTICLE_SYSTEM_INPUT
#define PARTICLE_SYSTEM_INPUT

#include "ParticleDataInput.hlsl"

RWStructuredBuffer<ParticleData> _ParticlesBuffer;
RWStructuredBuffer<int> _ParticlesIndexsBuffer;

uint _CurrentIndex;         //当前的编号，不包含当前值
uint _ReleaseCount;         //释放的数量
uint _ParticlesCount;       //所有粒子的数量
int _SortIndexOffset;       //排序的偏移，实现交叉异步排序
int2 _SortSize;              //每次排序的数量, y:偏移大小
float3 _CameraPos;          //摄像机的世界坐标

void InitializeParticle(inout ParticleData particle);          //粒子初始化
bool CheckParticleIsDie(ParticleData particle);                //检查粒子是否需要停止运行
void UpdateParticle(inout ParticleData particle);              //运行中的粒子逐帧更新
void FixUpdateParticle(inout ParticleData particle);           //运行中的粒子逐帧更新
void OnCheckCollsion(inout ParticleData particle);             //需要碰撞检查时调用的方法


bool IsRelease(uint index){
    if(index > _CurrentIndex){
        return index >= (_CurrentIndex + _ParticlesCount - _ReleaseCount);
    }
    return (int)index > (int)_CurrentIndex - (int)_ReleaseCount;
}

[numthreads(64, 1, 1)]
void ParticleUpdate (IndexInput input)
{
    if(input.id.x >= _ParticlesCount) return;
    ParticleData particle = _ParticlesBuffer[input.id.x];
    if(particle.index.x == 0){
        if(IsRelease(input.id.x)){
            InitializeParticle(particle);
            particle.index.x = 1;
        }
        else
            return;
    }
    
    if(CheckParticleIsDie(particle)){
        particle.index.x = 0;
        _ParticlesBuffer[input.id.x] = particle;
        return;
    }
    UpdateParticle(particle);

    _ParticlesBuffer[input.id.x] = particle;
}

//得到a和b点的距离平方
float GetPositionSqrLength(float3 a, float3 b){
    float3 ray = a - b;
    return dot(ray, ray);
}

//检查第a个buffer是否需要和第b个调换，越大，顺序越前
bool CheckChange(int a, int b){
    ParticleData left =  _ParticlesBuffer[a];
    ParticleData right = _ParticlesBuffer[b];
    if(right.index.x == 0) return false;      //如果左粒子不启动，则直接退出，不需要更换
    if(left.index.x == 0) return true;
    float disL = GetPositionSqrLength(left.worldPos, _CameraPos);
    float disR = GetPositionSqrLength(right.worldPos, _CameraPos);
    if(disL < disR) return true;
    return false;
}

inline void ShellSort(int begin, int end)
{
	int i, j;
    uint size = end - begin, d;
	//第一个循环是分组循环，从一半开始，每次分组小一半
	for (d = size / 2; d >= 1; d/=2) {
		//开始对每一个分组进行插入排序，根据插入排序的正常逻辑
		//确实应该要从第一个d开始，逐渐上升保证每一个排序的正确性
		for (i = begin + d; i < end; i++) {
			int tem = _ParticlesIndexsBuffer[i];    //右边的粒子
			for (j = i - d; j >= begin && CheckChange(_ParticlesIndexsBuffer[j], tem); j -= d) {
				_ParticlesIndexsBuffer[j + d] = _ParticlesIndexsBuffer[j];
			}
			_ParticlesIndexsBuffer[j + d] = tem;
		}
	}
}


[numthreads(64, 1, 1)]
void ParticleSorting (IndexInput input)
{
    int maxIndex = input.id.x * _SortSize.x + _SortIndexOffset * _SortSize.y;
    int minIndex = max(maxIndex - _SortSize.x, 0);
    if(minIndex >= (int)_ParticlesCount) return;
    maxIndex = min(_ParticlesCount, maxIndex);

    ShellSort(minIndex, maxIndex);

    // int minsIndex = max(0, index - 1);
    // int leftIndex = _ParticlesIndexsBuffer[minsIndex], rightIndex = _ParticlesIndexsBuffer[index];

    // ParticleData left =  _ParticlesBuffer[leftIndex];
    // ParticleData right = _ParticlesBuffer[rightIndex];

    // if(right.index.x == 0) return;      //如果左粒子不启动，则直接退出，不需要更换

    // //左边未运行，直接更换
    // if(left.index.x == 0) {
    //     _ParticlesIndexsBuffer[minsIndex] = rightIndex;
    //     _ParticlesIndexsBuffer[index] = leftIndex;
    //     return;
    // }

    // float disL = GetPositionSqrLength(left.worldPos, _CameraPos);
    // float disR = GetPositionSqrLength(right.worldPos, _CameraPos);
    // if(disL < disR){
    //     _ParticlesIndexsBuffer[minsIndex] = rightIndex;
    //     _ParticlesIndexsBuffer[index] = leftIndex;
    // }
}

[numthreads(64, 1, 1)]
void ParticleFixUpdate(IndexInput input)
{
    if(input.id.x >= _ParticlesCount) return;
    ParticleData particle = _ParticlesBuffer[input.id.x];
    if(particle.index.x == 0)
        return;
    
    FixUpdateParticle(particle);
    _ParticlesBuffer[input.id.x] = particle;
}

[numthreads(64, 1, 1)]
void ParticleFixUpdate_Collsion(IndexInput input)
{
    if(input.id.x >= _ParticlesCount) return;
    ParticleData particle = _ParticlesBuffer[input.id.x];
    if(particle.index.x == 0)
        return;
    
    FixUpdateParticle(particle);
    OnCheckCollsion(particle);
    _ParticlesBuffer[input.id.x] = particle;
}

#endif
