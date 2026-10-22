using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    class ExectionProverty
    {
        public static int
            LocalToWorld = Shader.PropertyToID("_LocalToWorld"),
            CubeRange = Shader.PropertyToID("_CubeRange"),
            Arc = Shader.PropertyToID("_Arc"),
            Radius = Shader.PropertyToID("_Radius"),
            BeginSpeed = Shader.PropertyToID("_BeginSpeed"),
            LifeTime = Shader.PropertyToID("_LifeTime"),
            Mode = Shader.PropertyToID("_Mode"),
            Gravity = Shader.PropertyToID("_Gravity"),
            Frequency = Shader.PropertyToID("_Frequency"),
            Octave = Shader.PropertyToID("_Octave"),
            Intensity = Shader.PropertyToID("_Intensity"),
            SizeRange = Shader.PropertyToID("_SizeRange"),
            AspectRatio = Shader.PropertyToID("_AspectRatio"),
            MainTex = Shader.PropertyToID("_MainTex"),
            SpeedDamping = Shader.PropertyToID("_SpeedDamping"),

            SizesBuffer = Shader.PropertyToID("_SizesBuffer"),
            ColorsBuffer = Shader.PropertyToID("_ColorsBuffer"),
            AlphasBuffer = Shader.PropertyToID("_AlphasBuffer");
    }

    enum ShapeMode
    {
        Point = 0,
        Sphere = 1,
        Cube = 2,
    }

    [CreateAssetMenu(menuName = "Rendering/Particle System/NoiseExection")]
    /// <summary>  /// 噪声粒子执行器  /// </summary>
    public class NoiseExection : ParticleExecutionBase
    {
        public Material originMat;
        private Material drawMat;
        public Material Mat
        {
            get
            {
                if (drawMat == null)
                    drawMat = new Material(originMat);
#if UNITY_EDITOR
                drawMat.CopyPropertiesFromMaterial(originMat);
#endif

                return drawMat;
            }
        }

        [SerializeField]
        private ShapeMode shapeMode;
        public SpeedBeginMode beginSpeedMode = SpeedBeginMode.JustBeginSpeed;
        public GravityMode gravityMode;
        public SizeBySpeedMode sizeMode;
        public bool isOpaque;
        public bool renderShadow;

        /// <summary>  /// 粒子平面大小，也就是宽/高   /// </summary>
        [Min(0.01f)]
        public float AspectRatio = 1;
        public Texture mainTex;
        [Min(1)]
        public int rowCount = 1;
        [Min(1)]
        public int colCount = 1;

        public Vector3 CubeRange = Vector3.one;
        [Range(0, 6.28f)]
        public float Arc = 1;
        [Min(0.01f)]
        public float Radius = 1;

        public Vector3 beginSpeed = Vector3.zero;
        [Min(0.1f)]
        public float lifeTime = 1;

        public Vector4 Gravity;

        [Min(0.01f)]
        public float Frequency;
        [Range(1, 8)]
        public int Octave = 3;
        [Min(0.01f)]
        public float Intensity = 1;
        [Range(0, 0.9f)]
        public float speedDamping = 0.1f;       //速度衰减

        public Vector2 sizeRange = Vector2.up;
        public Vector2 speedToSizeRange = Vector2.one;

        [GradientUsage(true)]
        public Gradient colors;
        public AnimationCurve sizeCurve;

        private ComputeBuffer colorsBuffer;
        private ComputeBuffer alphasBuffer;
        private ComputeBuffer sizesBuffer;


        private ComputeBuffer ColorsBuffer
        {
            get
            {
                if(colorsBuffer == null)
                    colorsBuffer = GetColorBuffers(colors);
                return colorsBuffer;
            }
        }

        private ComputeBuffer AlphasBuffer
        {
            get
            {
                if (alphasBuffer == null)
                    alphasBuffer = GetAlphaBuffers(colors);
                return alphasBuffer;
            }
        }

        private ComputeBuffer SizesBuffer
        {
            get
            {
                if (sizesBuffer == null)
                    sizesBuffer = GetSizeBuffers(sizeCurve);
                return sizesBuffer;
            }
        }

        public override int EnsureBufferSize(float releaseCount)
        {
            return Mathf.CeilToInt((lifeTime + 1) * releaseCount);
        }

        public override void SetUpdateProperty(Transform transform)
        {
            Compute.SetMatrix(ExectionProverty.LocalToWorld, transform.localToWorldMatrix);
            Compute.SetVector(ExectionProverty.CubeRange, CubeRange);
            Compute.SetFloat(ExectionProverty.Arc, Arc);
            Compute.SetFloat(ExectionProverty.Radius, Radius);
            Compute.SetVector(ExectionProverty.BeginSpeed, transform.TransformDirection(beginSpeed));
            Compute.SetFloat(ExectionProverty.LifeTime, lifeTime);
            Compute.SetInt(ParticleProperty.Row, rowCount);
            Compute.SetInt(ParticleProperty.Column, colCount);

            Compute.SetInts(ExectionProverty.Mode, new int[]
            {
                (int)shapeMode, (int)beginSpeedMode, (int)gravityMode, (int)sizeMode
            });

            Compute.SetVector(ExectionProverty.SizeRange, 
                new Vector4(sizeRange.x, sizeRange.y, speedToSizeRange.x, speedToSizeRange.y));

            Compute.SetBuffer(0, ExectionProverty.SizesBuffer, SizesBuffer);
            Compute.SetBuffer(0, ExectionProverty.ColorsBuffer, ColorsBuffer);
            Compute.SetBuffer(0, ExectionProverty.AlphasBuffer, AlphasBuffer);
        }

        public override void DrawTransparent(ScriptableRenderContext context, 
            CommandBuffer buffer, Camera camera, ParticleSystemData buffers)
        {
            Material material = Mat;
            buffer.SetGlobalBuffer(ParticleProperty.ParticlesBuffer, buffers.particlesBuffer);
            if(buffers.particlesIndexsBuffer != null)
            {
                buffer.SetGlobalBuffer(ParticleProperty.ParticlesIndexsBuffer, buffers.particlesIndexsBuffer);
                material.EnableKeyword("_SORT_PARTICLE");
            }
            else
                material.DisableKeyword("_SORT_PARTICLE");

            buffer.SetGlobalInt(ParticleProperty.Row, rowCount);
            buffer.SetGlobalInt(ParticleProperty.Column, colCount);
            buffer.SetGlobalFloat(ExectionProverty.AspectRatio, AspectRatio);
            buffer.SetGlobalTexture(ExectionProverty.MainTex, mainTex);

            buffer.DrawProcedural(Matrix4x4.identity,
                material, 0, MeshTopology.Points, buffers.particlesBuffer.count, 1);
        }

        public override CustomRP_DrawMode GetDrawMode()
        {
            CustomRP_DrawMode mode = CustomRP_DrawMode.None;
            if(isOpaque)
                mode |= CustomRP_DrawMode.GBuffer;
            else
                mode |= CustomRP_DrawMode.Transparent;

            if (renderShadow)
                mode = mode | CustomRP_DrawMode.Shadow;
            return mode;
        }

        public override void SetFixUpdateProperty(Transform transform)
        {
            Compute.SetInts(ExectionProverty.Mode, new int[]
            {
                (int)shapeMode, (int)beginSpeedMode, (int)gravityMode, (int)sizeMode
            });
            Compute.SetMatrix(ExectionProverty.LocalToWorld, transform.localToWorldMatrix);

            Compute.SetVector(ExectionProverty.Gravity, Gravity);
            Compute.SetFloat(ExectionProverty.Frequency, Frequency);
            Compute.SetFloat(ExectionProverty.Intensity, Intensity);
            Compute.SetInt(ExectionProverty.Octave, Octave);
            Compute.SetFloats(ExectionProverty.SpeedDamping, speedDamping);
        }

        public override void DrawGBuffer(ScriptableRenderContext context, CommandBuffer buffer, Camera camera, ParticleSystemData buffers)
        {
            Material material = Mat;
            material.SetBuffer(ParticleProperty.ParticlesBuffer, buffers.particlesBuffer);
            material.SetInt(ParticleProperty.Row, rowCount);
            material.SetInt(ParticleProperty.Column, colCount);
            material.SetTexture(ExectionProverty.MainTex, mainTex);

            buffer.DrawProcedural(Matrix4x4.identity,
                material, 0, MeshTopology.Points, buffers.particlesBuffer.count, 1);
        }

        public override void DrawShadow(ScriptableRenderContext context, CommandBuffer buffer, ParticleSystemData buffers)
        {
            Material material = Mat;
            material.SetBuffer(ParticleProperty.ParticlesBuffer, buffers.particlesBuffer);
            material.SetInt(ParticleProperty.Row, rowCount);
            material.SetInt(ParticleProperty.Column, colCount);
            material.SetTexture(ExectionProverty.MainTex, mainTex);

            buffer.DrawProcedural(Matrix4x4.identity,
                material, 1, MeshTopology.Points, buffers.particlesBuffer.count, 1);
        }


#if UNITY_EDITOR

        private void OnValidate()
        {
            colorsBuffer?.Release();
            alphasBuffer?.Release();
            sizesBuffer?.Release();
            colorsBuffer = alphasBuffer = sizesBuffer = null;
        }

        public override void DrawGizmos(Transform transform)
        {
            if(shapeMode == ShapeMode.Sphere)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(Vector3.zero, Radius);
            }
            else if(shapeMode == ShapeMode.Cube)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.color = Color.red;
                Gizmos.DrawWireCube(Vector3.zero, CubeRange);
            }
            if (shapeMode == ShapeMode.Point && beginSpeedMode != SpeedBeginMode.JustBeginSpeed)
                Debug.LogError("速度设置错误，Point仅支持仅速度");
        }

#endif
    }
}