using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    /// <summary>
    /// 基本的Shader 名称标识
    /// </summary>
    public class ShaderProperty
    {
        public static int
	        _MainTex = Shader.PropertyToID("_MainTex"),
            cameraDepthTexId = Shader.PropertyToID("_CameraDepthTexture"),
            cameraColorTexId = Shader.PropertyToID("_CameraColorTexture"),
            tempBufferId = Shader.PropertyToID("_CameraTempBuffer"),
            bufferSizeId = Shader.PropertyToID("_CameraBufferSize"),
            colorAttachmentId = Shader.PropertyToID("_CameraColorAttachment"),
            sourceTextureId = Shader.PropertyToID("_SourceTexture"),
            resultId = Shader.PropertyToID("Result"),
            inverseVPMatrixId = Shader.PropertyToID("_InverseVPMatrix"),
            frustumCornersRayId = Shader.PropertyToID("_FrustumCornersRay");

        public static int CameraDepthTexId => cameraDepthTexId; //非透明渲染完后的深度Buffer
        public static int CameraColorTexId => cameraColorTexId; //非透明渲染完后的颜色Buffer
        public static int TempBufferId => tempBufferId;        //临时Buffer，只声明一个，方便复用
        public static int BufferSizeId => bufferSizeId;        //屏幕大小, xy:width,height, zw:1/width,1/height
        public static int ColorAttachmentId => colorAttachmentId ;
        public static int SourceTextureId =>sourceTextureId ;        //颜色拷贝时传入纹理的名称
        public static int FrustumCornersRayId => frustumCornersRayId;

        public static int InverseVPMatrixId => inverseVPMatrixId;

        public static int ResultId => resultId;     //Compute Shader的输出位置

        public static ShaderTagId
            OnlyWriteDeep = new ShaderTagId("WriteDeep");


        //inverseVPMatrixId = Shader.PropertyToID("_InverseVPMatrix"),

        //inverseProjectionMatrix = Shader.PropertyToID("_InverseProjectionMatrix"),
        //viewToScreenMatrixId = Shader.PropertyToID("_ViewToScreenMatrix"),
        //cameraProjectMatrixId = Shader.PropertyToID("_CameraProjectionMatrix");
        //worldToCameraMatrixId = Shader.PropertyToID("_WorldToCamera");


    }
}