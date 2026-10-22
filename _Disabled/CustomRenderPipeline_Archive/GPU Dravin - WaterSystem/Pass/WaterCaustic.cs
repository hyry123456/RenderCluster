using UnityEngine;
using static KWS.KWS_ShaderConstants;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using CustomRenderPipline;
using CustomRenderPipline.DefferPipeline;

namespace KWS
{
    public class WaterCaustic
    {
        class CausticData
        {
            public Material PassMaterial;
            public Material DecalMaterial;

            public RenderTexture[] Lods = new RenderTexture[4];

            public void InitializeMaterials()
            {
                PassMaterial = new Material(Shader.Find(ShaderNames.CausticComputeShaderName));
                DecalMaterial = new Material(Shader.Find(ShaderNames.CausticDecalShaderName));
            }

            public void InitializeTextures(WaterSystem waterInstance)
            {
                int size = (int)waterInstance.Settings.CausticTextureSize;
                for (var i = 0; i < waterInstance.Settings.CausticActiveLods; i++)
                {
                    Lods[i] = RenderTexture.GetTemporary(size, size, 0, GraphicsFormat.R8_UNorm);
                    Lods[i].name = $"CausticLod{i}";
                    waterInstance.SharedData.CausticLods[i] = Lods[i];
                }
            }

            public void Release()
            {
                //KW_Extensions.SafeDestroy(PassMaterial, DecalMaterial, Lods[0], Lods[1], Lods[2], Lods[3]);
                Lods[0]?.Release();
                Lods[1]?.Release();
                Lods[2]?.Release();
                Lods[3]?.Release();
            }
        }

        private Mesh _causticMesh;
        private Mesh _decalMesh;
        CausticData causticData;
        public WaterCaustic(WaterSystem waterInstance)
        {
            _causticMesh = GeneratePlane(_causticMesh, (int)waterInstance.Settings.CausticMeshResolution, 1.2f);
        }

        public void ExecuteInstance(Camera cam, CommandBuffer cmd, WaterSystem waterInstance)
        {
            cmd.BeginSample("Water Caustic");
            ComputeCaustic(cam, cmd, waterInstance);
            DrawCausticDecal(cam, cmd, waterInstance);
            cmd.EndSample("Water Caustic");
        }

        public void Release()
        {
            KW_Extensions.SafeDestroy(_causticMesh, _decalMesh);
            causticData?.Release();
        }

        public void OnChange(WaterSystemSetting Settings)
        {
            if (Settings.UseCaustic)
            {
                causticData.DecalMaterial.SetFloat(CausticID.KW_DecalScale, KWS_Settings.Caustic.LodSettings[Settings.CausticActiveLods - 1] * 2);
                KWS_CoreUtils.SetKeywords(causticData.DecalMaterial,
                                          (CausticKeywords.USE_LOD1, Settings.CausticActiveLods == 2),
                                          (CausticKeywords.USE_LOD2, Settings.CausticActiveLods == 3),
                                          (CausticKeywords.USE_LOD3, Settings.CausticActiveLods == 4));
            }
        }


        void ComputeCaustic(Camera cam, CommandBuffer cmd, WaterSystem waterInstance)
        {
            var causticData = GetCausticData(waterInstance);

            var camPos = cam.transform.position;
            var camDir = cam.transform.forward;

            //cmd.SetKeyword(CausticKeywords.USE_DEPTH_SCALE, waterInstance.Settings.UseDepthCausticScale);
            waterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.FFT);

            //if (waterInstance.Settings.UseCausticDispersion)
            //{
                //var dispersionStrength = 1 - (Mathf.RoundToInt(Mathf.Log((int)waterInstance.Settings.FFT_SimulationSize, 2)) - 5) / 4.0f; // 0 - 4 => 1-0
                //if (dispersionStrength > 0.1f)
                //{
                //    dispersionStrength = Mathf.Lerp(dispersionStrength * 0.5f, dispersionStrength * 2, (int)waterInstance.Settings.CausticTextureSize / 1024f);
                //    causticData.DecalMaterial.SetFloat(CausticID.KW_CausticDispersionStrength, dispersionStrength);
                //}
                //    causticData.DecalMaterial.SetFloat(CausticID.KW_CausticDispersionStrength, 0.4f);
            //}
            //cmd.SetKeyword(CausticKeywords.USE_CAUSTIC_FILTERING, waterInstance.Settings.UseCausticBicubicInterpolation);
            cmd.SetGlobalFloat(CausticID.KW_CaustisStrength, waterInstance.Settings.CausticStrength);
            cmd.SetGlobalFloat(CausticID.KW_CausticDepthScale, waterInstance.Settings.CausticDepthScale);
            cmd.SetKeyword(CausticKeywords.USE_DISPERSION, false);

            //if (waterInstance.Settings.UseShorelineRendering) waterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.Shoreline);

            var lodSettings = KWS_Settings.Caustic.LodSettings;
            RenderLod(causticData.PassMaterial, cmd, camPos, camDir, lodSettings.x, causticData.Lods[0]);
            if (waterInstance.Settings.CausticActiveLods > 1) RenderLod(causticData.PassMaterial, cmd, camPos, camDir, lodSettings.y, causticData.Lods[1]);
            if (waterInstance.Settings.CausticActiveLods > 2) RenderLod(causticData.PassMaterial, cmd, camPos, camDir, lodSettings.z, causticData.Lods[2]);
            if (waterInstance.Settings.CausticActiveLods > 3) RenderLod(causticData.PassMaterial, cmd, camPos, camDir, lodSettings.w, causticData.Lods[3]);

