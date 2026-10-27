#ifndef CUSTOM_UNITY_INPUT_INCLUDED
#define CUSTOM_UNITY_INPUT_INCLUDED

CBUFFER_START(UnityPerDraw)
	float4x4 unity_ObjectToWorld;
	float4x4 unity_WorldToObject;
	float4 unity_LODFade;
	real4 unity_WorldTransformParams;

	float4 unity_RenderingLayer;

	real4 unity_LightData;
	real4 unity_LightIndices[2];

	float4 unity_ProbesOcclusion;

	float4 unity_LightmapST;
	float4 unity_DynamicLightmapST;

	float4 unity_SHAr;
	float4 unity_SHAg;
	float4 unity_SHAb;
	float4 unity_SHBr;
	float4 unity_SHBg;
	float4 unity_SHBb;
	float4 unity_SHC;

	float4 unity_ProbeVolumeParams;
	float4x4 unity_ProbeVolumeWorldToObject;
	float4 unity_ProbeVolumeSizeInv;
	float4 unity_ProbeVolumeMin;

	

    float4 unity_SpecCube0_BoxMax;
    float4 unity_SpecCube0_BoxMin;
    float4 unity_SpecCube0_ProbePosition;
    half4  unity_SpecCube0_HDR;

    float4 unity_SpecCube1_BoxMax;
    float4 unity_SpecCube1_BoxMin;
    float4 unity_SpecCube1_ProbePosition;
    half4  unity_SpecCube1_HDR;
CBUFFER_END



float4x4 unity_MatrixVP;
float4x4 unity_MatrixV;
float4x4 unity_IT_MatrixMV;
float4x4 unity_CameraToWorld;
float4x4 unity_CameraInvProjection;
float4x4 unity_CameraProjection;
float4x4 glstate_matrix_projection;
float4x4 unity_MatrixPreviousM;
float4x4 unity_MatrixPreviousMI;


float3 _WorldSpaceCameraPos;

float4 unity_OrthoParams;
float4 _ProjectionParams;
float4 _ScreenParams;
float4 _ZBufferParams;
float4 _Time;



//------------------------我的管线传入的数据------------------------
float4 _CameraBufferSize;
float4x4 _InverseVPMatrix;
float4 _Sh9[9];

/// <summary>    /// 贴图的结构体节点    /// </summary>
struct TextureNode
{
    int width;
    int height;
    int mipsLevel;
};

int _SumTextureCount;		//数组中每一层具有的贴图结点数量（= TexturesCount * TilesPerAxis * TilesPerAxis）
int _TilesPerAxis;		//每张贴图切成的 tile 数（每轴）；=1 时为旧整图一格路径
StructuredBuffer<TextureNode> _TextureNodesBuffer;		//4级Mips合并得到的数组
float4 _VTFeedbackParam;    //x:一行的页表数量，

float4 _LevelData[7];		//x:Begin,y:tileSizeX,z:tileSizeY,
float4 _LevelPadding[7];	//x:tilePaddingX, y:tilePaddingY
TEXTURE2D(_VirtualAlbedo);
TEXTURE2D(_VirtualNormal);
// 【3 张图集】布局见 TileTable.cs：
//   _VirtualAlbedo : rgb=albedo, a=emission.r
//   _VirtualNormal : rg=normal.xy(0..1), b=detailMask, a=roughness
//   _VirtualMask   : r=metallic, g=AO, b=emission.g, a=emission.b
TEXTURE2D(_VirtualMask);

//【各向异性过滤的关键】必须为每张图集声明【自己的】采样器 sampler_Virtual*，而不是用
//全局的 sampler_linear_repeat。原因：Unity 只有在「采样器对应的是一个材质属性纹理」时，
//才会沿用该纹理自己的 anisoLevel；全局采样器 sampler_linear_repeat 恒为 aniso=1。
//场景贴图（如桶 albedo/normal）导入设置是 aniso=9，标准路径走 sampler_MainTex 吃到了
//各向异性，而 RVT 走 linear 采样器没有 —— 圆柱侧面、地面这类掠射角表面会明显更糊，
//这正是「RVT 比不用 RVT 糊」的真实来源之一。图集改由材质属性绑定 + anisoLevel=8。
SAMPLER(sampler_VirtualAlbedo);
SAMPLER(sampler_VirtualNormal);
SAMPLER(sampler_VirtualMask);

//------------------------我的管线传入的数据------------------------

#endif