using UnityEngine;

namespace CustomRenderPipline
{
    [System.Serializable]
    public struct TextureStruct
    {
        public Texture2D texture;
        public int colCount;
        public int rowCount;
    }

    /// <summary>
    /// 粒子工厂的共用数据，用来确定贴图、Buffer
    /// </summary>
    [CreateAssetMenu(menuName = "Rendering/GPUDravin/Factory Common Data")]
    public class FactoryCommonData : ScriptableObject
    {
        public Shader shader;
        public ComputeShader computeShader;

        [SerializeField]
        private TextureStruct[] textures;
        [SerializeField, GradientUsage(true)]
        private Gradient[] gradients;
        [SerializeField]
        private AnimationCurve[] curves;

        public TextureStruct[] Textures => textures;

        public Texture2D[] TextureArray
        {
            get
            {
                Texture2D[] array = new Texture2D[this.textures.Length];
                for (int i = 0; i < array.Length; i++)
                    array[i] = textures[i].texture;
                return array;
            }
        }

        public Gradient[] Gradients => gradients;
        public AnimationCurve[] Curves => curves;

#if UNITY_EDITOR

        public bool isRecaculate;
        private void OnValidate()
        {
            isRecaculate = true;
        }

#endif

    }
}