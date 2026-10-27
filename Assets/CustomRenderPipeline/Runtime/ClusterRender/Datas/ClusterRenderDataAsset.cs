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
        /// <summary>
        /// Alpha Cutout 开关的【真值】，从源材质 _CLIPPING keyword 搬运而来。
        /// 注意：cullOff 只是阈值（每个材质默认 0.5），"有阈值 ≠ 该裁"；
        /// 真正决定是否裁切的是 _CLIPPING keyword，必须用这个布尔判断，不能用 cullOff > 0。
        /// （与 GetOrCreateClusterPBRMaterial 里 src.IsKeywordEnabled("_CLIPPING") 的判定保持一致。）
        /// </summary>
        public bool clipping = false;

        /// <summary>
        /// 是否「需要 alpha cutout（镂空）」的显式开关：草/蕨/灌木这类靠叶片卡牌 + 透明通道抠图的物体。
        /// Cluster 阴影 shader 是纯几何、不采样贴图，无法做镂空 ⇒ 这类物体必须走 SRP 阴影路径
        /// （其 ShadowCaster 支持 _CLIPPING，镂空正确），否则会画成实心方块阴影且巨耗。
        /// 路由条件取 cutoff || clipping：已有镂空材质（clipping=true）自动走 SRP，无需逐个改资产；
        /// 这个开关让你能单独覆盖（例如某镂空材质也想强行走 Cluster 时可关掉本开关）。
        /// </summary>
        public bool cutoff = false;

        /// <summary>是否需走 SRP 阴影路径：cutoff 开关打开，或本身是 alpha 镂空材质（clipping）。</summary>
        public bool NeedsSRPShadow => cutoff || clipping;

        /// <summary>
        /// 源材质是否真的开了 _MASK_MAP keyword 的【真值】。
        /// 与 <see cref="clipping"/> 同理：不能看 pbrData 是不是 null（资产里可能填了别的东西），
        /// 也不能看 cullOff 阈值。必须用 keyword 真值。
        /// 用途：RVT 路径按它决定「mask 图集要不要采」——
        /// 没有 mask map 时 mask 必须视为白(1)，否则 metallic/roughness/AO 会被无关贴图带偏
        /// （历史上 pbrData 填成高光图 ⇒ AO≈0 ⇒ 物体在延迟管线里直接变纯黑）。
        /// </summary>
        public bool hasMaskMap = false;

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
                return new TextureMapNode(albedo, normal, pbrData, emission);
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