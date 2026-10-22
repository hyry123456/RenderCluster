using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{

    //由组进行管理的粒子
    struct GroupControlParticle
    {
        public float dieTime;    //死亡时间
        //当前模式,x:状态标记(0是未启用,1是组阶段,2是粒子阶段)
        public Vector3Int currentMode;
        //世界坐标,随机数用第一个粒子的，避免并行错误
        public Vector3 worldPos;
        //当前速度，初始化方式同上
        public Vector3 currentSpeed;
    };

    public class NoiseGroupParticle : GPUDravinBase
    {
        private bool isInsert = false;

        public ComputeShader compute;
        public Shader shader;
        public int groupCount = 10,     //粒子组总共数量
            perReleaseGroup = 1;            //每一次释放的粒子数量

        public NoiseGroupNodeData groupNodeData;

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

        /// <summary> /// 是否需要开启碰撞  /// </summary>
        public bool useCollsion;
        /// <summary> /// 是否需要碰撞后打破组 /// </summary>
        public bool isCollsionBreak;
        [SerializeField]
        public List<IGetCollsion> collsions;    //所有碰撞器数据

        [Range(0, 100)]
        public int DrawQueue = 0;

        GroupControlParticle[] groups;
        private ComputeBuffer particleBuffer;
        private ComputeBuffer groupBuffer;
        private ComputeBuffer collsionBuffer;

        private int kernel_Perframe, kernel_PerFixframe, kernel_PerFixCollsion;
        private Material material;
        float time = 0;
        int index = 0;



        private void Start()
        {
            if (compute == null || shader == null)
                return;

            drawNode = new DrawNode(DrawQueue);
            drawNode.transparentEvent = DrawTransparent;

            GPUDravinSystem.Instance.InsertRender(drawNode);
            isInsert = true;

            kernel_Perframe = compute.FindKernel("Noise_PerFrame");
            kernel_PerFixframe = compute.FindKernel("Noise_PerFixFrame");
            kernel_PerFixCollsion = compute.FindKernel("Noise_PerFixFrameWithCollsion");


            if(collsions != null)
            {
                for (int i = this.collsions.Count - 1; i >= 0; i--)
                    if (this.collsions[i] == null) this.collsions.RemoveAt(i);
            }

            if (collsions == null || collsions.Count == 0)
                useCollsion = false;
            ReadyMaterial();
            ReadyBuffer();
            time = groupNodeData.groupReleaseTime;
        }

        private void OnDestroy()
        {
            if (isInsert)
            {
                GPUDravinSystem.Instance.RemoveRender(drawNode);
                isInsert = false;
                groupBuffer?.Release();
                particleBuffer?.Release();
                collsionBuffer?.Release();
            }

        }

        private void Update()
        {
            if (!isInsert) return;  //没有插入渲染栈就退出

            time += Time.deltaTime;
            if (time > groupNodeData.groupReleaseTime)
            {
                time = 0;
                for (int i = 0; i < perReleaseGroup; i++)
                {
                    //到时间就拷贝数据
                    if (groups[index].dieTime < Time.time)
                    {
                        groups[index].dieTime = Time.time + 
                            groupNodeData.groupTime + groupNodeData.particleTime;
                        groupBuffer.SetData(groups, index, index, 1);
                        index++;
                        index %= groupCount;
                    }
                    else
                        break;
                }

            }
            SetOnCompute();
            compute.Dispatch(kernel_Perframe, groupCount, 1, 1);
        }

        private void FixedUpdate()
        {
            if (!isInsert) return;  //没有插入渲染栈就退出
            SetOnFixCompute();
            compute.Dispatch(useCollsion ? kernel_PerFixCollsion :
                kernel_PerFixframe, groupCount, 1, 1);
        }


#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (groupNodeData == null)
                return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.red;
            if (groupNodeData.groupShapeMode == InitialShapeMode.Cube)
            {
                Gizmos.DrawWireCube(Vector3.zero, groupNodeData.groupCubeRange);
            }
            else if(groupNodeData.groupShapeMode == InitialShapeMode.Sphere)
            {
                Gizmos.DrawWireSphere(Vector3.zero, groupNodeData.groupRadius);
            }
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
        }

        /// <summary>  /// 创建以及初始化Buffer数据    /// </summary>
        private void ReadyBuffer()
        {
            particleBuffer?.Release();
            groupBuffer?.Release();
            collsionBuffer?.Release();

            NoiseParticleData[] particles = new NoiseParticleData[groupCount * 64];
            particleBuffer = new ComputeBuffer(particles.Length, Marshal.SizeOf<NoiseParticleData>());
            for (int i = 0; i < particles.Length; i++)
            {
                particles[i] = new NoiseParticleData()
                {
                    random = new Vector4(Random.value, Random.value, Random.value, 0),
                    index = new Vector2Int(i, 0),
                };
            }
            particleBuffer.SetData(particles, 0, 0, particles.Length);

            groups = new GroupControlParticle[groupCount];
            groupBuffer = new ComputeBuffer(groups.Length, Marshal.SizeOf<GroupControlParticle>());
            for (int i = 0; i < groups.Length; i++)
            {
                groups[i] = new GroupControlParticle()
                {
                    dieTime = -1,
                };
            }
            groupBuffer.SetData(groups, 0, 0, groupCount);

            if (useCollsion)
            {
                CollsionStruct[] collsions = new CollsionStruct[this.collsions.Count];
                for (int i = 0; i < collsions.Length; i++)
                {
                    collsions[i] = this.collsions[i].GetCollsionStruct();
                }
                collsionBuffer = new ComputeBuffer(collsions.Length,
                    Marshal.SizeOf<CollsionStruct>());
                collsionBuffer.SetData(collsions, 0, 0, collsions.Length);
            }
        }

        /// <summary>/// 设置逐帧的Compute shader的数据/// </summary>
        private void SetOnCompute()
        {
            //设置组数据
            compute.SetInts("_GroupMode", new int[]
                {(int)groupNodeData.groupShapeMode, (int)groupNodeData.groupSpeedMode,
                groupNodeData.groupUseGravity? 1:0, isCollsionBreak? 1 : 0});
            compute.SetFloat("_GroupArc", groupNodeData.groupArc);
            compute.SetFloat("_GroupRadius", groupNodeData.groupRadius);
            compute.SetVector("_GroupCubeRange", groupNodeData.groupCubeRange);

            //设置单个粒子数据
            compute.SetFloat("_Arc", groupNodeData.particleArc);
            compute.SetFloat("_Radius", groupNodeData.particleRadius);
            compute.SetVector("_CubeRange", groupNodeData.particleCubeRange);
            compute.SetFloat("_ParticleBeginSpeed", groupNodeData.particleVelocityBegin);
            compute.SetVector("_LifeTime", new Vector4(groupNodeData.groupTime,
                groupNodeData.particleTime, 0, 0));

            compute.SetInts("_Mode", new int[] {(int)groupNodeData.particleShadpeMode,
                (int)groupNodeData.particleSpeedMode, (int)groupNodeData.sizeBySpeedMode,
                groupNodeData.particleUseGravity? 1 : 0});
            compute.SetMatrix("_RotateMatrix", transform.localToWorldMatrix);
            Vector3 speed = transform.TransformDirection(groupNodeData.groupVelocityBegin);
            compute.SetVector("_BeginSpeed", new Vector4(speed.x, 
                speed.y, speed.z, groupNodeData.groupVelocityBegin.magnitude));

            compute.SetVector("_SizeRange", new Vector4(
                groupNodeData.sizeRange.x, groupNodeData.sizeRange.y,
                groupNodeData.speedRange.x, groupNodeData.speedRange.y));
            compute.SetVector("_Time", new Vector4(Time.time, Time.deltaTime, Time.fixedDeltaTime));
            compute.SetInts("_UVCount", new int[] { rowCount, columnCount });

            compute.SetVectorArray("_Colors", groupNodeData.GetParticleColors());
            compute.SetVectorArray("_GroupColors", groupNodeData.GetGroupColors());
            compute.SetVectorArray("_Alphas", groupNodeData.GetParticleAlphas());
            compute.SetVectorArray("_GroupAlphas", groupNodeData.GetGroupAlphas());
            compute.SetVectorArray("_Sizes", groupNodeData.GetParticleSizes());
            compute.SetVectorArray("_GroupSizes", groupNodeData.GetGroupSizes());

            compute.SetBuffer(kernel_Perframe, "_ParticleNoiseBuffer", particleBuffer);
            compute.SetBuffer(kernel_Perframe, "_GroupControlBuffer", groupBuffer);
        }


        /// <summary>
        /// 设置逐固定帧的Compute shader的数据，因为compute shader是全部共用的，
        /// 所以要每次都设置一次
        /// </summary>
        private void SetOnFixCompute()
        {
            int kernel = useCollsion ? kernel_PerFixCollsion : kernel_PerFixframe; 
            compute.SetBuffer(kernel, "_ParticleNoiseBuffer", particleBuffer);
            compute.SetBuffer(kernel, "_GroupControlBuffer", groupBuffer);
            if (useCollsion)
            {
                compute.SetBuffer(kernel, "_CollsionBuffer", collsionBuffer);
                compute.SetInt("_CollsionData", collsions.Count);
                compute.SetFloat("_CollsionScale", groupNodeData.collsionScale);
            }

            compute.SetFloat("_Frequency", groupNodeData.frequency);
            compute.SetInt("_Octave", groupNodeData.octave);
            compute.SetFloat("_Intensity", groupNodeData.particleIntensity);
            compute.SetFloat("_GroupIntensity", groupNodeData.groupIntensity);
            compute.SetInts("_GroupMode", new int[]
                {(int)groupNodeData.groupShapeMode, (int)groupNodeData.groupSpeedMode,
                groupNodeData.groupUseGravity? 1:0, isCollsionBreak? 1 : 0});
            compute.SetInts("_Mode", new int[] {(int)groupNodeData.particleShadpeMode,
                (int)groupNodeData.particleSpeedMode, (int)groupNodeData.sizeBySpeedMode,
                groupNodeData.particleUseGravity? 1 : 0});
        }

        public void DrawTransparent(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            material.SetBuffer("_ParticleNoiseBuffer", particleBuffer);
            material.SetTexture("_MainTex", mainTex);
            material.SetInt("_RowCount", rowCount);
            material.SetInt("_ColCount", columnCount);
            material.SetFloat("_TexAspectRatio", (float)mainTex.width / mainTex.height);
            //material.SetFloat("_TexAspectRatio", (float)mainTex.height / mainTex.width);
            if (particleFollowSpeed)
                material.EnableKeyword("_FELLOW_SPEED");
            else material.DisableKeyword("_FELLOW_SPEED");


            buffer.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Points,
                1, particleBuffer.count);
            ExecuteBuffer(ref buffer, context);
            return;
        }

    }
}