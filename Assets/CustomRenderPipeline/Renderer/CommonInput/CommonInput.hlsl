#ifndef CUSTOMRP_COMMON_INPUT
#define CUSTOMRP_COMMON_INPUT


TEXTURE2D(_SourceTexture);      //临时根据贴图存放位置
TEXTURE2D(_CameraDepthTexture);      //临时根据贴图存放位置
TEXTURE2D(_CameraColorTexture);

float4 _SourceTexture_TexelSize;
float4 _CameraColorTexture_TexelSize;

float4 GetSourceTexelSize () {
	return _SourceTexture_TexelSize;
}

//采样临时颜色贴图数据
float4 GetSource(float2 screenUV){
    return SAMPLE_TEXTURE2D_LOD(_SourceTexture, sampler_linear_repeat, screenUV, 0);
}

#endif