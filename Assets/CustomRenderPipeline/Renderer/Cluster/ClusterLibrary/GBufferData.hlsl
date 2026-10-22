#ifndef CLUSTER_GBUFFER_DATA
#define CLUSTER_GBUFFER_DATA

struct DefferTarget{
    float4 _GBufferRT0 : SV_Target0;	//rgb:abledo,w:metalness
    float4 _GBufferRT1 : SV_Target1;	//normal
    float4 _GBufferRT2 : SV_Target2;	//rgb:emissive,w:roughness
    float4 _GBufferRT3 : SV_Target3;	//rgb:reflect,w:AO
};

TEXTURE2D(_GBufferRT0);     //rgb:abledo,w:metalness
TEXTURE2D(_GBufferRT1);     //R,G:EncodeNormal
TEXTURE2D(_GBufferRT2);     //rgb:emissive,w:roughness
TEXTURE2D(_GBufferRT3);     //rgb:reflect,w:AO
TEXTURE2D(_GBufferDepthTex);     //rgb:reflect,w:AO
TEXTURE2D(_LightTexture);     //rgb:reflect,w:AO
#endif