using System;
using UnityEngine;

namespace Cluster
{
    public struct ClusterRenderData
    {
        private Texture2D[] pbr_texs;
        /// <summary>        /// PBR渲染需要的全部贴图        /// </summary>
        public Texture2D[] PBR_Texs
        {
            get
            {
                return pbr_texs;
            }
            set { pbr_texs = value; }
        }
        public Vector4 uvOffsetScale, detailUVOffset, lightMapScaleOffset;
        public Color emissionCol, mainColor;
        public Vector4 pbrData;     //x:metallic,y:smooth,z:normalScale,w:cullOff
        public Vector4 detailData;  //x:detail Albedo,yw:pack Normal(压缩后的法线), z:detail smooth
        public Mesh shardMesh;
    }

    [Serializable]
    [CreateAssetMenu(menuName = "Rendering/RenderAsset/Cluster Render Data")]
    public class ClusterRenderDataAsset : ScriptableObject
    {
        [SerializeField]
        public Texture2D albedo;
        [SerializeField]
        public Texture2D normal;
        [Tooltip("x:金属度，y:ao,z:detail mask w:光滑度"), SerializeField]
        public Texture2D pbrData;
        [SerializeField]
        public Texture2D emission;
        [Tooltip("R:Detail map albedo，G:Detail normal y, B: Detail smoothness, A:Detail Normal Map x"), SerializeField]
        public Texture2D detail;
        [Min(0f)]
        public float normalScale = 1;
        public Vector2 uv_Offset = Vector2.zero;
        public Vector2 uv_Scale = Vector2.one;
        public Vector4 uv_OffsetScale
        {
            get
            {
                return new Vector4(uv_Offset.x, uv_Offset.y, uv_Scale.x, uv_Scale.y);
            }
        }
        public Vector2 uv_DetailOffset = Vector2.zero;
        public Vector2 uv_DetailScale = Vector2.one;

        [Range(0f, 1.0f)]
        public float metallic = 0.5f;
        [Range(0f, 1.0f)]
        public float roughness = 0.5f;
        [Range(0f, 1.0f)]
        public float cullOff = 0;

        [Range(0f, 1.0f)]
        public float detailAlbedo = 0;
        [Range(0f, 1.0f)]
        public float detailNormal = 0;
        [Range(0f, 1.0f)]
        public float detailRoughness = 0;

        [ColorUsage(false, true)]
        public Color emissionColor = Color.black;
        [ColorUsage(true, true)]
        public Color mainColor = Color.white;
        private Material assetMat;


        public Material AssetMat
        {
            get
            {
                if (assetMat == null)
                {
                    assetMat = new Material(Shader.Find("Unlit/ClusterPBR"));
                    UpdataAssetMat();
                }
                return assetMat;
            }
        }
        
        public TextureMapNode MapNode
        {
            get
            {
                return new TextureMapNode(albedo, normal, pbrData, emission, detail);
            }
        }

        private void OnValidate()
        {
            if(assetMat != null)
                UpdataAssetMat();
        }

        private void UpdataAssetMat()
        {
            assetMat.SetVector("_MainTex_ST", new Vector4(uv_Scale.x, uv_Scale.y, uv_Offset.x, uv_Offset.y));
            assetMat.SetVector("_DetailMap_ST", new
                Vector4(uv_DetailScale.x, uv_DetailScale.y, uv_DetailOffset.x, uv_DetailOffset.y));

            assetMat.SetFloat("_CullOff", cullOff);
            assetMat.SetFloat("_NormalScale", normalScale);
            assetMat.SetFloat("_Metallic", metallic);
            assetMat.SetFloat("_Roughness", roughness);

            assetMat.SetVector("_Color", mainColor);
            assetMat.SetFloat("_DetailAlbedo", detailAlbedo);
            assetMat.SetFloat("_DetailNormal", detailNormal);
            assetMat.SetFloat("_DetailRoughness", detailRoughness);
        }

        //private void OnEnable()
        //{
        //    albedo =    (albedo == null) ? Texture2D.whiteTexture : albedo;
        //    normal =    (normal == null) ? Texture2D.normalTexture : normal;
        //    pbrData =   (pbrData == null) ? Texture2D.whiteTexture : pbrData;
        //    emission =  (emission == null) ? Texture2D.blackTexture : emission;
        //    detail =    (detail == null) ? Texture2D.linearGrayTexture : detail;

        //}

        private void Reset()
        {
            albedo = Texture2D.whiteTexture;
            normal = Texture2D.normalTexture;
            pbrData = Texture2D.whiteTexture;
            emission = Texture2D.blackTexture;
            detail = Texture2D.linearGrayTexture;
            normalScale = 1;
            uv_Offset = Vector2.zero;
            uv_Scale = Vector2.one;
            uv_DetailOffset = Vector2.zero;
            uv_DetailScale = Vector2.one;

            metallic = 0.5f;
            roughness = 0.5f;
            cullOff = 0;

            detailAlbedo = 0;
            detailNormal = 0;
            detailRoughness = 0;

            emissionColor = Color.black;
            mainColor = Color.white;
        }


    }
}