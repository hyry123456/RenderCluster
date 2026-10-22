using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    /// <summary>
    /// 粒子蝴蝶，对输出的粒子定点进行一些其他操作
    /// </summary>
    public class NoiseButterFlies : GPUDravinBase, IDrawGBuffer, IDrawShadow
    {

        public ComputeShader compute;
        public Shader shader;
        public int groupCount = 10,     //粒子组总共数量
            perReleaseGroup = 1;            //每一次释放的粒子数量
        public NoisePerNodeData noiseData;

        public Texture2D mainTex;
        public int rowCount = 1;
        public int columnCount = 1;
        public bool particleFollowSpeed;

        public bool useNearAlpha = false;
        public float nearFadeDistance = 1;
        public float nearFadeRange = 1;

        public bool useSoftParticle = false;
        public float softParticleDistance = 1;
        public float softParticleRange = 1;

        public bool useDistorion = false;
        public float distorionSize = 0.1f;
        public float distorionBlend = 0.15f;
        public Texture2D distorionTex;

        float time;
        private Material material;

        NoiseParticle particleCore;

        /// <summary> /// 是否需要开启碰撞  /// </summary>
        public bool useCollsion;
        [SerializeField]
        public List<IGetCollsion> collsions;    //所有碰撞器数据


        [SerializeField]
        bool isAutoPlay;
        public bool IsAutoPlay
        {
            get { return isAutoPlay; }
            set { isAutoPlay = value; }
        }

        private void Start()
        {
            //粒子本身不需要动画，由渲染决定图片块的选择
            if (useCollsion)
                particleCore = new NoiseParticle(compute, collsions, noiseData, groupCount,
                    1, 1);
            else
                particleCore = new NoiseParticle(compute, noiseData, groupCount, 1, 1);
            ReadyMaterial();

            time = noiseData.releaseTime;
            if (drawNode == null)
            {
                drawNode = new DrawNode();
                drawNode.gBufferEvent = DrawGBuffer;
                drawNode.shadowEvent = DrawShadow;
            }


            GPUDravinSystem.Instance.InsertRender(drawNode);
        }

        private void OnDestroy()
        {
            GPUDravinSystem.Instance.RemoveRender(drawNode);
        }

        private void Update()
        {
            //等待粒子删除
            if (IsAutoPlay)
            {
                time += Time.deltaTime;
                if (time > noiseData.releaseTime * 0.9f)
                {
                    particleCore.ReleaseOneCount(perReleaseGroup);
                    time = 0;
                }
            }
            particleCore.OnUpdata(transform);
        }

        private void FixedUpdate()
        {
            particleCore.OnFix(transform);
        }


#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (noiseData == null)
                return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.red;
            if (noiseData.shapeMode == InitialShapeMode.Cube)
            {
                Gizmos.DrawWireCube(Vector3.zero, noiseData.cubeRange);
            }
            else if (noiseData.shapeMode == InitialShapeMode.Sphere)
                Gizmos.DrawWireSphere(Vector3.zero, noiseData.radius);
        }

        private void OnValidate()
        {
            if (drawNode == null) return;
            particleCore.Dispose();
            Start();
        }
#endif

        private void ReadyMaterial()
        {
            material = new Material(shader);
            if (useSoftParticle)
            {
                material.EnableKeyword("_SOFT_PARTICLE");
                material.SetFloat("_SoftParticlesDistance", softParticleDistance);
                material.SetFloat("_SoftParticlesRange", softParticleRange);
            }
            else
                material.DisableKeyword("_SOFT_PARTICLE");

            if (useNearAlpha)
            {
                material.EnableKeyword("_NEAR_ALPHA");
                material.SetFloat("_NearFadeDistance", nearFadeDistance);
                material.SetFloat("_NearFadeRange", nearFadeRange);
            }
            else
                material.DisableKeyword("_NEAR_ALPHA");

            if (useDistorion)
            {
                material.EnableKeyword("_DISTORTION");
                material.SetFloat("_DistortionSize", distorionSize);
                material.SetFloat("_DistortionBlend", distorionBlend);
                material.SetTexture("_DistortionTex", distorionTex);
            }
            else
                material.DisableKeyword("_DISTORTION");

            material.SetTexture("_MainTex", mainTex);
            material.SetInt("_RowCount", rowCount);
            material.SetInt("_ColCount", columnCount);
            material.SetFloat("_TexAspectRatio", (float)mainTex.width / mainTex.height);
        }




        public void DrawGBuffer(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {

        }

        public void DrawShadow(ScriptableRenderContext context, CommandBuffer buffer)
        {
            //未启动，且不是因为等待关闭时造成的为未启用才退出
            material.SetBuffer("_ParticleNoiseBuffer", particleCore.ParticleBuffer);

            buffer.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Points,
                1, particleCore.ParticleBuffer.count);
            ExecuteBuffer(ref buffer, context);
            return;
        }
    }
}