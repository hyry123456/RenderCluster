using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// BRG 二分定位测试渲染器 —— 逐行复刻官方文档 SimpleBRGExample 的模式：
/// 一个 batch、N 个实例、矩阵经 metadata 以 PackedMatrix(float3x4) 打包写入 Raw buffer。
///
/// ⚠️ 关键布局（这是之前反复画不出来的根因之一，务必与 shader 寻址一致）：
///    DOTS 寻址 stride 是固定的【每个矩阵 48 字节】（见 UnityDOTSInstancing.hlsl 的
///    LoadDOTSInstancedData_float4x4_from_float3x4，传 stride = 3*16 = 48）。
///    因此 buffer 必须是【两段式】：先连续排所有 unity_ObjectToWorld（o2w），
///    再连续排所有 unity_WorldToObject（w2o）。
///    实例 i 的 o2w 在字节 (kByteAddressObjectToWorld + i*48)，w2o 在
///    (kByteAddressWorldToObject + i*48)。绝不能把 {o2w,w2o} 按每实例 96 字节交错排，
///    否则实例 1+ 的 o2w 会被读到实例 0 的 w2o 位置。
///
/// 用法：
///   1. 用 "Unlit/BRGTest" shader 新建一个材质；
///   2. 新建空物体，挂本脚本，把内置 Cube mesh 与该材质拖进对应槽位；
///   3. 进 Play：若画面出现 3 个一排的纯色立方体（默认白/蓝）→ BRG 环境没问题；
///      若 Console 报与 ClusterPBR 相同的 "wrong cbuffer setup" 错 → 问题在管线环境层。
///   本测试完全不依赖 ClusterControl.useBRG，与 ClusterPBR 互不影响。
/// </summary>
public class BRGTestRenderer : MonoBehaviour
{
    public Mesh mesh;
    public Material material;
    public int numInstances = 3;

    private BatchRendererGroup brg;
    private BatchMeshID meshID;
    private BatchMaterialID matID;
    private BatchID batchID;
    private GraphicsBuffer instanceDataBuffer;

    // 与官方示例一致：每实例 48 字节 float3x4（列主序前 3 行），o2w + w2o。
    private struct PackedMatrix
    {
        public float c0x, c0y, c0z, c1x, c1y, c1z, c2x, c2y, c2z, c3x, c3y, c3z;
        public PackedMatrix(Matrix4x4 m)
        {
            c0x = m.m00; c0y = m.m10; c0z = m.m20;
            c1x = m.m01; c1y = m.m11; c1z = m.m21;
            c2x = m.m02; c2y = m.m12; c2z = m.m22;
            c3x = m.m03; c3y = m.m13; c3z = m.m23;
        }
    }

    private const int kSizeOfPackedMatrix = sizeof(float) * 4 * 3;   // 48
    // 首部留白 128 字节（与 BatchRenderManager 对齐，[0,96) 这段是零初始化，无人读取）。
    private const int kExtraBytes = 128;
    // o2w 起始字节：必须是 kSizeOfPackedMatrix(48) 的整数倍，这样 SetData 才能用整数元素偏移写入。
    // 96 = 2*48，元素偏移 = 2。
    private const uint kByteAddressObjectToWorld = 96;

    private unsafe void Start()
    {
        brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
        meshID = brg.RegisterMesh(mesh);
        matID = brg.RegisterMaterial(material);

        // 一排立方体的世界矩阵，间隔 2 米。
        var matrices = new Matrix4x4[numInstances];
        for (int i = 0; i < numInstances; i++)
        {
            var m = Matrix4x4.identity;
            m.SetTRS(new Vector3(i * 2f - numInstances, 0f, 0f), Quaternion.identity, Vector3.one);
            matrices[i] = m;
        }

        // 两段式布局：[0,96) 留白 | o2w[0..N-1]（每段 48）| w2o[0..N-1]（每段 48）。
        uint kByteAddressWorldToObject = kByteAddressObjectToWorld + (uint)(numInstances * kSizeOfPackedMatrix);
        int totalBytes = (int)(kByteAddressWorldToObject + numInstances * kSizeOfPackedMatrix);

        instanceDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, totalBytes / 4, sizeof(int));

