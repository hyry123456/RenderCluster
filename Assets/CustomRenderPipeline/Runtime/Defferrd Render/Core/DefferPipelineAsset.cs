using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline.DefferPipeline
{
    //[Serializable]
    //public class SSR_Setting
    //{
    //    [SerializeField]
    //    Shader ssrShader;
    //    Material material;

    //    public Material SSRMat
    //    {
    //        get
    //        {
    //            if(material == null)
    //            {
    //                material = new Material(ssrShader);
    //                return material;
    //            }
    //            else
    //            {
    //                if(material.shader != ssrShader)
    //                    material.shader = ssrShader;
    //                return material;
    //            }
    //        }
    //    }


    //    public bool useSSR;
    //    public int rayMarchingSetp = 64;
    //    [Range(0.0001f, 1f)]
    //    public float bRDF_Bias = 0.8f;
    //    [Range(0.0001f, 1f)]
    //    public float edgeFactor = 0.5f;
    //    /// <summary>/// Ƿ˥ҲǶβ/// </summary>
    //    public bool rayReuse = false;
    //    /// <summary>/// Ƿб׼/// </summary>
    //    public bool useNomraliztion = false;
    //    /// <summary>/// ǷҪө	/// </summary>
    //    public bool reduceFireflies = false;
    //    /// <summary>/// Ƿʹʱ	/// </summary>
    //    public bool useTemporal = false;
    //    public float scale = 0.3f;
    //    [Range(0.85f, 1.0f)]
    //    public float response = 0.9f;
    //    /// <summary>/// ͼֱӰ/// </summary>
    //    public Texture2D Noise;
    //    //ȾȽ޳
    //    public bool useBackClip;
    //}

    [System.Serializable]
    public struct ClusterLightSetting
    {
        public ComputeShader clusterLightCS;
        public bool isUse;
        public bool renderVisableLight;
    }

    [System.Serializable]
    public struct LightSetting
    {
        public ClusterLightSetting clusterLightSetting;
    }


    [Serializable]
    public class AmbientOcclusionSetting
    {
        public bool useAO = false;
        public float intensity = 1;
        public float radius = 2;
        public bool downSample = false;

        [Serializable]
        public enum SimpleCount
        {
            Lowest = 3,
            Low = 6,
            Medium = 10,
            High = 16,
        }
        [SerializeField]
        public SimpleCount simpleCount = SimpleCount.Medium;
    }


    [Serializable]
    public enum DefferDebugMode
    {
        Color,
        Normal,
        Albedo,
        PBRData,
    }

    [Serializable]
    public class DefferSetting
    {
        public ScreenSpaceReflectionSetting ssrSetting = new ScreenSpaceReflectionSetting();
        public AmbientOcclusionSetting ambientOcclusionSetting = new AmbientOcclusionSetting();

        public LightSetting lightingSettings = new LightSetting
        {
            clusterLightSetting = new ClusterLightSetting
            {
                isUse = false,
            },
        };

        // 后处理的开关与抗锯齿已经挪到独立的 PostProcessSettings 资产（见上面的字段），
        // 这里不再持有后处理相关的一切 —— DefferSetting 只留"渲染管线本身的算法开关"。

        public bool accuracyNormal = false;

        [Tooltip("PBR:x:ȣyֲڶȣz")]
        public DefferDebugMode debugMode;

#if UNITY_EDITOR
        public bool renderWire;
#endif

    }

    [CreateAssetMenu(menuName = "Rendering/RenderAsset/Deffer Render Pipeline")]
    public class DefferPipelineAsset : RenderAsset
    {
        [Tooltip("后处理的管线级配置（Game/Scene 开关、抗锯齿）。\n" +
                 "独立资源 ⇒ 方便继续加字段，也不用每次改它都脏化管线资产。\n" +
                 "留空 = 不做后处理（HDR 高光会被硬切，仅用于对比）。\n" +
                 "美术参数（Bloom/暗角/tonemap…）不在这里，在 Volume 里。")]
        public PostProcessSettings postProcessSettings;

        public Shader cameraShader;

        [SerializeField]
        DefferSetting defferSetting = new DefferSetting();
        DefferPipeline defferPipeline;


        private bool CreateDefferPipeline()
        {
            if (defferPipeline != null)
                return true;
            else
            {
                if (cameraShader == null)
                    return false;
                defferPipeline = new DefferPipeline(cameraShader);
                return true;
            }

        }



        public override void Render(ScriptableRenderContext context, Camera camera, 
            int renderLayer)
        {
#if UNITY_EDITOR
            if (defferSetting.renderWire)
            {
                GL.wireframe = true;
            }
            else
                GL.wireframe = false;
#endif
            renderSetting.RenderLayer = renderLayer;
            if (CreateDefferPipeline())
                defferPipeline.Render(context, camera,
                    renderSetting, shadowSetting, defferSetting, postProcessSettings);


        }

        public override void Dispose()
        {
            if (defferPipeline != null)
            {
                defferPipeline.Dispose();
                defferPipeline = null;
            }
        }
    }


}