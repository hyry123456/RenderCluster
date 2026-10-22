using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CustomRenderPipline {

    [System.Serializable]
    struct ParticleGroupsData
    {
        public float dieTime;
    };

    /// <summary> /// 简单噪声粒子的核心类，负责计算组以及粒子，但是不进行渲染 /// </summary>
    public class NoiseParticle : System.IDisposable
    {
        private int rowCount = 1;
        private int columnCount = 1;

        ParticleGroupsData[] groups;
        private ComputeBuffer particleBuffer;
        private ComputeBuffer groupBuffer;
        private ComputeBuffer collsionBuffer;

        private int kernel_Perframe, kernel_PerFixframe, kernel_PerFixCollsion;
        int index = 0;

        bool useCollsion; int collsionCount;

        int groupCount;
        ComputeShader compute;
        NoisePerNodeData noiseData;

        public ComputeBuffer ParticleBuffer => particleBuffer;

        public NoiseParticle(ComputeShader compute, List<IGetCollsion> collsions,
            NoisePerNodeData noiseData, int groupCount, int row, int column)
        {
            if (compute == null)
                return;
            kernel_Perframe = compute.FindKernel("Noise_PerFrame");
            kernel_PerFixframe = compute.FindKernel("Noise_PerFixFrame");
            kernel_PerFixCollsion = compute.FindKernel("Noise_PerFixFrameWithCollsion");

            if (collsions != null)
            {
                for (int i = collsions.Count - 1; i >= 0; i--)
                    if (collsions[i] == null) collsions.RemoveAt(i);
            }

            if (collsions == null || collsions.Count == 0)
                useCollsion = false;
            else
                useCollsion = true;
            this.compute = compute;
            this.groupCount = groupCount;
            this.noiseData = noiseData;
            this.rowCount = row; this.columnCount = column;
            ReadyBuffer(collsions);
            collsionCount = collsions.Count;
        }

        public NoiseParticle(ComputeShader compute, NoisePerNodeData noiseData, int groupCount,
            int row, int column)
        {
            if (compute == null)
                return;
            kernel_Perframe = compute.FindKernel("Noise_PerFrame");
            kernel_PerFixframe = compute.FindKernel("Noise_PerFixFrame");
            kernel_PerFixCollsion = compute.FindKernel("Noise_PerFixFrameWithCollsion");

            useCollsion = false;

            this.compute = compute;
            this.groupCount = groupCount;
            this.noiseData = noiseData;
            this.rowCount = row; this.columnCount = column;
            ReadyBuffer(null);
        }

        public void Dispose()
        {
            particleBuffer.Release();
            groupBuffer.Release();
            collsionBuffer?.Release();
        }


        /// <summary>  /// 创建以及初始化Buffer数据    /// </summary>
        private void ReadyBuffer(List<IGetCollsion> collsions)
        {
            particleBuffer?.Dispose();
            groupBuffer?.Dispose();
            collsionBuffer?.Dispose();

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

            groups = new ParticleGroupsData[groupCount];
            groupBuffer = new ComputeBuffer(groups.Length, Marshal.SizeOf<ParticleGroupsData>());
            for (int i = 0; i < groups.Length; i++)
            {
                groups[i] = new ParticleGroupsData()
                {
                    dieTime = -1,
                };
            }
            groupBuffer.SetData(groups, 0, 0, groupCount);

            if (useCollsion)
            {
                CollsionStruct[] collsionStructs = new CollsionStruct[collsions.Count];
                for (int i = 0; i < collsionStructs.Length; i++)
                {
                    collsionStructs[i] = collsions[i].GetCollsionStruct();
                }
                collsionBuffer = new ComputeBuffer(collsionStructs.Length,
                    Marshal.SizeOf<CollsionStruct>());
                collsionBuffer.SetData(collsionStructs);
            }
        }

        /// <summary>   /// 释放的方法     /// </summary>
        /// <param name="releaseGroup">释放的组数</param>
        public void ReleaseOneCount(int releaseGroup)
        {
            for (int i = 0; i < releaseGroup; i++)
            {
                //到时间就拷贝数据
                if (groups[index].dieTime < Time.time)
                {
                    groups[index].dieTime = Time.time + noiseData.releaseTime + noiseData.liveTime;
                    groupBuffer.SetData(groups, index, index, 1);
                    index++;
                    index %= groupCount;
                }
                else
                    break;
            }
        }

        public void OnFix(Transform transform)
        {
            SetOnFixCompute(transform);
            compute.Dispatch(useCollsion ? kernel_PerFixCollsion :
                kernel_PerFixframe, groupCount, 1, 1);
        }

        public void OnUpdata(Transform transform)
        {
            SetOnCompute(transform);
            compute.Dispatch(kernel_Perframe, groupCount, 1, 1);
        }

        /// <summary>
        /// 设置逐固定帧的Compute shader的数据，因为compute shader是全部共用的，
        /// 所以要每次都设置一次
        /// </summary>
        private void SetOnFixCompute(Transform transform)
        {
            if (useCollsion)
            {
                compute.SetBuffer(kernel_PerFixCollsion, "_ParticleNoiseBuffer", particleBuffer);
                compute.SetBuffer(kernel_PerFixCollsion, "_CollsionBuffer", collsionBuffer);
                compute.SetFloat("_CollsionScale", noiseData.collsionScale);
                compute.SetInt("_CollsionData", collsionCount);
            }
            else
            {
                compute.SetBuffer(kernel_PerFixframe, "_ParticleNoiseBuffer", particleBuffer);
            }

            compute.SetFloat("_Frequency", noiseData.frequency);
            compute.SetInt("_Octave", noiseData.octave);
            compute.SetFloat("_Intensity", noiseData.intensity);
            compute.SetInt("_SpeedMode", (int)noiseData.speedMode);
            compute.SetInts("_Mode", new int[] {(int)noiseData.shapeMode,
                (int)noiseData.speedBeginMode, (int)noiseData.sizeBySpeedMode,
                (int)noiseData.gravityMode});
            compute.SetVector("_Gravity", noiseData.gravitySize);
            compute.SetMatrix("_RotateMatrix", transform.localToWorldMatrix);
        }

        /// <summary>
        /// 设置逐帧的Compute shader的数据
        /// </summary>
        private void SetOnCompute(Transform transform)
        {
            compute.SetFloat("_Arc", noiseData.arc);
            compute.SetFloat("_Radius", noiseData.radius);
            compute.SetVector("_CubeRange", noiseData.cubeRange);
            compute.SetInt("_Octave", noiseData.octave);
            compute.SetFloat("_Frequency", noiseData.frequency);
            compute.SetFloat("_Intensity", noiseData.intensity);
            compute.SetVector("_LifeTime", new Vector4(noiseData.releaseTime,
                noiseData.liveTime, 0, 0));
            compute.SetInts("_Mode", new int[] {(int)noiseData.shapeMode,
                (int)noiseData.speedBeginMode, (int)noiseData.sizeBySpeedMode,
                (int)noiseData.gravityMode});
            compute.SetMatrix("_RotateMatrix", transform.localToWorldMatrix);
            compute.SetVector("_BeginSpeed", transform.TransformDirection(noiseData.velocityBegin));
            compute.SetVector("_SizeRange", new Vector4(
                noiseData.sizeRange.x, noiseData.sizeRange.y,
                noiseData.speedRange.x, noiseData.speedRange.y));
            compute.SetVector("_Time", new Vector4(Time.time, Time.deltaTime, Time.fixedDeltaTime));
            compute.SetInts("_UVCount", new int[] { rowCount, columnCount });

            compute.SetVectorArray("_Colors", noiseData.GetColors());
            compute.SetVectorArray("_Alphas", noiseData.GetAlphas());
            compute.SetVectorArray("_Sizes", noiseData.GetSizes());

            compute.SetBuffer(kernel_Perframe, "_ParticleNoiseBuffer", particleBuffer);
            compute.SetBuffer(kernel_Perframe, "_GroupBuffer", groupBuffer);
        }
    }
}