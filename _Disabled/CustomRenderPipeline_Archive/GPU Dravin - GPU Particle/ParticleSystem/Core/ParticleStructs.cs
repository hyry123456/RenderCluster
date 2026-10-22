
namespace CustomRenderPipline
{
    [System.Serializable]
    //单个粒子需要的数据
    struct ParticleData
    {
        public float4 random;          //xyz是随机数，w是目前存活时间
        public uint4 index;            //状态标记，x是运行标记(0未运行，1运行)，yzw可以拓展
        public float3 worldPos;        //当前位置
        public float4 uvTransData;     //uv动画需要的数据
        public float interpolation;    //插值需要的数据
        public float4 color;           //颜色值，包含透明度
        public float size;             //粒子大小
        public float3 nowSpeed;        //xyz是当前速度以及方向
    };


}