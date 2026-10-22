using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    public class ParticleProperty
    {
        private static int 
            particlesIndexsBuffer = Shader.PropertyToID("_ParticlesIndexsBuffer"),
            sortIndexOffset = Shader.PropertyToID("_SortIndexOffset"),
            cameraPos = Shader.PropertyToID("_CameraPos"),

            particlesBuffer = Shader.PropertyToID("_ParticlesBuffer"),
            releaseCount = Shader.PropertyToID("_ReleaseCount"),
            particlesCount = Shader.PropertyToID("_ParticlesCount"),
            time = Shader.PropertyToID("_Time"),
            currentIndex = Shader.PropertyToID("_CurrentIndex"),
            row = Shader.PropertyToID("_Row"),
            column = Shader.PropertyToID("_Column");

        public static int ParticlesBuffer => particlesBuffer;
        public static int ParticlesIndexsBuffer => particlesIndexsBuffer;
        public static int SortIndexOffset => sortIndexOffset;
        public static int CameraPos => cameraPos;

        public static int ReleaseCount => releaseCount;
        public static int ParticlesCount => particlesCount;
        public static int CurrentIndex => currentIndex;
        public static int Time => time;

        public static int Row => row;
        public static int Column => column;
    }

    public struct ParticleSystemData
    {
        public ComputeBuffer particlesBuffer;
        public ComputeBuffer particlesIndexsBuffer;
    }

    /// <summary>
    /// 粒子系统调用类，只进行基本的粒子调用，
    /// 其他所有操作由具体的粒子实现决定
    /// 注意：不能命名为 ParticleSystem，会与 Unity 内置组件重名导致 AddComponent/GetComponent 出现歧义
    /// </summary>
    public class GPUParticleSystem : ParticleBase,
        IDrawTransparent, IDrawShadow, IDrawGBuffer, IDrawOpaque, ISetup, ICleanUp
    {
        /// <summary>  /// 每秒释放的粒子数量  /// </summary>
        [Min(0), SerializeField]
        private float releaseSpeed;

        [SerializeField]
        private ParticleExecutionBase execution;

        int updateKernel, fixKernel, sortKernel;

        [SerializeField]
        private bool sortParticle;
        [SerializeField]
        private int sortCount = 32;

        [SerializeField, Tooltip("执行距离，超过该距离就不执行，负数为执行")]
        private float exectionDistance = 100;
        private float exectionDisSqr;
        private bool exectionAlways;
        private Transform mainCamera;

        [Tooltip("用来检测当前是否因为距离过远而处于暂停状态")]
        public bool isPausing = false;

        Bounds bounds;
        private ComputeBuffer particlesBuffer;
        private ComputeBuffer particleIndexsBuffer;
        private ParticleSystemData buffers;
        private int[] sortSize;
        private int bufferSize;
        private float releaseCount;
        private int currentIndex;   //当前选择到的编号

        public Vector3 extendSize = Vector3.one * 10;

        protected override void Start()
        {
            if (execution == null || !execution.IsRunning)
            {
                Debug.LogError("粒子执行器未正确设置");
                DestroyImmediate(gameObject);
                return;
            }
            base.Start();

            currentIndex = 0;
            releaseCount = 0;
            //创建范围，方便剔除
            bounds = new Bounds();
            bounds.center = transform.position;
            bounds.extents = extendSize * 0.5f;

            exectionDisSqr = exectionDistance * exectionDistance;
            exectionAlways = (exectionDistance < 0)? true : false;

            if (Camera.main != null)
                mainCamera = Camera.main.transform;
            else
            {
                Debug.LogError("主摄像机不存在");
                this.enabled = false;
                return;
            }


            InitializeBuffers();
            InitializeKernel();
        }



        /// <summary> /// 初始化所有的buffer  /// </summary>
        private void InitializeBuffers()
        {
            if(particlesBuffer != null)
                particlesBuffer.Release();
            if(particleIndexsBuffer != null)
                particleIndexsBuffer.Dispose();

            bufferSize = execution.EnsureBufferSize(releaseSpeed);
            
            NativeArray<ParticleData> particleData = 
                new NativeArray<ParticleData>(bufferSize, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < particleData.Length; i++)
            {
                ParticleData particle = new ParticleData();
                particle.random = new float4(UnityEngine.Random.value,
                    UnityEngine.Random.value, UnityEngine.Random.value, 0);
                particle.index = uint4.zero;
                particleData[i] = particle;
            }
            //先使用同步数据，之后尝试使用不同步数据
            particlesBuffer = new ComputeBuffer(bufferSize, 
                System.Runtime.InteropServices.Marshal.SizeOf<ParticleData>(),
                ComputeBufferType.Structured);
            particlesBuffer.SetData(particleData);
            GC.SuppressFinalize(particlesBuffer);
            particleData.Dispose();


            buffers = new ParticleSystemData();
            buffers.particlesBuffer = particlesBuffer;

            if (sortParticle)
            {
                particleIndexsBuffer = new ComputeBuffer(bufferSize, 4, ComputeBufferType.Structured);
                NativeArray<int> indexData =
                    new NativeArray<int>(bufferSize, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < bufferSize; i++)
                    indexData[i] = i;
                particleIndexsBuffer.SetData(indexData);
                indexData.Dispose();

                sortSize = new int[] { sortCount, sortCount / 2 };
                buffers.particlesIndexsBuffer = particleIndexsBuffer;
            }

        }

        private void InitializeDrawMode()
        {
            if(drawNode != null)
                drawNode.Remove();

            drawNode = new DrawNode();
            int drawMode = (int)execution.GetDrawMode();
            if ((drawMode & (int)CustomRP_DrawMode.Transparent) != 0)
                drawNode.transparentEvent = DrawTransparent;

            if ((drawMode & (int)CustomRP_DrawMode.GBuffer) != 0)
                drawNode.gBufferEvent = DrawGBuffer;

            if ((drawMode & (int)CustomRP_DrawMode.Opaque) != 0)
                drawNode.opaqueEvent = DrawOpaque;

            if ((drawMode & (int)CustomRP_DrawMode.Shadow) != 0)
                drawNode.shadowEvent = DrawShadow;

            if ((drawMode & (int)CustomRP_DrawMode.Cleanup) != 0)
                drawNode.cleanUpEvent = CleanUp;

            //排序是自动的
            drawNode.setupEvent = Setup;

            drawNode.Submit();
        }

        private void InitializeKernel()
        {
            ComputeShader compute = execution.Compute;
            updateKernel = compute.FindKernel("ParticleUpdate");
            fixKernel = compute.FindKernel("ParticleFixUpdate");
            sortKernel = compute.FindKernel("ParticleSorting");
        }

        protected override void ReleaseParticle()
        {
            //确定释放数量
            releaseCount += Time.deltaTime * releaseSpeed;
        }

        protected override void UpdateParticle()
        {
            if (!exectionAlways && (isPausing || !seeing)) return;

            execution.Compute.SetBuffer(updateKernel, 
                ParticleProperty.ParticlesBuffer, particlesBuffer);

            int release = Mathf.FloorToInt(releaseCount);
            currentIndex += release;
            currentIndex %= bufferSize;
            releaseCount -= release;

            Vector4 time = new Vector4(Time.time, Time.deltaTime, Time.fixedDeltaTime);

            execution.Compute.SetInt(ParticleProperty.CurrentIndex, currentIndex);
            execution.Compute.SetInt(ParticleProperty.ParticlesCount, bufferSize);
            execution.Compute.SetInt(ParticleProperty.ReleaseCount, release);

            execution.Compute.SetVector(ParticleProperty.Time, time);

            execution.SetUpdateProperty(transform);

            int patchSize = Mathf.CeilToInt(bufferSize / 64.0f);

            execution.Compute.Dispatch(updateKernel, patchSize, 1, 1);

            bounds.center = transform.position;     //刷新位置
        }

        protected override void FixUpdateParticle()
        {
            if (!exectionAlways &&(isPausing || !seeing)) return;
            execution.Compute.SetBuffer(fixKernel, ParticleProperty.ParticlesBuffer,
                particlesBuffer);
            Vector4 time = new Vector4(Time.time, Time.deltaTime, Time.fixedDeltaTime);
            execution.Compute.SetVector(ParticleProperty.Time, time);
            execution.Compute.SetInt(ParticleProperty.ParticlesCount, bufferSize);

            execution.SetFixUpdateProperty(transform);

            int patchSize = Mathf.CeilToInt(bufferSize / 64.0f);
            execution.Compute.Dispatch(fixKernel, patchSize, 1, 1);
        }

        private void OnEnable()
        {
            InitializeDrawMode();
        }

        private void OnDisable()
        {
            if (drawNode != null)
                drawNode.Remove();
            drawNode = null;
        }

        private void OnDestroy()
        {
            if(particlesBuffer != null)
                particlesBuffer.Dispose();
            if(particleIndexsBuffer != null)
                particleIndexsBuffer.Dispose();
            if(drawNode != null)
                drawNode.Remove();
        }

#if UNITY_EDITOR

        protected override void Validate()
        {
            bounds.center = transform.position;
            bounds.extents = extendSize * 0.5f;

            sortCount = sortCount - sortCount % 2;
            if (Application.isPlaying)
                InitializeBuffers();
        }

        private void OnDrawGizmos()
        {
            if(execution != null)
            {
                execution.DrawGizmos(transform);
            }
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(Vector3.zero, extendSize);
        }
#endif

        /// <summary>
        /// 用来存储该粒子是否处于可视区间
        /// </summary>
        [SerializeField]
        bool seeing;

        public void DrawTransparent(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            if (seeing)
            {
                execution.DrawTransparent(context, buffer, camera, buffers);
            }
        }

        public void DrawShadow(ScriptableRenderContext context, CommandBuffer buffer)
        {
            execution.DrawShadow(context, buffer, buffers);
        }

        public void DrawGBuffer(ScriptableRenderContext context, CommandBuffer buffer, 
            Camera camera)
        {
            if (seeing)
            {
                execution.DrawGBuffer(context, buffer, camera, buffers);
            }
        }

        public void DrawOpaque(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            if (seeing)
            {
                execution.DrawOpaque(context, buffer, camera, buffers);
            }
        }

        int sortOffset = 0;

        public void Setup(ScriptableRenderContext context, CommandBuffer buffer, Camera camera,
            int width, int height, bool useHDR)
        {
            if ((mainCamera.position - transform.position).sqrMagnitude > exectionDisSqr)
                isPausing = true;
            else isPausing = false;

            if (GPUDravinSystem.Instance.FrustumCulling(bounds))
                seeing = true;
            else seeing = false;

            if (sortParticle)
            {
                buffer.SetComputeIntParam(execution.Compute, ParticleProperty.ParticlesCount, bufferSize);
                buffer.SetComputeBufferParam(execution.Compute,
                    sortKernel, ParticleProperty.ParticlesBuffer, particlesBuffer);
                buffer.SetComputeBufferParam(execution.Compute,
                    sortKernel, ParticleProperty.ParticlesIndexsBuffer, particleIndexsBuffer);
                buffer.SetComputeVectorParam(execution.Compute,
                    ParticleProperty.CameraPos, camera.transform.position);
                buffer.SetComputeIntParam(execution.Compute,
                    ParticleProperty.SortIndexOffset, sortOffset);
                buffer.SetComputeIntParams(execution.Compute,
                    "_SortSize", sortSize);
                sortOffset = (sortOffset + 1) % 2;

                int halfPatchSize = Mathf.CeilToInt(bufferSize / (64f * sortSize[0]));

                buffer.DispatchCompute(execution.Compute,
                    sortKernel, halfPatchSize, 1, 1);
            }


            execution.Setup(context, buffer, camera, buffers);
        }

        public void CleanUp(ScriptableRenderContext context, CommandBuffer buffer)
        {
            execution.CleanUp(context, buffer, buffers);
        }
    }
}