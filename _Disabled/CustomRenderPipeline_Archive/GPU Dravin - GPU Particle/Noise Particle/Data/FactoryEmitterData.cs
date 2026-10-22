using UnityEngine;

namespace CustomRenderPipline
{
    public enum BeginSpeedMode
    {
        FollowSpeed = -1,
        OppositeSpeed = 1,
    }

    [CreateAssetMenu(menuName = "Rendering/GPUDravin/Factory Emitter Data")]
    public class FactoryEmitterData : ScriptableObject
    {
        public FactoryCommonData commonData;
        //粒子的位置初始化模式
        public InitialShapeMode particleShadpeMode = InitialShapeMode.Pos;
        //粒子的速度初始化模式
        public SpeedBeginMode particleSpeedMode = SpeedBeginMode.JustBeginSpeed;
        //释放器移动时对初始速度的调整模式
        public BeginSpeedMode beginMode = BeginSpeedMode.OppositeSpeed;
        //释放器移动时设置速度的缩放值
        [Range(0.0f, 1f)]
        public float beginSpeedScale = 0.5f;
        [Min(0.01f)]
        public float sustainTime = 1;
        [Min(0.01f)]
        public float releaseTime = 1;
        [Min(0.01f)]
        public float beginSpeed;        //初始速度大小，控制粒子的发散速度

        [Range(0, 6.28f)]
        public float arc = 1;
        [Min(0.01f)]
        public float size = 1;
        [Min(0.01f)]
        public float frequency = 1;
        [Min(0.01f)]
        public float intensity = 1;
        [Range(0, 8)]
        public int octave = 1;
        public bool useGravity;
        public bool fellowSpeed;
        public int particleCount = 1;
        public Vector2 sizeRange;
        public Vector2 speedRange;
        public int textureIndex;
        public int gradientIndex;
        public int curveIndex;

        //是否需要释放等待，还是直接逐帧释放
        public bool isReleasingWait;


#if UNITY_EDITOR
        public Gradient color;
        public AnimationCurve curve;
        public Texture2D texture;

        private void OnValidate()
        {
            if(commonData == null)
            {
                Debug.LogError("Not set common Data");
                return;
            }
            sizeRange.x = Mathf.Max(0, sizeRange.x);
            sizeRange.y = Mathf.Max(sizeRange.x, sizeRange.y);
            speedRange.y = Mathf.Max(speedRange.x, speedRange.y);

            if(commonData.Textures == null || commonData.Gradients == null || commonData.Curves == null)
            {
                textureIndex = 0;
                curveIndex = 0;
                gradientIndex = 0;
                return;
            }
            textureIndex = Mathf.Clamp(textureIndex, 0, commonData.Textures.Length - 1);
            gradientIndex = Mathf.Clamp(gradientIndex, 0, commonData.Gradients.Length - 1);
            curveIndex = Mathf.Clamp(curveIndex, 0, commonData.Curves.Length - 1);
            particleCount = Mathf.Max(0, particleCount);

            color.colorKeys = commonData.Gradients[gradientIndex].colorKeys;
            color.alphaKeys = commonData.Gradients[gradientIndex].alphaKeys;
            curve.keys = commonData.Curves[curveIndex].keys;
            texture = commonData.Textures[textureIndex].texture;
        }

#endif

    }
}