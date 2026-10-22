using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{

    public enum DefferedQueue
    {
        All = int.MinValue,
        GBuffer = 1,
        Color = 2,
    }

    /// <summary>
    /// 粒子噪声类，用来绘制通用的一般粒子的效果
    /// </summary>
    public class NoiseParticleSimple : ParticleBase, IDrawTransparent, IDrawGBuffer
    {
        public ComputeShader compute;
        public Material oriMat;
        public DefferedQueue queue = DefferedQueue.Color;
        /// <summary>
        /// 在该队列中的偏移值
        /// </summary>
        public int QueueOffset = 0;
        public int groupCount = 10,     //粒子组总共数量
            perReleaseGroup = 1;            //每一次释放的粒子数量
        public NoisePerNodeData noiseData;

        public Texture2D mainTex;
        public int rowCount = 1;
        public int columnCount = 1;
        public bool particleFollowSpeed;

        /// <summary> /// 是否需要开启碰撞  /// </summary>
        public bool useCollsion;
        [SerializeField]
        public List<IGetCollsion> collsions;    //所有碰撞器数据

        float time;
        private Material renderMat;

        NoiseParticle particleCore;


        protected override void Start()
        {
            base.Start();

            InitializeParticle();

            if (drawNode == null)
            {
                drawNode = new DrawNode(QueueOffset);
                if (((int)queue & (int)DefferedQueue.GBuffer) != 0)
                    drawNode.gBufferEvent = DrawGBuffer;
                if (((int)queue & (int)DefferedQueue.Color) != 0)
                    drawNode.transparentEvent = DrawTransparent;
                GPUDravinSystem.Instance.InsertRender(drawNode);
            }
        }

        private void InitializeParticle()
        {
            if (useCollsion)
                particleCore = new NoiseParticle(compute, collsions, noiseData, groupCount,
                    rowCount, columnCount);
            else
                particleCore = new NoiseParticle(compute, noiseData, groupCount, rowCount, columnCount);
            ReadyMaterial();

            time = noiseData.releaseTime;
        }

        private void OnDestroy()
        {
            GPUDravinSystem.Instance.RemoveRender(drawNode);
            particleCore?.Dispose();
        }



#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (noiseData == null)
                return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.red;
            if(noiseData.shapeMode == InitialShapeMode.Cube)
            {
                Gizmos.DrawWireCube(Vector3.zero, noiseData.cubeRange);
            }
            else if(noiseData.shapeMode == InitialShapeMode.Sphere)
            {
                Gizmos.DrawWireSphere(Vector3.zero, noiseData.radius);
            }
            else
            {
                Gizmos.DrawWireSphere(Vector3.zero, 0.1f);
            }
        }


        protected override void Validate()
        {
            if (Application.isPlaying)
            {
                if (particleCore != null)
                {
                    particleCore.Dispose();
                    InitializeParticle();
                }
            }
        }
#endif

        private void ReadyMaterial()
        {
            renderMat = new Material(oriMat);

            renderMat.SetTexture("_MainTex", mainTex);
            renderMat.SetInt("_RowCount", rowCount);
            renderMat.SetInt("_ColCount", columnCount);
            renderMat.SetFloat("_TexAspectRatio", (float)mainTex.width / mainTex.height);
            if (particleFollowSpeed)
                renderMat.EnableKeyword("_FELLOW_SPEED");
            else renderMat.DisableKeyword("_FELLOW_SPEED");

        }



        public void DrawTransparent(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            //未启动，且不是因为等待关闭时造成的为未启用才退出
            renderMat.SetBuffer("_ParticleNoiseBuffer", particleCore.ParticleBuffer);

            buffer.DrawProcedural(Matrix4x4.identity, renderMat, 0, MeshTopology.Points,
                1, particleCore.ParticleBuffer.count);
            //Debug.Log("Inser");
            ExecuteBuffer(ref buffer, context);
            return;
        }


        public void DrawGBuffer(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            //未启动，且不是因为等待关闭时造成的为未启用才退出
            renderMat.SetBuffer("_ParticleNoiseBuffer", particleCore.ParticleBuffer);

            buffer.DrawProcedural(Matrix4x4.identity, renderMat, 0, MeshTopology.Points,
                1, particleCore.ParticleBuffer.count);
            ExecuteBuffer(ref buffer, context);
            return;
        }

        protected override void ReleaseParticle()
        {
            time += Time.deltaTime;
            if (time > noiseData.releaseTime * 0.9f)
            {
                particleCore.ReleaseOneCount(perReleaseGroup);
                time = 0;
            }
        }

        protected override void UpdateParticle()
        {
            particleCore.OnUpdata(transform);

        }

        protected override void FixUpdateParticle()
        {
            particleCore.OnFix(transform);
        }


    }
}