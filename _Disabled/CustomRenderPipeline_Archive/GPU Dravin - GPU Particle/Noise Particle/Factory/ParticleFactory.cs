using UnityEngine;
using Unity.Collections;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    /// <summary>
    /// 粒子工厂的粒子组，用来定义一组粒子的数据
    /// </summary>
    public struct FactoryNode
    {
        public Matrix4x4 projectMatrix;    //旋转矩阵
        //xyz：初始速度，w:其他模式添加的力的强度
        public Vector4 beginSpeed;  
        public float arc;    //角度
        //x:大小(半径以及边长，形状靠矩阵),y:频率，z：强度，w：采样次数
        public Vector4 size_Noises;
        //大小范围，x-y:大小范围，z-w:速度映射范围
        public Vector4 sizeRange;
        //生命周期：x:结束时间，y:持续时间,z:释放时间
        public Vector4 lifeTime;
        //初始化模式：x:形状, y:速度，z：是否使用重力
        public Vector3Int initlizeMode;
        //渲染设置，x:贴图编号，y:Gradient编号, z:大小曲线编号
        public Vector3Int drawSetting;
        //附加设置，x:是否跟随速度
        public Vector3Int showSetting;
    };

    /// <summary> /// 用来确定单个Gradient的数据用的结构体  /// </summary>
    public struct GradientData
    {
        public int colorBegin;      //颜色起始位置
        public int colorEnd;        //颜色结束位置
        public int alphaBegin;      //透明度起始
        public int alphaEnd;        //透明度终点
    }

    /// <summary> /// 用来确定单个曲线的结构体 /// </summary>
    public struct CurveData
    {
        public int curveBegin;
        public int curveEnd;
    }


    /// <summary> /// 粒子工厂改进版，提供更合理的粒子创建模式 /// </summary>
    public class ParticleFactory : GPUDravinBase
    {
#if UNITY_EDITOR
        public bool isDebug;
        public int collsionCount;
        public CollsionStruct[] collsionStructs;
#endif

        static ParticleFactory instance;
        public static ParticleFactory Instance
        {
            get
            {
                if(instance == null)
                {
                    GameObject gameObject = new GameObject("ParticleFactory");
                    gameObject.AddComponent<ParticleFactory>();
                }
                return instance;
            }
        }

        private const int PerNodeCount = 32;

        [SerializeField]
        NativeArray<FactoryNode> nodes;
        [SerializeField]
        NativeArray<CollsionStruct> collsions;
        ComputeBuffer nodesBuffer, collsionsBuffer, particlesBuffer;
        ComputeBuffer gradientsBuffer, colorsBuffer, alphasBuffer,
            curvesBuffer, keyframesBuffer, textureDataBuffer;

        [SerializeField]
        Queue<int> emptyNodes;      //空闲的所有节点
        [SerializeField]
        SortedList<float, List<int>> usingNodes; //使用中的节点，使用优先队列优化使用

        private DnaCollsionList collsionList;

        Material material;
        ComputeShader compute;
        [SerializeField]
        Texture2DArray texture2DArray;

        int kernel_PerFrame, kernel_PerFixFrame;
        FactoryCommonData commonData;



        private void Awake()
        {
            if(instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;

            drawNode = new DrawNode();
            drawNode.transparentEvent = DrawTransparent;

            GPUDravinSystem.Instance.InsertRender(drawNode);
            collsionList = new DnaCollsionList();
            DontDestroyOnLoad(gameObject);

            commonData = Resources.Load<FactoryCommonData>("Render/ParticleFactory/FactoryCommonData");
            Initilize();
            ReadyBuffer();
        }

        private void Update()
        {
            while(usingNodes.Count > 0)
            {
                List<int> indexs = usingNodes.Values[0];
                //有空闲的就扔出渲染栈
                if (nodes[indexs[0]].lifeTime.x < Time.time)
                {
                    usingNodes.RemoveAt(0);
                    for (int i = 0; i < indexs.Count; i++)
                        emptyNodes.Enqueue(indexs[i]);
                }
                else
                    break;
            }
            SetOnUpdate();
            compute.Dispatch(kernel_PerFrame, particlesBuffer.count / 1024 + 1, 1, 1);


        }

        private void FixedUpdate()
        {
            SetOnFixUpdate();
            compute.Dispatch(kernel_PerFixFrame, particlesBuffer.count / 1024 + 1, 1, 1);

#if UNITY_EDITOR
            if (commonData.isRecaculate)
            {
                Initilize();
                commonData.isRecaculate = false;
            }
#endif
        }

        private void OnDestroy()
        {
            nodes.Dispose();
            collsions.Dispose();
            nodesBuffer.Dispose();
            collsionsBuffer.Dispose();
            particlesBuffer.Dispose();

            gradientsBuffer.Dispose();
            colorsBuffer.Dispose();
            alphasBuffer.Dispose();
            curvesBuffer.Dispose();
            keyframesBuffer.Dispose();
            textureDataBuffer.Dispose();

            GPUDravinSystem.Instance.RemoveRender(drawNode);
            collsionList.DisposeList();
        }

        /// <summary> /// 初始化数据  /// </summary>
        private void Initilize()
        {
            texture2DArray = TextureArrayCreate.CreateTextureArrayDirect(commonData.TextureArray);
            Gradient[] gradients = commonData.Gradients;
            List<GradientData> gradientDatas = new List<GradientData>();
            List<Vector4> colorKeys = new List<Vector4>();
            List<Vector2> alphaKeys = new List<Vector2>();
            for (int i = 0; i < gradients.Length; i++)
            {
                GradientData gradientData = new GradientData();

                GradientColorKey[] gradientColorKeys = gradients[i].colorKeys;
                gradientData.colorBegin = colorKeys.Count;
                for(int j = 0; j < gradientColorKeys.Length; j++)
                {
                    Vector4 tem = gradientColorKeys[j].color;
                    tem.w = gradientColorKeys[j].time;
                    colorKeys.Add(tem);
                }
                gradientData.colorEnd = colorKeys.Count;

                GradientAlphaKey[] gradientAlphaKeys = gradients[i].alphaKeys;
                gradientData.alphaBegin = alphaKeys.Count;
                for(int j = 0; j < gradientAlphaKeys.Length; j++)
                    alphaKeys.Add(new Vector2(gradientAlphaKeys[j].alpha, gradientAlphaKeys[j].time));
                gradientData.alphaEnd = alphaKeys.Count;

                gradientDatas.Add(gradientData);
            }
            gradientsBuffer?.Release();
            gradientsBuffer = new ComputeBuffer(gradientDatas.Count,
                Marshal.SizeOf<GradientData>());
            gradientsBuffer.SetData(gradientDatas);
            colorsBuffer?.Release();
            colorsBuffer = new ComputeBuffer(colorKeys.Count, Marshal.SizeOf<Vector4>());
            colorsBuffer.SetData(colorKeys);
            alphasBuffer?.Release();
            alphasBuffer = new ComputeBuffer(alphaKeys.Count, Marshal.SizeOf<Vector2>());
            alphasBuffer.SetData(alphaKeys);

            List<CurveData> curveDatas = new List<CurveData>();
            List<Vector4> curveKeys = new List<Vector4>();
            AnimationCurve[] curves = commonData.Curves;
            for(int i = 0; i < curves.Length; i++)
            {
                CurveData curveData = new CurveData();
                curveData.curveBegin = curveKeys.Count;
                Keyframe[] keyframes = curves[i].keys;
                for(int j = 0; j < keyframes.Length; j++)
                {
                    curveKeys.Add(new Vector4(keyframes[j].time,
                        keyframes[j].value, keyframes[j].inTangent,
                        keyframes[j].outTangent));
                }
                curveData.curveEnd = curveKeys.Count;
                curveDatas.Add(curveData);
            }

            List<Vector2Int> textureDatas = new List<Vector2Int>();
            for(int i = 0; i < commonData.Textures.Length; i++)
            {
                var temp = commonData.Textures[i];
                textureDatas.Add( new Vector2Int(temp.rowCount, temp.colCount));
            }
            textureDataBuffer = new ComputeBuffer(textureDatas.Count,
                Marshal.SizeOf<Vector2Int>());
            textureDataBuffer.SetData(textureDatas);

            curvesBuffer = new ComputeBuffer(curveDatas.Count, Marshal.SizeOf<CurveData>());
            curvesBuffer.SetData(curveDatas);
            keyframesBuffer = new ComputeBuffer(curveKeys.Count, Marshal.SizeOf<Vector4>());
            keyframesBuffer.SetData(curveKeys);

            kernel_PerFrame = commonData.computeShader.FindKernel("Particles_PerFrame");
            kernel_PerFixFrame = commonData.computeShader.FindKernel("Particles_PerFixFrame");
            material = new Material(commonData.shader);

            material.EnableKeyword("_SOFT_PARTICLE");
            material.SetFloat("_SoftParticlesDistance", 1);
            material.SetFloat("_SoftParticlesRange", 1);

            material.EnableKeyword("_NEAR_ALPHA");
            material.SetFloat("_NearFadeDistance", 1);
            material.SetFloat("_NearFadeRange", 1);

            compute = commonData.computeShader;
        }

        private void ReadyBuffer()
        {
            //全部初始化为默认值,使用该数组让内存拷贝时加速
            nodes = new NativeArray<FactoryNode>(30, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < nodes.Length; i++) nodes[i] = new FactoryNode();
            collsions = new NativeArray<CollsionStruct>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for(int i = 0; i < collsions.Length; i++) collsions[i] = IGetCollsion.NoneCollsion;
            //一组PerNodeCount个粒子
            NativeArray<NoiseParticleData> particles = new NativeArray<NoiseParticleData>(
                nodes.Length * PerNodeCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < particles.Length; i++)
            {
                particles[i] = new NoiseParticleData()
                {
                    random = new Vector4(Random.value, Random.value, Random.value, 0),
                    index = new Vector2Int(i, 0),
                };
            }

            nodesBuffer = new ComputeBuffer(nodes.Length, Marshal.SizeOf<FactoryNode>());
            nodesBuffer.SetData(nodes);
            collsionsBuffer = new ComputeBuffer(collsions.Length, Marshal.SizeOf<CollsionStruct>());
            collsionsBuffer.SetData(collsions);
            particlesBuffer = new ComputeBuffer(particles.Length, Marshal.SizeOf<NoiseParticleData>());
            particlesBuffer.SetData(particles);
            particles.Dispose();

            //设置空闲位
            emptyNodes = new Queue<int>(nodes.Length);
            for(int i = 0; i < nodes.Length; i++)
                emptyNodes.Enqueue(i);
            usingNodes = new SortedList<float, List<int>>();
        }

        private void SetOnUpdate()
        {
            compute.SetVector("_Time", new Vector4(Time.time, Time.deltaTime, Time.fixedDeltaTime));
            compute.SetBuffer(kernel_PerFrame, "nodesBuffer", nodesBuffer);
            compute.SetBuffer(kernel_PerFrame, "particlesBuffer", particlesBuffer);
            compute.SetBuffer(kernel_PerFrame, "GradientDataBuffer", gradientsBuffer);
            compute.SetBuffer(kernel_PerFrame, "colorsBuffer", colorsBuffer);
            compute.SetBuffer(kernel_PerFrame, "alphasBuffer", alphasBuffer);
            compute.SetBuffer(kernel_PerFrame, "curvesBuffer", curvesBuffer);
            compute.SetBuffer(kernel_PerFrame, "keyframesBuffer", keyframesBuffer);
            compute.SetBuffer(kernel_PerFrame, "textureDatasBuffer", textureDataBuffer);
        }

        private void SetOnFixUpdate()
        {
            compute.SetVector("_Time", new Vector4(Time.time, Time.deltaTime, Time.fixedDeltaTime));
            compute.SetBuffer(kernel_PerFixFrame, "nodesBuffer", nodesBuffer);
            compute.SetBuffer(kernel_PerFixFrame, "particlesBuffer", particlesBuffer);
            compute.SetBuffer(kernel_PerFixFrame, "_CollsionBuffer", collsionList.CollsionsBuffer);
            compute.SetInt("_CollsionData", collsionList.CollsionCount);

#if UNITY_EDITOR
            if (isDebug)
            {
                collsionCount = collsionList.CollsionCount;
                collsionStructs = new CollsionStruct[collsionList.CollsionsBuffer.count];
                collsionList.CollsionsBuffer.GetData(collsionStructs);
            }
#endif
        }

        /// <summary> /// 对Nodes节点数据进行翻倍，且复制写入  /// </summary>
        private void ReCaculateNodes()
            {
                NativeArray<FactoryNode> tempNodes = new NativeArray<FactoryNode>(
                    nodes.Length * 2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for(int i = 0; i < nodes.Length; i++)
                    tempNodes[i] = nodes[i];   //传递数据
                for (int i = nodes.Length; i < tempNodes.Length; i++)
                {
                    tempNodes[i] = new FactoryNode();
                    emptyNodes.Enqueue(i);
                }
                ComputeBuffer tempNodesBuffer = new ComputeBuffer(tempNodes.Length,
                    Marshal.SizeOf<FactoryNode>());
                tempNodesBuffer.SetData(tempNodes);
                nodesBuffer.Release();
                nodesBuffer = tempNodesBuffer;
                nodes.Dispose();
                nodes = tempNodes;
            }

        /// <summary>/// 对所有粒子进行拷贝 /// </summary>
        private void ReCaculateParticles()
        {
            ComputeBuffer tempParticlesBuffer = new ComputeBuffer(
                nodes.Length * PerNodeCount, Marshal.SizeOf<NoiseParticleData>());
            NoiseParticleData[] particles = new NoiseParticleData[nodes.Length * PerNodeCount];
            particlesBuffer.GetData(particles, 0, 0, particlesBuffer.count);
            for(int i = particlesBuffer.count; i < tempParticlesBuffer.count; i++)
            {
                particles[i] = new NoiseParticleData
                {
                    random = new Vector4(Random.value, Random.value, Random.value, 0),
                    index = new Vector2Int(i, 0),
                };
            }
            tempParticlesBuffer.SetData(particles);
            particlesBuffer.Dispose();
            particlesBuffer = tempParticlesBuffer;
        }

        public void DrawTransparent(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            material.SetBuffer("nodesBuffer", nodesBuffer);
            material.SetBuffer("particlesBuffer", particlesBuffer);
            material.SetBuffer("textureDatasBuffer", textureDataBuffer);
            material.SetTexture("_Textures", texture2DArray);
            buffer.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Points,
                1, particlesBuffer.count);
            return;
        }


        public void ReleaseParticle(FactoryEmitterData emitterData,
            Matrix4x4 matrix4, Vector4 beginSpeed)
        {
            List<int> indexs = new List<int>(emitterData.particleCount);
            float beginTime = Time.time + emitterData.sustainTime + emitterData.releaseTime;
            FactoryNode node = new FactoryNode
            {
                projectMatrix = matrix4,
                beginSpeed = beginSpeed,
                arc = emitterData.arc,
                size_Noises = new Vector4(emitterData.size,
                    emitterData.frequency, emitterData.intensity,
                    emitterData.octave),
                sizeRange = new Vector4(
                    emitterData.sizeRange.x, emitterData.sizeRange.y,
                    emitterData.speedRange.x, emitterData.speedRange.y),
                lifeTime = new Vector4(beginTime,
                    emitterData.sustainTime, emitterData.releaseTime),
                initlizeMode = new Vector3Int((int)emitterData.particleShadpeMode,
                    (int)emitterData.particleSpeedMode, emitterData.useGravity ? 1 : 0),
                drawSetting = new Vector3Int(emitterData.textureIndex,
                    emitterData.gradientIndex, emitterData.curveIndex),
                showSetting = new Vector3Int(emitterData.fellowSpeed ? 1 : 0, 0, 0),
            };
            for (int i = 0; i < emitterData.particleCount; i++)
            {
                //拓展空间
                if (emptyNodes.Count == 0)
                {
                    Debug.Log("Recaculate");
                    ReCaculateNodes();
                    ReCaculateParticles();
                }
                int index = emptyNodes.Dequeue();

                nodes[index] = node;
                indexs.Add(index);
                nodesBuffer.SetData(nodes, index, index, 1);
            }
            if (usingNodes.ContainsKey(beginTime))
            {
                List<int> pre = usingNodes[beginTime];
                pre.AddRange(indexs);
                usingNodes[beginTime] = pre;
            }
            else
                usingNodes[beginTime] = indexs;
        }

        public CollsionNode CreateCollsionNode(CollsionStruct collsion)
        {
            return collsionList.GetCollsionNode(collsion);
        }

    }
}