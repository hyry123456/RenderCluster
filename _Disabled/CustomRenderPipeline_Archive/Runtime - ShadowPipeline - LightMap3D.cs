using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    /// <summary> /// 3D光照贴图渲染实例 /// </summary>
    public class LightMap3D : IShadowPipeline
    {
        int currentIndex;
        Bounds bounds;
        Light[] renderLights;
        ComputeShader computeShader;
        RenderTexture texture3D;

        public RenderTexture LightMap => texture3D;

        ShadowMapSize shadowMapSize;
        ShadowFilter filter;

        public LightMap3D(Bounds bounds, Light[] lights,
            ComputeShader computeShader, ShadowMapSize shadowSize, 
            Texture3DSize texSize, ShadowFilter filter, RenderTextureFormat textureFormat)
        {
            this.shadowMapSize = shadowSize;
            this.bounds = bounds;
            currentIndex = 0;
            renderLights = lights;
            this.filter = filter;
            this.computeShader = computeShader;

            //特殊情况下可能只要一张全黑图，但是需要体积效果
            if(lights != null && lights.Length > 0)
                ShadowPipelineRegister.Instance.Enroll(this);
            int size = (int)texSize;
            texture3D = RenderTexture.GetTemporary(size, size, 0, textureFormat);
            texture3D.enableRandomWrite = true;
            texture3D.dimension = TextureDimension.Tex3D;
            texture3D.volumeDepth = size;     //深度Tex3d的深度
            texture3D.filterMode = FilterMode.Bilinear;
            texture3D.wrapMode = TextureWrapMode.Clamp;
            texture3D.Create();
            Graphics.Blit(Texture2D.blackTexture, texture3D);   //先得到一张全黑图
        }

        public Light SetUpCamera(out int mapSize, out Bounds bounds)
        {
            mapSize = (int)shadowMapSize;
            bounds = this.bounds;
            return renderLights[currentIndex / 2];
        }

        public void BeforeEnd(RenderTexture depthRT, ScriptableRenderContext context, CommandBuffer buffer, Light light, Matrix4x4[] vpMatrixs)
        {
            if (currentIndex % 2 != 1) return;
            int count = Mathf.CeilToInt(texture3D.width / 8.0f);

            Vector2 mapSize = new Vector2(depthRT.width, 1.0f / depthRT.width);

            buffer.SetComputeVectorParam(computeShader, "_BoxMax", bounds.max);
            buffer.SetComputeVectorParam(computeShader, "_BoxMin", bounds.min);
            buffer.SetComputeMatrixParam(computeShader, "_WorldToNDC", vpMatrixs[0]);
            buffer.SetComputeVectorParam(computeShader, "_ShadowMapSize", mapSize);
            buffer.SetComputeVectorParam(computeShader, "_LightColor", light.color * light.intensity);
            buffer.SetComputeVectorParam(computeShader, "_NormalBias", light.transform.forward * light.shadowNormalBias);
            buffer.SetComputeIntParam(computeShader, "_FilterCount", (int)filter);
            switch (light.type)
            {
                case LightType.Directional:
                    buffer.SetComputeTextureParam(computeShader, 0, "_ShadowMap", depthRT);
                    buffer.SetComputeTextureParam(computeShader, 0, "Result", texture3D);
                    buffer.DispatchCompute(computeShader, 0, count, count, count);
                    break;
                case LightType.Spot:
                    buffer.SetComputeTextureParam(computeShader, 1, "_ShadowMap", depthRT);
                    buffer.SetComputeTextureParam(computeShader, 1, "Result", texture3D);

                    Vector4 position = light.transform.position;
                    position.w =
                        1f / Mathf.Max(light.range * light.range, 0.00001f);
                    Vector4 direction = -light.transform.forward;

                    float innerCos = Mathf.Cos(Mathf.Deg2Rad * 0.5f * light.innerSpotAngle);
                    float outerCos = Mathf.Cos(Mathf.Deg2Rad * 0.5f * light.spotAngle);
                    float angleRangeInv = 1f / Mathf.Max(innerCos - outerCos, 0.001f);
                    Vector4 lightSpotAngles = new Vector4(
                        angleRangeInv, -outerCos * angleRangeInv, outerCos
                    );

                    buffer.SetComputeVectorParam(computeShader, "_LightPosition", position);
                    buffer.SetComputeVectorParam(computeShader, "_LightSpotAngle", lightSpotAngles);
                    buffer.SetComputeVectorParam(computeShader, "_LightDirect", direction);


                    buffer.DispatchCompute(computeShader, 1, count, count, count);

                    break;
                case LightType.Point:
                    buffer.SetComputeTextureParam(computeShader, 2, "_ShadowMap", depthRT);
                    buffer.SetComputeTextureParam(computeShader, 2, "Result", texture3D);
                    Vector4 pointPos = light.transform.position;
                    pointPos.w =
                        1f / Mathf.Max(light.range * light.range, 0.00001f);
                    buffer.SetComputeVectorParam(computeShader, "_LightPosition", pointPos);
                    buffer.SetComputeMatrixArrayParam(computeShader, "_PointWorldToNDC", vpMatrixs);
                    buffer.DispatchCompute(computeShader, 2, count, count, count);
                    break;
            }
        }

        public bool IsEnding()
        {
            currentIndex++;
            if (currentIndex >= renderLights.Length * 2)
                return true;
            return false;
        }

        public void Dispose()
        {
            RenderTexture.ReleaseTemporary(texture3D);
        }
    }
}