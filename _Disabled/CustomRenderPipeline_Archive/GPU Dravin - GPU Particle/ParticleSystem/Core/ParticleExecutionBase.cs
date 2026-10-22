using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    /// <summary>
    /// 粒子执行器基类，用来定义具体执行器的基本方法
    /// </summary>
    public abstract class ParticleExecutionBase : ScriptableObject
    {
        [SerializeField]
        protected ComputeShader compute;
        public ComputeShader Compute => compute;

        /// <summary>
        /// 返回需要入栈的渲染模式，用按位或来多次叠加
        /// </summary>
        public abstract CustomRP_DrawMode GetDrawMode();

        public bool IsRunning {
            get
            {
                if(compute == null)
                    return false;
                return true;
            }
        }

        /// <summary>
        /// 根据当前设置的粒子存活时间以及粒子释放速度确定粒子的Buffer大小
        /// </summary>
        /// <param name="releaseCount">每秒释放的数量</param>
        /// <returns>buffer大小</returns>
        public abstract int EnsureBufferSize(float releaseCount);

        /// <summary>
        /// 设置Compute Shader的逐帧函数数据
        /// </summary>
        public abstract void SetUpdateProperty(Transform transform);

        /// <summary>
        /// 设置Compute Shader的逐固定帧数据
        /// </summary>
        public abstract void SetFixUpdateProperty(Transform transform);

        protected ComputeBuffer GetColorBuffers(Gradient gradient)
        {
            GradientColorKey[] colorKeys = gradient.colorKeys;
            Vector4[] colors = new Vector4[colorKeys.Length];
            for(int i =0; i< colorKeys.Length; i++)
            {
                colors[i] = colorKeys[i].color;
                colors[i].w = colorKeys[i].time;
            }
            ComputeBuffer buffer = new ComputeBuffer(colorKeys.Length, 16);
            buffer.SetData(colors);
            return buffer;
        }

        protected ComputeBuffer GetAlphaBuffers(Gradient gradient)
        {
            GradientAlphaKey[] alphaKeys = gradient.alphaKeys;
            Vector2[] alphas = new Vector2[alphaKeys.Length];
            for (int i = 0; i < alphaKeys.Length; i++)
            {
                alphas[i].x = alphaKeys[i].alpha;
                alphas[i].y = alphaKeys[i].time;
            }
            ComputeBuffer buffer = new ComputeBuffer(alphaKeys.Length, 8);
            buffer.SetData(alphas);
            return buffer;
        }

        protected ComputeBuffer GetSizeBuffers(AnimationCurve curve)
        {
            Vector4[] sizes = new Vector4[curve.keys.Length];

            for(int i = 0; i < sizes.Length; i++)
            {
                sizes[i] = new Vector4(curve.keys[i].time, curve.keys[i].value,
                    curve.keys[i].inTangent, curve.keys[i].outTangent);
            }
            ComputeBuffer buffer = new ComputeBuffer(sizes.Length, 4 * 4);
            buffer.SetData(sizes);
            return buffer;
        }


        public virtual void DrawTransparent(ScriptableRenderContext context, 
            CommandBuffer buffer, Camera camera, ParticleSystemData buffers)
        {

        }

        public virtual void DrawShadow(ScriptableRenderContext context, CommandBuffer buffer, ParticleSystemData buffers)
        {
        }

        public virtual void DrawGBuffer(ScriptableRenderContext context, 
            CommandBuffer buffer, Camera camera, ParticleSystemData buffers)
        {
        }

        public virtual void DrawOpaque(ScriptableRenderContext context, CommandBuffer buffer, 
            Camera camera, ParticleSystemData buffers)
        {

        }

        public virtual void Setup(ScriptableRenderContext context, CommandBuffer buffer, 
            Camera camera, ParticleSystemData buffers)
        {

        }

        public virtual void CleanUp(ScriptableRenderContext context, CommandBuffer buffer, 
            ParticleSystemData buffers)
        {

        }

#if UNITY_EDITOR
        public abstract void DrawGizmos(Transform transform);
#endif
    }
}