        // 打包两段矩阵，分别用 SetData 写到对应元素偏移：
        //   o2w 元素偏移 = 96 / 48 = 2
        //   w2o 元素偏移 = (96 + N*48) / 48 = 2 + N
        var o2wArr = new PackedMatrix[numInstances];
        var w2oArr = new PackedMatrix[numInstances];
        for (int i = 0; i < numInstances; i++)
        {
            o2wArr[i] = new PackedMatrix(matrices[i]);
            w2oArr[i] = new PackedMatrix(matrices[i].inverse);
        }
        int o2wElem = (int)(kByteAddressObjectToWorld / kSizeOfPackedMatrix);
        int w2oElem = (int)(kByteAddressWorldToObject / kSizeOfPackedMatrix);
        instanceDataBuffer.SetData(o2wArr, 0, o2wElem, numInstances);
        instanceDataBuffer.SetData(w2oArr, 0, w2oElem, numInstances);

        // metadata：声明该 batch 需要每实例的 unity_ObjectToWorld / unity_WorldToObject。
        // 0x80000000 最高位告诉 shader：这是「每实例数组」，按实例索引寻址（stride=48）。
        var metadata = new NativeArray<MetadataValue>(2, Allocator.Temp);
        metadata[0] = new MetadataValue
        {
            NameID = Shader.PropertyToID("unity_ObjectToWorld"),
            Value = 0x80000000 | kByteAddressObjectToWorld,
        };
        metadata[1] = new MetadataValue
        {
            NameID = Shader.PropertyToID("unity_WorldToObject"),
            Value = 0x80000000 | kByteAddressWorldToObject,
        };
        batchID = brg.AddBatch(metadata, instanceDataBuffer.bufferHandle);
        metadata.Dispose();
    }

    private unsafe JobHandle OnPerformCulling(
        BatchRendererGroup rendererGroup,
        BatchCullingContext cullingContext,
        BatchCullingOutput cullingOutput,
        IntPtr userContext)
    {
        // 该 Unity 版本 UnsafeUtility.Malloc 第二参是 int，故对齐值用 int（不能用 long）。
        int alignment = UnsafeUtility.AlignOf<long>();
        var drawCommands = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();

        drawCommands->drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(
            UnsafeUtility.SizeOf<BatchDrawCommand>(), alignment, Allocator.TempJob);
        drawCommands->drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(
            UnsafeUtility.SizeOf<BatchDrawRange>(), alignment, Allocator.TempJob);
        drawCommands->visibleInstances = (int*)UnsafeUtility.Malloc(
            sizeof(int) * numInstances, alignment, Allocator.TempJob);
        drawCommands->drawCommandPickingInstanceIDs = null;
        drawCommands->instanceSortingPositions = null;
        drawCommands->instanceSortingPositionFloatCount = 0;

        drawCommands->drawCommands[0].visibleOffset = 0;
        drawCommands->drawCommands[0].visibleCount = (uint)numInstances;
        drawCommands->drawCommands[0].batchID = batchID;
        drawCommands->drawCommands[0].materialID = matID;
        drawCommands->drawCommands[0].meshID = meshID;
        drawCommands->drawCommands[0].submeshIndex = 0;
        drawCommands->drawCommands[0].splitVisibilityMask = 0xff;
        drawCommands->drawCommands[0].flags = 0;
        drawCommands->drawCommands[0].sortingPosition = 0;

        for (int i = 0; i < numInstances; i++)
            drawCommands->visibleInstances[i] = i;

        drawCommands->drawCommandCount = 1;
        drawCommands->drawRangeCount = 1;
        drawCommands->visibleInstanceCount = numInstances;
        drawCommands->drawRanges[0].drawCommandsBegin = 0;
        drawCommands->drawRanges[0].drawCommandsCount = 1;
        drawCommands->drawRanges[0].filterSettings = new BatchFilterSettings
        {
            renderingLayerMask = 0xffffffff
        };

        return default;
    }

    private void OnDisable()
    {
        if (brg != null)
        {
            brg.RemoveBatch(batchID);
            brg.UnregisterMesh(meshID);
            brg.UnregisterMaterial(matID);
            brg.Dispose();
            brg = null;
        }
        if (instanceDataBuffer != null)
        {
            instanceDataBuffer.Dispose();
            instanceDataBuffer = null;
        }
    }
}
