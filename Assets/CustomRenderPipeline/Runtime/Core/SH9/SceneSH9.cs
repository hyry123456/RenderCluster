using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline.MyRenderPipeline
{
    [ExecuteAlways]
    public class SceneSH9 : MonoBehaviour
    {
        // 手动指定的天空盒 Cubemap（兜底用）。开启 autoUseSceneSkybox 时会被「场景真实天空盒」覆盖，
        // 避免误用反射探针 / Unity 默认天空盒（之前 currentSkybox 指向 ReflectionProbe-0.exr，
        // 与场景实际使用的 SunsetSkyboxHDR 不一致，导致 SH9 与反射颜色/亮度都对不上）。
        public Cubemap currentSkybox;

        [Tooltip("是否自动使用场景当前天空盒（RenderSettings.skybox）作为 SH9 / 反射来源。" +
                 "建议保持开启：这样换天空盒时会自动跟随，不会用错来源。")]
        public bool autoUseSceneSkybox = true;

        [Tooltip("间接光（SH9）强度倍率。来源修正后若仍偏亮/偏暗，可在此微调。1 = 原始烘焙值。")]
        [Range(0f, 4f)]
        public float indirectIntensity = 1.0f;

        private AsyncGPUReadbackRequest _gpuReq;
        [SerializeField]
        private List<Vector4> _sh9;

        // 记录上次烘焙依据的 Cubemap 实例 ID，用于「来源改变时自动重烤」，避免一直沿用旧来源烘焙出的 _sh9。
        [SerializeField]
        private int _bakedFromInstanceId = 0;

        private const int Sh9Count = 9;

        private void OnEnable()
        {
            // 优先采用场景真实天空盒（RenderSettings.skybox 材质里的 _Tex Cubemap），
            // 而非可能指向反射探针 / 默认天空盒的手动引用。
            bool resolvedScene = false;
            if (autoUseSceneSkybox)
            {
                var sceneSky = ResolveSceneSkybox();
                if (sceneSky != null)
                {
                    currentSkybox = sceneSky;
                    resolvedScene = true;
                }
            }

            bool needBake;
            if (currentSkybox == null)
            {
                // 没有任何天空盒：不烘焙（避免崩溃），沿用已有 _sh9（可能为空）。
                needBake = false;
            }
            else if (!resolvedScene && _sh9 != null && _sh9.Count == Sh9Count)
            {
                // 没解析到场景天空盒、但已有缓存：先沿用缓存，绝不用旧的手动引用（可能指向反射探针）重烤。
                needBake = false;
            }
            else
            {
                // 需要重烤：还没烘焙过 / 来源（实例 ID）与上次烘焙时不同。
                needBake = (_sh9 == null || _sh9.Count != Sh9Count ||
                            currentSkybox.GetInstanceID() != _bakedFromInstanceId);
            }

            if (needBake)
                Bake();
            else
                UpdateSceneSH9();
        }

        // 从场景当前天空盒材质中取出 Cubemap：
        //   · Skybox/Cubemap 用 _Tex；
        //   · 兜底取 mainTexture。
        // （若是 Skybox/6 Sided 这类无单张 Cubemap 的天空盒，这里取不到，会回退到手动指定的 currentSkybox。）
        // 注意：必须先用 HasProperty 守卫。Unity 内置 Default-Skybox（Skybox/Procedural）
        // 既没有 _Tex 也没有 _MainTex，直接 GetTexture/mainTexture 会报
        // "Material 'Default-Skybox' ... doesn't have a texture property" 错误。
        private Cubemap ResolveSceneSkybox()
        {
            var skyMat = RenderSettings.skybox;
            if (skyMat == null) return null;
            if (skyMat.HasProperty("_Tex"))
            {
                var tex = skyMat.GetTexture("_Tex") as Cubemap;
                if (tex != null) return tex;
            }
            if (skyMat.HasProperty("_MainTex"))
            {
                var main = skyMat.mainTexture as Cubemap;
                if (main != null) return main;
            }
            return null;
        }

        [ContextMenu("Re-Bake Scene SH9")]
        public void Bake()
        {
            if (currentSkybox == null) return;
            var proj = new SH9Project();
            int srcId = currentSkybox.GetInstanceID();
            _gpuReq = proj.FromCubeMapAsync(currentSkybox, (sh9) => {
                _sh9 = new List<Vector4>(sh9);
                _bakedFromInstanceId = srcId;
                UpdateSceneSH9();
            });
        }

        public AsyncGPUReadbackRequest gpuRequest
        {
            get { return _gpuReq; }
        }

        private void UpdateSceneSH9()
        {
            if (_sh9 == null || _sh9.Count == 0) return;
            var scaled = new List<Vector4>(_sh9.Count);
            for (int i = 0; i < _sh9.Count; i++)
                scaled.Add(_sh9[i] * indirectIntensity);
            GPUDravinSystem.SetSceneLighting(scaled, currentSkybox);
        }
    }
}
