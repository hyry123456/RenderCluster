using UnityEngine;
using System.Runtime.InteropServices;
using Unity.Collections;

namespace CustomRenderPipline
{
    public class CollsionNode
    {
        private int index;
        private DnaCollsionList control;

        public CollsionNode(int index, DnaCollsionList control)
        {
            this.index = index;
            this.control = control;
        }

        public int Index
        {
            get { return index; }
            set { index = value; }
        }
        public void UpdateCollsionStruct(CollsionStruct collsion)
        {
            if(index < 0)
            {
                Debug.LogError("该碰撞器并未入栈，却进行读取");
                return;
            }
            if (index > control.CollsionCount)
            {
                Debug.LogError("编号有问题");
                return;
            }
            else
                control.UpdataNode(index, collsion);
        }

        /// <summary>  /// 移除当前节点  /// </summary>
        public void RemoveCollsion()
        {
            control.RecycleNode(index);
        }

        public CollsionStruct CollsionStruct()
        {
            return control.GetCollsionStruct(index);
        }
    }
    /// <summary>
    /// 动态粒子碰撞器列表，用来进行碰撞Buffer生成以及动态更新的跟踪
    /// </summary>
    public class DnaCollsionList
    {
        CollsionNode[] collsions;
        NativeArray<CollsionStruct> collsionStructs;
        ComputeBuffer collsionsBuffer;
        int currentCount;

        public DnaCollsionList()
        {
            collsions = new CollsionNode[5];        //初始化碰撞体数量
            //初始化内存空间
            collsionStructs = new NativeArray<CollsionStruct>(
                collsions.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            collsionsBuffer = new ComputeBuffer(collsions.Length,
                Marshal.SizeOf<CollsionStruct>());  //初始化Buffer，但是一开始不需要更新值，没有意义
            currentCount = 0;
        }

        /// <summary>   /// 所有碰撞体的Buffer    /// </summary>
        public ComputeBuffer CollsionsBuffer => collsionsBuffer;

        /// <summary>  /// 当前碰撞体的数量，在这个数量之前的都是有值的   /// </summary>
        public int CollsionCount => currentCount;

        /// <summary>  /// 创造一个碰撞器，对该碰撞器的更新会自动影响Buffer的值  /// </summary>
        /// <returns>碰撞器控制对象</returns>
        public CollsionNode GetCollsionNode(CollsionStruct collsion)
        {
            //检测目前的数组是否够大
            if(currentCount == collsions.Length)
                UpdateCollsions();
            collsions[currentCount] = new CollsionNode(currentCount, this);
            collsionStructs[currentCount] = collsion;
            //更新单个位置
            collsionsBuffer.SetData(collsionStructs, currentCount, currentCount, 1);
            return collsions[currentCount++];       //返回后递增
        }

        /// <summary> /// 回收一个碰撞节点    /// </summary>
        public void RecycleNode(int index)
        {
            if (!collsionStructs.IsCreated)
                return;
            CollsionNode beRemoved = collsions[index];  //被移除的节点
            CollsionNode replace = collsions[currentCount - 1];     //用来替换的节点
            beRemoved.Index = -1;   //标记为无用的
            collsions[index] = replace; //替换
            replace.Index = index;      //标记位置
            collsionStructs[index] = collsionStructs[currentCount - 1];
            collsionsBuffer.SetData(collsionStructs, index, index, 1);
            currentCount--;
        }

        /// <summary>   /// 更新当个碰撞器数据   /// </summary>
        public void UpdataNode(int index, CollsionStruct collsionStruct)
        {
            this.collsionStructs[index] = collsionStruct;
            collsionsBuffer.SetData(collsionStructs, index, index, 1);
        }

        /// <summary>    /// 清空数据     /// </summary>
        public void DisposeList()
        {
            collsionStructs.Dispose();
            collsionsBuffer.Dispose();
            collsionsBuffer = null;
        }

        public CollsionStruct GetCollsionStruct(int index)
        {
            return collsionStructs[index];
        }

        /// <summary>   /// 更新碰撞器大小   /// </summary>
        private void UpdateCollsions()
        {
            CollsionNode[] temp = new CollsionNode[collsions.Length * 2];
            NativeArray<CollsionStruct> tempStruct = new NativeArray<CollsionStruct>(
                temp.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            //保留之前的数据
            for (int i = 0; i < collsions.Length; i++)
            {
                temp[i] = collsions[i];
                tempStruct[i] = collsionStructs[i];
            }
            //释放之前的
            collsionsBuffer.Dispose();
            collsionStructs.Dispose();
            //传递给GPU
            collsionsBuffer = new ComputeBuffer(temp.Length, Marshal.SizeOf<CollsionStruct>());
            collsionsBuffer.SetData(tempStruct, 0, 0, collsions.Length);
            collsions = temp;       //更换根据数组
            collsionStructs = tempStruct;
        }
    }
}