using System;
using UnityEngine;

namespace KWS
{
    public enum WaterProfileEnum
    {
        Custom,
        Ultra,
        High,
        Medium,
        Low,
        PotatoPC
    }

    /// <summary>
    /// Resolution quality in percent relative to current screen size. For example Medium quality = 35, it's mean ScreenSize * (35 / 100)
    /// </summary>
    public enum ScreenSpaceReflectionResolutionQualityEnum
    {
        Ultra = 75,
        High = 50,
        Medium = 35,
        Low = 25,
        VeryLow = 20,
    }

    public enum CubemapReflectionResolutionQualityEnum
    {
        High = 512,
        Medium = 256,
        Low = 128,
    }

    public enum CausticTextureResolutionEnum
    {
        Ultra = 1024,
        High = 768,
        Medium = 512,
        Low = 384,
        PotatoPC = 256
    }

    public enum CausticMeshResolutionEnum
    {
        Ultra = 384,
        High = 320,
        Medium = 256,
        Low = 192,
        PotatoPC = 128
    }

    [System.Serializable]
    public class WaterSystemSetting
    {
        [Header("Water Setting")]
        public PlaneMeshScaleMode meshScale = PlaneMeshScaleMode.Medium;
        [SerializeField]
        private Vector3 waterScale = new Vector3(1000, -1000, 1000);
        public Vector4 WaterScale
        {
            get
            {
                return new Vector4(waterScale.x, waterScale.z, waterScale.y, Mathf.Abs(waterScale.y));
            }
        }
        public Vector3 WaterScale3 => waterScale;


        //Color Setting
        [Space(10), Header("Color Setting")]
        [Range(0.1f, 50)]
        public float Transparent = 5;
        public Color WaterColor = new Color(175 / 255.0f, 225 / 255.0f, 240 / 255.0f);
        public Color TurbidityColor = new Color(10 / 255.0f, 110 / 255.0f, 100 / 255.0f);
        [Range(0.05f, 1)]
        public float Turbidity = 0.25f;

        //FFT Setting
        [Space(10), Header("FFT Setting")]
        public FFT_GPU.SizeSetting FFT_SimulationSize = FFT_GPU.SizeSetting.Size_256;
        [Range(0, 15)]
        public float WindSpeed = 1.5f;
        [Range(0, 360)]
        public float WindRotation = 0;
        [Range(0, 1)]
        public float WindTurbulence = 0.5f;
        [Range(0, 2)]
        public float TimeScale = 1;

        //Render Setting
        [Space(10), Header("Render Setting")]
        public bool UseFiltering = true;
        public bool UseAnisotropicFiltering = false;

        //Refraction settings
        [Space(10), Header("Refraction settings")]
        //public WaterProfileEnum RefractionProfile = WaterProfileEnum.High;
        public RefractionModeEnum RefractionMode = RefractionModeEnum.PhysicalAproximationIOR;
        public float RefractionAproximatedDepth = 2f;
        public float RefractionSimpleStrength = 0.25f;
        public bool UseRefractionDispersion = true;
        public float RefractionDispersionStrength = 0.35f;

        [Space(10), Header("Reflection settings")]
        public WaterProfileEnum ReflectionProfile = WaterProfileEnum.High;
        public bool UseScreenSpaceReflection = true;
        public ScreenSpaceReflectionResolutionQualityEnum ScreenSpaceReflectionResolutionQuality = ScreenSpaceReflectionResolutionQualityEnum.High;
        public float ScreenSpaceBordersStretching = 0.015f;
        public float ReflectionClipPlaneOffset = 0.0025f;
        [Space(3)]
        public CubemapReflectionResolutionQualityEnum CubemapReflectionResolutionQuality = CubemapReflectionResolutionQualityEnum.Medium;

        public bool ReflectSun = true;
        public float ReflectedSunCloudinessStrength = 0.04f;
        public float ReflectedSunStrength = 1.0f;

        [Space(10), Header("UnderWater Setting")]
        public bool UseUnderwater = false;
        [Space(10), Header("Caustic Setting")]
        public bool UseCaustic = false;
        public float CausticStrength = 1;
        public float CausticDepthScale = 1;
        public CausticTextureResolutionEnum CausticTextureSize = CausticTextureResolutionEnum.Medium;
        public CausticMeshResolutionEnum CausticMeshResolution = CausticMeshResolutionEnum.Medium;
        [Range(1, 4)]
        public int CausticActiveLods = 3;


        [Space(10), Header("VolumetricLight Setting")]
        public bool UseVolumetricLight = false;
        public ScreenSpaceReflectionResolutionQualityEnum VolumetricLightResolutionQuality = ScreenSpaceReflectionResolutionQualityEnum.High;
        public int VolumetricLightIteration = 6;
    }
}