            waterInstance.SharedData.CausticActiveLods = waterInstance.Settings.CausticActiveLods;
        }

        void RenderLod(Material passMaterial, CommandBuffer cmd, Vector3 camPos, Vector3 camDir, float lodSize, RenderTexture target)
        {
            var bakeCamPos = camPos + camDir * lodSize * 0.5f;

            cmd.SetGlobalFloat(CausticID.KW_MeshScale, lodSize);
            cmd.SetGlobalVector(CausticID.KW_CausticCameraOffset, bakeCamPos);

            cmd.SetRenderTarget(target);
            cmd.ClearRenderTarget(false, true, Color.black);

            cmd.DrawMesh(_causticMesh, Matrix4x4.identity, passMaterial, 0);
        }

        CausticData GetCausticData(WaterSystem waterInstance)
        {
            if(causticData == null)
            {
                causticData = new CausticData();
                causticData.InitializeMaterials();
                causticData.InitializeTextures(waterInstance);
                OnChange(waterInstance.Settings);
            }
            return causticData;
        }

        void DrawCausticDecal(Camera cam, CommandBuffer cmd, WaterSystem waterInstance)
        {
            var camPos = cam.transform.position;
            var causticData = GetCausticData(waterInstance);

            var decalScale = KWS_Settings.Caustic.LodSettings[waterInstance.Settings.CausticActiveLods - 1] * 2;
            var decalPos = camPos;
            decalPos.y = waterInstance.WaterRelativeWorldPosition.y - (KWS_Settings.Caustic.CausticDecalHeight * 0.5f - 2);

            var decalMaterial = causticData.DecalMaterial;

            cmd.SetGlobalVector(CausticID.KW_CausticLodOffset, cam.transform.forward * 0.5f);
            cmd.SetGlobalFloat(CausticID.KW_CaustisStrength, waterInstance.Settings.CausticStrength);
            //if (waterInstance.Settings.UseCausticDispersion)
            //{
            //    var dispersionStrength = 1 - (Mathf.RoundToInt(Mathf.Log((int)waterInstance.Settings.FFT_SimulationSize, 2)) - 5) / 4.0f; // 0 - 4 => 1-0
            //    if (dispersionStrength > 0.1f)
            //    {
            //        dispersionStrength = Mathf.Lerp(dispersionStrength * 0.5f, dispersionStrength * 2, waterInstance.Settings.CausticTextureSize / 1024f);
            //        decalMaterial.SetFloat(CausticID.KW_CausticDispersionStrength, dispersionStrength);
            //    }
            //}
            //cmd.SetKeyword(CausticKeywords.USE_DISPERSION, waterInstance.Settings.UseCausticDispersion);
            waterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.Caustic);
            cmd.SetGlobalInt(DynamicWaterParams.KWS_WaterPassID, waterInstance.SharedData.WaterShaderPassID);

            //if (waterInstance.Settings.UseDynamicWaves) waterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.DynamicWaves);

            var decalTRS = Matrix4x4.TRS(decalPos, Quaternion.identity, new Vector3(decalScale, KWS_Settings.Caustic.CausticDecalHeight, decalScale));
            if (_decalMesh == null) GenerateDecalMesh();

            //OnRenderToCameraTarget?.Invoke(cmd);
            cmd.SetRenderTarget(
                ShaderProperty.CameraColorTexId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                DefferProperty.GBufferDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            cmd.DrawMesh(_decalMesh, decalTRS, decalMaterial);

        }


        Mesh GeneratePlane(Mesh mesh, int meshResolution, float scale)
        {
            if (mesh == null)
            {
                mesh = new Mesh();
                mesh.hideFlags = HideFlags.DontSave;
                mesh.indexFormat = IndexFormat.UInt32;
            }

            var vertices = new Vector3[(meshResolution + 1) * (meshResolution + 1)];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[meshResolution * meshResolution * 6];

            for (int i = 0, y = 0; y <= meshResolution; y++)
                for (var x = 0; x <= meshResolution; x++, i++)
                {
                    vertices[i] = new Vector3(x * scale / meshResolution - 0.5f * scale, y * scale / meshResolution - 0.5f * scale, 0);
                    uv[i] = new Vector2(x * scale / meshResolution, y * scale / meshResolution);
                }

            for (int ti = 0, vi = 0, y = 0; y < meshResolution; y++, vi++)
                for (var x = 0; x < meshResolution; x++, ti += 6, vi++)
                {
                    triangles[ti] = vi;
                    triangles[ti + 3] = triangles[ti + 2] = vi + 1;
                    triangles[ti + 4] = triangles[ti + 1] = vi + meshResolution + 1;
                    triangles[ti + 5] = vi + meshResolution + 2;
                }

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            return mesh;
        }

        void GenerateDecalMesh()
        {
            Vector3[] vertices =
            {
                new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, 0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f),
                new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f),
            };

            int[] triangles =
            {
                0, 2, 1, //face front
                0, 3, 2,
                2, 3, 4, //face top
                2, 4, 5,
                1, 2, 5, //face right
                1, 5, 6,
                0, 7, 4, //face left
                0, 4, 3,
                5, 4, 7, //face back
                5, 7, 6,
                0, 6, 7, //face bottom
                0, 1, 6
            };

            if (_decalMesh == null)
            {
                _decalMesh = new Mesh();
                _decalMesh.hideFlags = HideFlags.DontSave;
            }

            _decalMesh.Clear();
            _decalMesh.vertices = vertices;
            _decalMesh.triangles = triangles;
            _decalMesh.RecalculateNormals();
        }
    }
}