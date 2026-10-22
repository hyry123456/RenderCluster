using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline.MyRenderPipeline
{
    public class MyShadow
    {
        const string bufferName = "Shadows";

        static string[] directionalFilterKeywords = {
            "_DIRECTIONAL_PCF3",
            "_DIRECTIONAL_PCF5",
            "_DIRECTIONAL_PCF7",
        };

        static string[] otherFilterKeywords = {
            "_OTHER_PCF3",
            "_OTHER_PCF5",
            "_OTHER_PCF7",
        };

        static string[] cascadeBlendKeywords = {
            "_CASCADE_BLEND_SOFT",
            "_CASCADE_BLEND_DITHER"
        };

        struct ShadowedDirectionalLight
        {
            public float slopeScaleBias;
            public Light light;
        }
        ShadowedDirectionalLight[] shadowedDirectionalLights =
            new ShadowedDirectionalLight[maxShadowedDirLightCount];

        struct ShadowedOtherLight
        {
            public Light light;
            public float slopeScaleBias;
            public float normalBias;
            public bool isPoint;
        }

        ShadowedOtherLight[] shadowedOtherLights =
            new ShadowedOtherLight[maxShadowedOtherLightCount];


        int shadowedDirLightCount, shadowedOtherLightCount;

        const int maxShadowedDirLightCount = 4, maxShadowedOtherLightCount = 16;
        const int maxCascades = 4;

        ScriptableRenderContext context;
        //CullingResults cullingResults;
        ShadowSetting settings;
        float shadowDistance; //far和near
        Material material;

        CommandBuffer buffer = new CommandBuffer
        {
            name = bufferName
        };


        Vector4 atlasSizes;
        /// <summary>        /// 摄像机到视锥体的射线        /// </summary>
        Vector3 viewLD, viewLU, viewRD, viewRU, camPos, camDir;
        /// <summary>
        /// 宽度比例，和宽度比例，乘以距离就能得到视锥体的宽和高
        /// </summary>
        float widthRadio, hightRadio;
        Camera camera;

        static int
            dirShadowAtlasId = Shader.PropertyToID("_DirectionalShadowAtlas"),
            dirShadowMatricesId = Shader.PropertyToID("_DirectionalShadowMatrices"),
            otherShadowAtlasId = Shader.PropertyToID("_OtherShadowAtlas"),
            otherShadowMatricesId = Shader.PropertyToID("_OtherShadowMatrices"),
            otherShadowTilesId = Shader.PropertyToID("_OtherShadowTiles"),
            cascadeCountId = Shader.PropertyToID("_CascadeCount"),
            //cascadeCullingSpheresId = Shader.PropertyToID("_CascadeCullingSpheres"),
            csmDataId = Shader.PropertyToID("_CSMData"),
            shadowViewMatricesId = Shader.PropertyToID("_ShadowViewMatrices"),
            cascadeDataId = Shader.PropertyToID("_CascadeData"),
            lightDataId = Shader.PropertyToID("_LightData"),
            lightAngleDataId = Shader.PropertyToID("_LightAngleData"),

            shadowAtlastSizeId = Shader.PropertyToID("_ShadowAtlasSize"),
            shadowDistanceFadeId = Shader.PropertyToID("_ShadowDistanceFade"),
            shadowPancakingId = Shader.PropertyToID("_ShadowPancaking");

        static Vector4[]
            //cascadeCullingSpheres = new Vector4[maxCascades],
            cascadeData = new Vector4[maxCascades],
            csmData = new Vector4[maxCascades],
            otherShadowTiles = new Vector4[maxShadowedOtherLightCount];
        //static float[] csmData = new float[maxCascades];        //看不懂阴影级联球，只能用距离算了

        static Matrix4x4[]
            dirShadowMatrices = new Matrix4x4[maxShadowedDirLightCount * maxCascades],
            otherShadowMatrices = new Matrix4x4[maxShadowedOtherLightCount];

        public MyShadow(Material material)
        {
            this.material = material;
        }

        

        /// <summary>	/// 准备阴影数据，用来传递一些对象	/// </summary>
        public void Setup(
            ScriptableRenderContext context, float shadowDistance,
            ShadowSetting settings, Camera camera
        )
        {
            this.context = context;
            this.shadowDistance = shadowDistance;
            this.settings = settings;

            this.camera = camera;

            float fov = camera.fieldOfView;
            float asp = camera.aspect;
            float yf = Mathf.Tan(fov / 2 * Mathf.Deg2Rad); float xf = yf * asp;
            Transform tr = camera.transform;
            viewLD = tr.forward - tr.right * xf - tr.up * yf;
            viewLU = tr.forward - tr.right * xf + tr.up * yf;
            viewRD = tr.forward + tr.right * xf - tr.up * yf;
            viewRU = tr.forward + tr.right * xf + tr.up * yf;
            camDir = tr.forward; camPos = tr.position;
            widthRadio = xf; hightRadio = yf;

            shadowedDirLightCount = shadowedOtherLightCount = 0;
        }

        /// <summary>	/// 注册直接光阴影，判断该直接光是否支持阴影	/// </summary>
        /// <returns>阴影强度，图片开始位置，normalBais，-1(无意义)</returns>
        public Vector4 ReserveDirectionalShadows(
            Light light
        )
        {
            //因为目前只实现灯光渲染，因此其他内容
            if (
                shadowedDirLightCount < maxShadowedDirLightCount &&
                light.shadows != LightShadows.None && light.shadowStrength > 0f
            )
            {
                shadowedDirectionalLights[shadowedDirLightCount] =
                    new ShadowedDirectionalLight
                    {
                        light = light,
                        slopeScaleBias = light.shadowBias * 2,
                    };
                return new Vector4(
                    light.shadowStrength,
                    settings.directional.cascadeCount * shadowedDirLightCount++,
                    light.shadowNormalBias, -1f
                );
            }
            return new Vector4(0f, 0f, 0f, -1f);
        }

        /// <summary>	/// 注册其他光源阴影，用法和直接光类似	/// </summary>
        public Vector4 ReserveOtherShadows(Light light)
        {
            if (light.shadows == LightShadows.None || light.shadowStrength <= 0f)
            {
                return new Vector4(0f, 0f, 0f, -1f);
            }
            float maskChannel = -1f;
            //LightBakingOutput lightBaking = light.bakingOutput;
            //if (
            //    lightBaking.lightmapBakeType == LightmapBakeType.Mixed &&
            //    lightBaking.mixedLightingMode == MixedLightingMode.Shadowmask
            //)
            //{
            //    useShadowMask = true;
            //    maskChannel = lightBaking.occlusionMaskChannel;
            //}

            bool isPoint = light.type == LightType.Point;
            int newLightCount = shadowedOtherLightCount + (isPoint ? 6 : 1);
            if (
                newLightCount > maxShadowedOtherLightCount
            )
            {
                return new Vector4(-light.shadowStrength, 0f, 0f, maskChannel);
            }

            shadowedOtherLights[shadowedOtherLightCount] = new ShadowedOtherLight
            {
                //visibleLightIndex = visibleLightIndex,
                light = light,
                slopeScaleBias = light.shadowBias,
                normalBias = light.shadowNormalBias,
                isPoint = isPoint
            };

            Vector4 data = new Vector4(
                light.shadowStrength, shadowedOtherLightCount,
                isPoint ? 1f : 0f, maskChannel
            );
            shadowedOtherLightCount = newLightCount;
            return data;
        }

        bool IsRenderCluster;

        public void Render(int layerMask)
        {
            //buffer.SetComputeIntParam(compute, ClusterProperty.LayerMaskId, layerMask);
            //var instance = ClusterCoreCreate.Instance;
            //if (instance == null)
            //    IsRenderCluster = false;
            //else
            //    IsRenderCluster = instance.IsRender();

            if (shadowedDirLightCount > 0)
            {
                RenderDirectionalShadows();
            }
            else
            {
                buffer.GetTemporaryRT(
                    dirShadowAtlasId, 1, 1,
                    32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap
                );
            }
            if (shadowedOtherLightCount > 0)
            {
                RenderOtherShadows();
            }
            else
            {
                buffer.SetGlobalTexture(otherShadowAtlasId, dirShadowAtlasId);
            }

            buffer.BeginSample(bufferName);

            buffer.SetGlobalInt(
                cascadeCountId,
                shadowedDirLightCount > 0 ? settings.directional.cascadeCount : 0
            );
            float f = 1f - settings.directional.cascadeFade;
            //存储渐变数据，方便之后做多级渐变
            buffer.SetGlobalVector(
                shadowDistanceFadeId, new Vector4(
                    1f / settings.maxDistance, 1f / settings.distanceFade,
                    1f / (1f - f * f)
                )
            );
            buffer.SetGlobalVector(shadowAtlastSizeId, atlasSizes);
            buffer.EndSample(bufferName);
            ExecuteBuffer();
        }

        public void Cleanup()
        {
            buffer.ReleaseTemporaryRT(dirShadowAtlasId);
            if (shadowedOtherLightCount > 0)
            {
                buffer.ReleaseTemporaryRT(otherShadowAtlasId);
            }
            ExecuteBuffer();
        }


        void RenderDirectionalShadows()
        {
            int atlasSize = (int)settings.directional.atlasSize;
            atlasSizes.x = atlasSize;
            atlasSizes.y = 1f / atlasSize;
            buffer.GetTemporaryRT(
                dirShadowAtlasId, atlasSize, atlasSize,
                32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap
            );
            buffer.SetRenderTarget(
                dirShadowAtlasId,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store
            );

            buffer.ClearRenderTarget(true, false, Color.clear);
            buffer.SetGlobalFloat(shadowPancakingId, 1f);
            buffer.BeginSample(bufferName);
            ExecuteBuffer();

            int tiles = shadowedDirLightCount * settings.directional.cascadeCount;
            int split = tiles <= 1 ? 1 : tiles <= 4 ? 2 : 4;
            int tileSize = atlasSize / split;

            //循环每一个直接光
            for (int i = 0; i < shadowedDirLightCount; i++)
            {
                RenderDirectionalShadows(i, split, tileSize);
            }
            //设置视角矩阵
            Matrix4x4 camViewMat = GetViewMatrix(camera.transform);
            //传递裁剪数据
            buffer.SetGlobalVectorArray(csmDataId, csmData);
            buffer.SetGlobalVectorArray(cascadeDataId, cascadeData);
            buffer.SetGlobalMatrix(shadowViewMatricesId, camViewMat);
            buffer.SetGlobalMatrixArray(dirShadowMatricesId, dirShadowMatrices);
            SetKeywords(
                directionalFilterKeywords, (int)settings.directional.filter - 1
            );
            SetKeywords(
                cascadeBlendKeywords, (int)settings.directional.cascadeBlend - 1
            );
            buffer.EndSample(bufferName);
            ExecuteBuffer();
        }

        void RenderDirectionalShadows(int index, int split, int tileSize)
        {
            ShadowedDirectionalLight light = shadowedDirectionalLights[index];
            int cascadeCount = settings.directional.cascadeCount;
            int tileOffset = index * cascadeCount;
            Vector3 ratios = settings.directional.CascadeRatios;
            float tileScale = 1f / split;

            Matrix4x4 viewMatrix = GetViewMatrix(light.light.transform);


            buffer.EnableShaderKeyword("_DIRECTION_NEAR");

            for (int i = 0; i < cascadeCount; i++)
            {
                Vector2 near_far = GetNearAndFar(ratios, i, cascadeCount);
                float texelSize = 2f * near_far.y / tileSize;
                float filterSize = texelSize * ((float)settings.directional.filter + 1f);
                //cascadeData[i] = new Vector4(1f / near_far.y, filterSize * 1.7142136f);
                cascadeData[i] = new Vector4(1f / near_far.y, filterSize * 2.0142136f);

                Matrix4x4 project; 
                Vector4[] cullPlane;
                //NearMax = ratios[i] * shadowDistance;
                CreateDirectionData(light.light, near_far, viewMatrix, 
                    out project, out cullPlane, i);

                if (index == 0)
                {
                    SetShadowData(i, near_far, settings.directional.cascadeFade);
                }

                int tileIndex = tileOffset + i;
                    dirShadowMatrices[tileIndex] = ConvertToAtlasMatrix(
                        project * viewMatrix,
                        SetTileViewport(tileIndex, split, tileSize), tileScale
                    );

                buffer.SetViewProjectionMatrices(viewMatrix, project);
                buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);

                var renderObjects = MyRenderObject.Instance;
                if (renderObjects != null)
                {
                    var staticJob = new StaticObjectShadowJob();
                    staticJob.Plane0 = cullPlane[0]; staticJob.Plane1 = cullPlane[1];
                    staticJob.Plane2 = cullPlane[2]; staticJob.Plane3 = cullPlane[3];
                    staticJob.Plane4 = cullPlane[4];

                    staticJob.staticObjs = renderObjects.StaticCPUObjects;
                    int staticCount = renderObjects.CurrentStaticObjectsCount;
                    JobHandle handle = staticJob.Schedule(staticCount, 1);
                    //等待作业完成
                    handle.Complete();
                    for (int j = 0; j < staticCount; j++)
                    {
                        if (staticJob.staticObjs[j].isUse)
                        {
                            var obj = renderObjects.StaticCPUObjects_data[j];
                            for (int k = 0; k < obj.renderMesh.subMeshCount; k++)
                                buffer.DrawMesh(obj.renderMesh,
                                    obj.transform.localToWorldMatrix, obj.material, k, 1);
                        }

                    }

                }

                //if (IsRenderCluster)
                //{
                //    ClusterRenderSystem system = ClusterCoreCreate.Instance.RenderSystem;
                //    buffer.SetComputeVectorArrayParam(compute, "_DirectionShadowPlanes"
                //        , cullPlane);

                //    buffer.DispatchCompute(compute, 1,
                //        Mathf.CeilToInt(system.ClusterCount / 64f), 1, 1);
                //    buffer.CopyCounterValue(system.appendBuffer, system.indrectBuffer, 4);

                //    buffer.DrawProceduralIndirect(Matrix4x4.identity, material,
                //        (int)ClusterCorePass.DrawShadow, MeshTopology.Points, system.indrectBuffer);
                //    //清理内容
                //    buffer.SetComputeBufferCounterValue(system.appendBuffer, 0);
                //}
                //GPUDravinSystem.Instance.SetDirectionCullingPlane(
                //    cullPlane, light.);
                GPUDravinSystem.Instance.DrawShadows(context, buffer);
            }
            buffer.DisableShaderKeyword("_DIRECTION_NEAR");
            ExecuteBuffer();
            buffer.SetGlobalDepthBias(0f, 0f);
        }

        void RenderOtherShadows()
        {
            int atlasSize = (int)settings.other.atlasSize;
            atlasSizes.z = atlasSize;
            atlasSizes.w = 1f / atlasSize;
            buffer.GetTemporaryRT(
                otherShadowAtlasId, atlasSize, atlasSize,
                32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap
            );
            buffer.SetRenderTarget(
                otherShadowAtlasId,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store
            );
            buffer.ClearRenderTarget(true, false, Color.clear);
            buffer.SetGlobalFloat(shadowPancakingId, 0f);
            buffer.BeginSample(bufferName);
            ExecuteBuffer();

            int tiles = shadowedOtherLightCount;
            int split = tiles <= 1 ? 1 : tiles <= 4 ? 2 : 4;
            int tileSize = atlasSize / split;

            for (int i = 0; i < shadowedOtherLightCount;)
            {
                if (shadowedOtherLights[i].isPoint)
                {
                    RenderPointShadows(i, split, tileSize);
                    i += 6;
                }
                else
                {
                    RenderSpotShadows(i, split, tileSize);
                    i += 1;
                }
            }

            buffer.SetGlobalMatrixArray(otherShadowMatricesId, otherShadowMatrices);
            buffer.SetGlobalVectorArray(otherShadowTilesId, otherShadowTiles);
            SetKeywords(
                otherFilterKeywords, (int)settings.other.filter - 1
            );
            buffer.EndSample(bufferName);
            ExecuteBuffer();
        }

        void RenderSpotShadows(int index, int split, int tileSize)
        {
            ShadowedOtherLight light = shadowedOtherLights[index];

            Matrix4x4 projectMatrix, viewMatrix = GetViewMatrix(light.light.transform);
            GetSpotData(light.light, out projectMatrix);

            float texelSize = 2f / (tileSize * projectMatrix.m00);
            float filterSize = texelSize * ((float)settings.other.filter + 1f);
            float bias = light.normalBias * filterSize * 1.4142136f;
            Vector2 offset = SetTileViewport(index, split, tileSize);
            float tileScale = 1f / split;
            SetOtherTileData(index, offset, tileScale, bias);
            otherShadowMatrices[index] = ConvertToAtlasMatrix(
                projectMatrix * viewMatrix, offset, tileScale
            );

            buffer.SetViewProjectionMatrices(viewMatrix, projectMatrix);
            buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);

            //Light lig = light.light;
            //Vector4 lightData = lig.transform.position; lightData.w = lig.range;
            //buffer.SetComputeVectorParam(compute, lightDataId, lightData);
            //Vector4 lightAngleData = lig.transform.forward;
            //lightAngleData.w = lig.spotAngle * 0.5f * Mathf.Deg2Rad;
            //buffer.SetComputeVectorParam(compute, lightAngleDataId, lightAngleData);


            //if (IsRenderCluster)
            //{
            //    ClusterRenderSystem system = ClusterCoreCreate.Instance.RenderSystem;
            //    buffer.DispatchCompute(compute, 2,
            //        Mathf.CeilToInt(system.ClusterCount / 64f), 1, 1);

            //    buffer.CopyCounterValue(system.appendBuffer, system.indrectBuffer, 4);


            //    buffer.DrawProceduralIndirect(Matrix4x4.identity, material,
            //        (int)ClusterCorePass.DrawShadow, MeshTopology.Points, system.indrectBuffer);
            //    //清理内容
            //    buffer.SetComputeBufferCounterValue(system.appendBuffer, 0);
            //}


            //实现灯光剔除，使用球剔除来优化

            ExecuteBuffer();
            buffer.SetGlobalDepthBias(0f, 0f);
        }

        void RenderPointShadows(int index, int split, int tileSize)
        {
            ShadowedOtherLight light = shadowedOtherLights[index];
            float texelSize = 2f / tileSize;
            float filterSize = texelSize * ((float)settings.other.filter + 1f);
            float bias = light.normalBias * filterSize * 1.4142136f;
            float tileScale = 1f / split;
            float fovBias =
                Mathf.Atan(1f + bias + filterSize) * Mathf.Rad2Deg * 2f - 90f;


            Light lig = light.light;
            Vector4 lightData = lig.transform.position; lightData.w = lig.range;

            //buffer.SetComputeVectorParam(compute, lightDataId, lightData);

            //buffer.SetComputeBufferParam(compute, 3,
            //    ClusterProperty.ClustersBufferId, system.clustersBuffer);
            //buffer.SetComputeBufferParam(compute, 3,
            //    ClusterProperty.AppendBufferId, system.appendBuffer);
            //buffer.SetComputeBufferParam(compute, 1,
            //    ClusterProperty.RenderDatasBufferId, system.renderDatasBuffer);
            //ClusterRenderSystem system = null;

            //if (IsRenderCluster)
            //{
            //    system = ClusterCoreCreate.Instance.RenderSystem;
            //    buffer.DispatchCompute(compute, 3,
            //        Mathf.CeilToInt(system.ClusterCount / 64f), 1, 1);
            //    //提前裁剪好数据
            //    buffer.CopyCounterValue(system.appendBuffer, system.indrectBuffer, 4);
            //}

            for (int i = 0; i < 6; i++)
            {
                //cullingResults.ComputePointShadowMatricesAndCullingPrimitives(
                //    light.visibleLightIndex, (CubemapFace)i, fovBias,
                //    out Matrix4x4 viewMatrix, out Matrix4x4 projectionMatrix,
                //    out ShadowSplitData splitData
                //);

                Matrix4x4 viewMatrix, projectMat;
                GetPointData(light.light, (CubemapFace)i, fovBias, out projectMat, out viewMatrix);

                //viewMatrix.m11 = -viewMatrix.m11;
                //viewMatrix.m12 = -viewMatrix.m12;
                //viewMatrix.m13 = -viewMatrix.m13;

                int tileIndex = index + i;
                Vector2 offset = SetTileViewport(tileIndex, split, tileSize);
                SetOtherTileData(tileIndex, offset, tileScale, bias);
                otherShadowMatrices[tileIndex] = ConvertToAtlasMatrix(
                    projectMat * viewMatrix, offset, tileScale
                );

                buffer.SetViewProjectionMatrices(viewMatrix, projectMat);
                buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);

                //if (IsRenderCluster)
                //{
                //    buffer.DrawProceduralIndirect(Matrix4x4.identity, material,
                //        (int)ClusterCorePass.DrawShadow, MeshTopology.Points, system.indrectBuffer);
                //}


                ExecuteBuffer();
                buffer.SetGlobalDepthBias(0f, 0f);
            }
            //if (IsRenderCluster)
            //    buffer.SetComputeBufferCounterValue(system.appendBuffer, 0);
        }


        Vector2 GetNearAndFar(Vector3 ratios, int index, int maxIndex)
        {
            Vector2 near_far = Vector2.zero;
            if (maxIndex - 1 == index)   //最后一个的末尾一定是1.
                near_far.y = 1f;
            switch (index)
            {
                case 0:
                    near_far.x = camera.nearClipPlane; 
                    near_far.y = ratios.x * 1.05f;//略微增加重合，避免断裂

                    if (maxIndex - 1 == index)   //最后一个的末尾一定是1.
                        near_far.y = 1f;
                    near_far.y *= shadowDistance;
                    return near_far;
                case 1:
                    near_far.x = ratios.x;
                    near_far.y = ratios.y * 1.05f;//略微增加重合，避免断裂
                    break;
                case 2:
                    near_far.x = ratios.y;
                    near_far.y = ratios.z * 1.05f;//略微增加重合，避免断裂
                    break;
                case 3:
                    near_far.x = ratios.z;
                    near_far.y = 1;
                    break;
            }
            if (maxIndex - 1 == index)   //最后一个的末尾一定是1.
                near_far.y = 1f;

            near_far.y *= shadowDistance; near_far.x *= shadowDistance;

            return near_far;
        }

        /// <summary>        /// 获得直接光渲染时需要的数据        /// </summary>
        void CreateDirectionData(Light light, Vector2 near_far, Matrix4x4 viewMat,
            out Matrix4x4 projectMat, out Vector4[] cullPlane, int index
            )
        {
            Transform ltr = light.transform;
            //确定视锥体的所有顶点
            Vector4[] points = new Vector4[]
            {
                //远平面
                camPos + near_far.y * viewLD,
                camPos + near_far.y * viewLU,
                camPos + near_far.y * viewRD,
                camPos + near_far.y * viewRU,

                //近平面
                camPos + near_far.x * viewLD,
                camPos + near_far.x * viewLU,
                camPos + near_far.x * viewRD,
                camPos + near_far.x * viewRU,
             };


            //确定矩阵范围
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), 
                max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            //为了保证阴影的质量，还是尝试找距离好一点
            Vector4 point;
            for (int i = 0; i < 8; i++)
            {
                point = points[i]; point.w = 1;
                Vector4 tem = viewMat * point;
                max.z = Mathf.Max(max.z, tem.z);
                min.z = Mathf.Min(min.z, tem.z);

                max.y = Mathf.Max(max.y, tem.y);
                min.y = Mathf.Min(min.y, tem.y);

                max.x = Mathf.Max(max.x, tem.x);
                min.x = Mathf.Min(min.x, tem.x);
            }

            //生成投影矩阵
            projectMat = Matrix4x4.Ortho(min.x, max.x, min.y, max.y, -max.z, -min.z);

            Matrix4x4 viewInv = (projectMat * viewMat).inverse;
            Vector3 forward = ltr.forward, up = ltr.up, right = ltr.right;
            //接下来需要生成裁剪面，每一个面的方向都是朝向中心，面上的点因为矩阵旋转过了
            //导致有所变形，只想到了用逆来求。
            cullPlane = new Vector4[]
            {
                new Vector4(-forward.x, -forward.y, -forward.z,
                    -Vector3.Dot(-forward, viewInv.MultiplyPoint(new Vector3(0, 0, 1)))), //正前方
                new Vector4(-up.x, -up.y, -up.z, 
                    -Vector3.Dot(-up, viewInv.MultiplyPoint(new Vector3(0, 1, 0)))), //上方
                new Vector4(up.x, up.y, up.z, 
                    -Vector3.Dot(up, viewInv.MultiplyPoint( new Vector3(0, -1, 0) ))), //下方
                new Vector4(right.x, right.y, right.z, 
                    -Vector3.Dot(right, viewInv.MultiplyPoint(new Vector3(-1, 0, 0)))), //左边
                new Vector4(-right.x, -right.y, -right.z, 
                    -Vector3.Dot(-right, viewInv.MultiplyPoint(new Vector3(1, 0, 0)))), //右边
            };

            //if (camera == Camera.main && temIndex == 0)
            //{
            //    Camera ligC = light.GetComponent<Camera>();
            //    if (ligC == null) ligC = light.gameObject.AddComponent<Camera>();
            //    ligC.orthographic = true; ligC.enabled = false;
            //    ligC.farClipPlane = -min.z; ligC.nearClipPlane = -max.z;
            //    ligC.projectionMatrix = projectMat;

            //    GameObject game1 = GameObject.Find("tem1");
            //    float maxz = Mathf.Max(Mathf.Abs(min.z), Mathf.Abs(max.z));
            //    if (game1 == null)
            //    {
            //        game1 = new GameObject("tem1");
            //        game1.transform.position = viewInv.MultiplyPoint(new Vector3(0, -1, 0));
            //    }
            //    else
            //        game1.transform.position = viewInv.MultiplyPoint(new Vector3(0, -1, 0));

            //    GameObject game2 = GameObject.Find("tem2");
            //    if (game2 == null)
            //    {
            //        game2 = new GameObject("tem2");
            //        game2.transform.position = viewInv.MultiplyPoint(new Vector3(-1, 0, 0));
            //    }
            //    else
            //        game2.transform.position = viewInv.MultiplyPoint(new Vector3(-1, 0, 0));

            //}

            //Debug.Log($"Len = {maxNear} NearMax = {max.z}");

        }

        void GetSpotData(Light light, out Matrix4x4 projectMat)
        {
            projectMat = Matrix4x4.Perspective(light.spotAngle, 1, 
                light.shadowNearPlane, light.range);
        }

        void GetPointData(Light light, CubemapFace face, float fovBais, out Matrix4x4 projectMat,
            out Matrix4x4 viewMatrix)
        {
            projectMat = Matrix4x4.Perspective(90 + fovBais, 1,
                light.shadowNearPlane, light.range);
            //Camera cam = light.GetComponent<Camera>();
            //if(cam == null)
            //{
            //    cam = light.gameObject.AddComponent<Camera>();
            //    cam.orthographic = false; cam.enabled = false;
            //}
            switch (face)
            {
                case CubemapFace.PositiveX:

                    viewMatrix = GetViewMatrix(light.transform.position,
                        Vector3.up, Vector3.right);
                    //light.transform.forward = Vector3.right;
                    //cam.farClipPlane = light.range;
                    //cam.fieldOfView = 90 + fovBais;
                    //cam.aspect = 1;
                    break;
                case CubemapFace.NegativeX:
                    viewMatrix = GetViewMatrix(light.transform.position,
                        Vector3.up, -Vector3.right);
                    //light.transform.forward = -Vector3.right;
                    //cam.farClipPlane = light.range;
                    //cam.fieldOfView = 90 + fovBais;
                    //cam.aspect = 1;
                    break;
                case CubemapFace.PositiveY:
                    viewMatrix = GetViewMatrix(light.transform.position,
                        -Vector3.forward, Vector3.up);

                    //light.transform.forward = Vector3.up;
                    //Debug.Log(light.transform.up);
                    //cam.farClipPlane = light.range;
                    //cam.fieldOfView = 90 + fovBais;
                    //cam.aspect = 1;

                    break;
                case CubemapFace.NegativeY:
                    viewMatrix = GetViewMatrix(light.transform.position,
                        Vector3.forward, -Vector3.up);
                    //light.transform.forward = -Vector3.up;
                    //Debug.Log(light.transform.up);
                    //cam.farClipPlane = light.range;
                    //cam.fieldOfView = 90 + fovBais;
                    //cam.aspect = 1;

                    break;
                case CubemapFace.PositiveZ:
                    viewMatrix = GetViewMatrix(light.transform.position,
                        Vector3.up, Vector3.forward);
                    //light.transform.forward = Vector3.forward;
                    //Debug.Log(light.transform.up);
                    //cam.farClipPlane = light.range;
                    //cam.fieldOfView = 90 + fovBais;
                    //cam.aspect = 1;

                    break;
                case CubemapFace.NegativeZ:
                    viewMatrix = GetViewMatrix(light.transform.position,
                        Vector3.up, -Vector3.forward);
                    //light.transform.forward = -Vector3.forward;
                    //Debug.Log(light.transform.up);
                    //cam.farClipPlane = light.range;
                    //cam.fieldOfView = 90 + fovBais;
                    //cam.aspect = 1;
                    break;
                default:
                    viewMatrix = Matrix4x4.identity;
                    break;
            }
        }

        Matrix4x4 GetViewMatrix(Transform tr)
        {
            var lookMatrix = Matrix4x4.LookAt(tr.position, tr.position + tr.forward, tr.up);
            var scaleMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1));
            //生成灯光的视角矩阵
            return scaleMatrix * lookMatrix.inverse;
        }

        Matrix4x4 GetViewMatrix(Vector3 position, Vector3 up, Vector3 forward)
        {
            var lookMatrix = Matrix4x4.LookAt(position, position + forward, up);
            var scaleMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1));
            //生成灯光的视角矩阵
            return scaleMatrix * lookMatrix.inverse;
        }

        //void SetCascadeData(int index, Vector4 cullingSphere, float tileSize)
        //{
        //    float texelSize = 2f * cullingSphere.w / tileSize;
        //    float filterSize = texelSize * ((float)settings.directional.filter + 1f);
        //    cullingSphere.w -= filterSize;
        //    cullingSphere.w *= cullingSphere.w;
        //    //cullingSphere.w =
        //    cascadeCullingSpheres[index] = cullingSphere;
        //    cascadeData[index] = new Vector4(
        //        1f / cullingSphere.w,
        //        filterSize * 1.4142136f
        //    );
        //}

        void SetShadowData(int index, Vector2 near_far, float fade)
        {
            csmData[index] = new Vector4(near_far.y, (near_far.y - near_far.x) * fade);
        }

        /// <summary>
        /// 旋转投影矩阵，这个公式能够让世界坐标在投影到阴影空间时可以很方便的和阴影纹理匹配，
        /// 也就是检查Z翻转，进行xyz的0.5缩放，让投影后的坐标直接就能和阴影贴图比较
        /// </summary>
        /// <param name="m">原来的VP矩阵</param>
        /// <param name="offset">偏移大小</param>
        /// <param name="scale">分为多少块</param>
        /// <returns>转移后的矩阵</returns>
        Matrix4x4 ConvertToAtlasMatrix(Matrix4x4 m, Vector2 offset, float scale)
        {
            if (SystemInfo.usesReversedZBuffer)
            {
                m.m20 = -m.m20;
                m.m21 = -m.m21;
                m.m22 = -m.m22;
                m.m23 = -m.m23;
            }
            m.m00 = (0.5f * (m.m00 + m.m30) + offset.x * m.m30) * scale;
            m.m01 = (0.5f * (m.m01 + m.m31) + offset.x * m.m31) * scale;
            m.m02 = (0.5f * (m.m02 + m.m32) + offset.x * m.m32) * scale;
            m.m03 = (0.5f * (m.m03 + m.m33) + offset.x * m.m33) * scale;
            m.m10 = (0.5f * (m.m10 + m.m30) + offset.y * m.m30) * scale;
            m.m11 = (0.5f * (m.m11 + m.m31) + offset.y * m.m31) * scale;
            m.m12 = (0.5f * (m.m12 + m.m32) + offset.y * m.m32) * scale;
            m.m13 = (0.5f * (m.m13 + m.m33) + offset.y * m.m33) * scale;
            m.m20 = 0.5f * (m.m20 + m.m30);
            m.m21 = 0.5f * (m.m21 + m.m31);
            m.m22 = 0.5f * (m.m22 + m.m32);
            m.m23 = 0.5f * (m.m23 + m.m33);
            return m;
        }

        Vector2 SetTileViewport(int index, int split, float tileSize)
        {
            Vector2 offset = new Vector2(index % split, index / split);
            buffer.SetViewport(new Rect(
                offset.x * tileSize, offset.y * tileSize, tileSize, tileSize
            ));
            return offset;
        }

        void SetOtherTileData(int index, Vector2 offset, float scale, float bias)
        {
            float border = atlasSizes.w * 0.5f;
            Vector4 data;
            data.x = offset.x * scale + border;
            data.y = offset.y * scale + border;
            data.z = scale - border - border;
            data.w = bias;
            otherShadowTiles[index] = data;
        }

        void SetKeywords(string[] keywords, int enabledIndex)
        {
            for (int i = 0; i < keywords.Length; i++)
            {
                if (i == enabledIndex)
                {
                    buffer.EnableShaderKeyword(keywords[i]);
                }
                else
                {
                    buffer.DisableShaderKeyword(keywords[i]);
                }
            }
        }

        void ExecuteBuffer()
        {
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }

        //float CaculateShadowMax(Matrix4x4 viewMat)
        //{
        //    float max = float.MinValue;
        //    ClusterRenderSystem system = ClusterCoreCreate.Instance.RenderSystem;
        //    if (system == null) return 0;
        //    for(int i = 0; i < system.ClustersDatas.Length; i++)
        //    {
        //        if (system.ClustersDatas[i].isUsing == 0) continue;
        //        Vector4 point = system.ClustersDatas[i].worldCenter;
        //        point.w = 1;
        //        point = viewMat * point;
        //        max = Mathf.Max(max, point.z);
        //    }
        //    return max;
        //}

    }
}