using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cluster
{
    public class LevelCache
    {
        PerLevel[] levels;

        //反馈分析在后台 Task 里调用 MoveNodeToUse / AllocateNode，
        //主线程的 RequestMinsMap 同时调用 AllocateNode。
        //链表操作不加锁会数据竞争：结点可能被分配两次（两个贴图共用一页，显示错页/黑块），
        //或者从链表上丢失（该页再也无法分配，贴图永远缺页），必须统一加锁
        private readonly object listLock = new object();

        //LRU 淘汰记录：分配结点时若无空闲页，会从使用链表头部淘汰最久未使用的结点。
        //被淘汰结点占用的旧贴图页表映射需要降级/清理，而降级要写 texturesBuffer
        //（ComputeBuffer.SetData 只能在主线程调用），后台 Task 里不能直接做，
        //所以只记录 (结点, 旧贴图编号)，由主线程每帧在 RequestMinsMap 开头统一处理
        private readonly List<KeyValuePair<CacheNode, int>> pendingEvict = new List<KeyValuePair<CacheNode, int>>();

        public LevelCache(int mipsLevel, int maxCellCount, int heightScale)
        {
            levels = new PerLevel[mipsLevel];
            for(int i = 0; i < mipsLevel; i++)
            {
                levels[i] = new PerLevel(maxCellCount, i, heightScale);
                maxCellCount *= 2;      //每行乘2，最终就是Mips的4倍
            }
        }

        public void Dispose()
        {
            lock (listLock)
                pendingEvict.Clear();
            for(int i=0; i< levels.Length; i++)
            {
                levels[i].Dispose();
            }
        }

        /// <summary>
        /// 根据目标Mips等级，分配一个结点，并将该结点放入使用队列中（LRU 策略）。
        /// 优先取空闲结点；无空闲结点时，从使用链表头部淘汰最久未使用的结点——
        /// 使用链表按最近使用时间排序（头最旧、尾最新），MoveNodeToUse 每帧被
        /// Feedback 分析调用一次即为 LRU 的 touch，因此头部就是最久未被看到的页
        /// </summary>
        /// <param name="mipsLevel">目标级别</param>
        /// <returns>有时返回结点，没有时就是null</returns>
        public CacheNode AllocateNode(int mipsLevel)
        {
            lock (listLock)
            {
                CacheNode node = levels[mipsLevel].GetEmpty();
                if (node == null)
                {
                    //无空闲页：淘汰最久未使用的页（LRU 端）
                    node = levels[mipsLevel].EvictLRUHead();
                    if (node == null)
                        return null;    //该级总页数为0，理论上不可达

                    //记录旧贴图编号供主线程清理页表映射；同时立即把 node.texId 置 -1，
                    //让本帧的分配检查（texId != -1 拒绝）能通过——结点马上被重用，
                    //旧贴图编号已存在 pair 的 Value 里，不会丢失
                    int oldTexId = node.texId;
                    node.texId = -1;
                    if (oldTexId != -1)
                        pendingEvict.Add(new KeyValuePair<CacheNode, int>(node, oldTexId));
                }
                levels[mipsLevel].AddNodeInUse(node);
                return node;
            }
        }

        /// <summary>主线程调用：取出累积的 LRU 淘汰记录（锁内交换，持锁时间极短）</summary>
        public void TakePendingEvict(List<KeyValuePair<CacheNode, int>> target)
        {
            lock (listLock)
            {
                if (pendingEvict.Count == 0) return;
                target.AddRange(pendingEvict);
                pendingEvict.Clear();
            }
        }
        public void CheckEmptyNodeCount(int mipsLevel)
        {
            Debug.Log("Empty Count = " + levels[mipsLevel].GetEmptyCount()
                + " Use Count = " + levels[mipsLevel].GetUseCount());
        }

        public void Check(int mipLevel)
        {
            levels[mipLevel].CheckCount();
        }

        public void MoveNodeToUse(CacheNode node)
        {
            lock (listLock)
                levels[node.MipsLevel].AddNodeInUse(node);
        }

        /// <summary>        /// 把所有结点放入为使用，方便之后腾出空间，不移动最后一个        /// </summary>
        public void SetAllLevelInEmpty()
        {
            lock (listLock)
            {
                for(int i = 0; i < levels.Length - 1; i++)
                {
                    levels[i].ResetAllUseToEmpty();
                }
            }
        }

        /// <summary>
        /// 回收结点到空闲链表（贴图移除时调用，任何级别都回收）。
        /// 调用方需保证结点已从使用链表上摘除映射（texId = -1），且未被本帧重新分配
        /// </summary>
        public void ReleaseNode(CacheNode node)
        {
            lock (listLock)
                levels[node.MipsLevel].SetNodeInEmpty(node);
        }

        public void SetMinNodeInEmpty(CacheNode node)
        {
            ReleaseNode(node);
        }
    }
    [Serializable]
    public class CacheNode
    {
        public CacheNode(int w_Id, int h_Id, int level, int texId)
        {
            widthId = w_Id; heightId = h_Id;
            mipsLevel = level;
            next = null;
            prev = null;
            this.texId = texId;
            this.tileX = 0; this.tileY = 0;
        }
        private int widthId, heightId, mipsLevel;
        public int texId;      //外部仅可以改变这个值
        //Tile 粒度虚拟贴图：该结点当前承载的是哪一块 tile（仅 useTileVT 开启且被实际
        //分配时才有意义；空闲结点与 T=1 路径恒为 (0,0)）。用于 DrawTile 与页表索引。
        public int tileX;
        public int tileY;
        public CacheNode next;
        public CacheNode prev;

        public int WidthId => widthId;
        public int HeightId => heightId;
        public int MipsLevel => mipsLevel;
    }

    class PerLevel
    {
        /// <summary>
        /// 该级别页表的最大编号
        /// </summary>
        public int maxId;
        private CacheNode useHead;
        private CacheNode useTail;
        private CacheNode emptyHead;
        private CacheNode emptyTail;

        public PerLevel(int maxId, int level, int heightScale)
        {
            this.maxId = maxId;
            useHead = new CacheNode(-1, -1, level, -1);      useTail = useHead;
            emptyHead = new CacheNode(-1, -1, level, -1);    emptyTail = emptyHead;
            int finalHeight = maxId * heightScale;
            for(int i = 0; i < maxId; i++)
                for(int j = 0; j < finalHeight; j++)
                    AddNodeInEmpty(new CacheNode(i, j, level, -1));
        }

        public void Dispose()
        {
            ResetAllUseToEmpty();  //全部移动到空闲
            for (CacheNode tem = emptyHead; tem != null; )
            {
                tem.prev = null;
                CacheNode tem1 = tem.next;
                tem.next = null;
                tem = tem1;
            }
            emptyHead = null;
        }

        /// <summary>
        /// 添加一个空闲结点
        /// </summary>
        public void AddNodeInEmpty(CacheNode node)
        {
            emptyTail.next = node;
            node.prev = emptyTail;
            node.next = null;
            emptyTail = node;
        }

        /// <summary>
        /// 取一个空闲结点
        /// </summary>
        public CacheNode GetEmpty()
        {
            if(emptyHead.next == null)
                return null;

            CacheNode temp = emptyHead.next;
            emptyHead.next = emptyHead.next.next;

            if(emptyHead.next != null)
                emptyHead.next.prev = emptyHead;
            else    //没有下一个时就表示全部为空了，Tail就置为空
                emptyTail = emptyHead;

            temp.next = null;
            temp.prev = null;
            return temp;
        }

        /// <summary>    /// 在使用队列中加入一个元素     /// </summary>
        public void AddNodeInUse(CacheNode node)
        {
            if (node == useTail) return;    //如果结点已经是使用的最后，就退出

            if(node == emptyTail)   //尾结点单独处理
            {
                emptyTail = node.prev;
                node.prev = null;       //尾结点的next一定是null，不用设置
                emptyTail.next = null;  //往前移动后设置下一个为null，表示尾结点
            }
            else        //中间结点就正常提取
            {
                if (node.prev != null) node.prev.next = node.next;
                if (node.next != null) node.next.prev = node.prev;
            }

            useTail.next = node;

            node.prev = useTail;
            node.next = null;
            useTail = node;
        }

        public void SetNodeInEmpty(CacheNode node)
        {
            if (node == emptyTail) return;    //如果结点已经是使用的最后，就退出

            if (node == useTail)   //尾结点单独处理
            {
                useTail = node.prev;
                node.prev = null;       //尾结点的next一定是null，不用设置
                useTail.next = null;  //往前移动后设置下一个为null，表示尾结点
            }
            else        //中间结点就正常提取
            {
                if (node.prev != null) node.prev.next = node.next;
                if (node.next != null) node.next.prev = node.prev;
            }

            emptyTail.next = node;

            node.prev = emptyTail;
            node.next = null;
            emptyTail = node;
        }

        /// <summary>
        /// 淘汰使用链表头部的结点（最久未使用，即 LRU 端）。
        /// 使用链表按最近使用时间排序：头部最旧，尾部最新（AddNodeInUse/MoveNodeToUse 都加到尾部）
        /// </summary>
        public CacheNode EvictLRUHead()
        {
            CacheNode node = useHead.next;
            if (node == null)
                return null;    //没有可淘汰的结点

            useHead.next = node.next;
            if (node.next != null)
                node.next.prev = useHead;
            else                //淘汰的是尾结点，尾指针往前移
                useTail = useHead;

            node.next = null;
            node.prev = null;
            return node;
        }

        /// <summary>   /// 把使用的结点全部刷新为未使用   /// </summary>
        public void ResetAllUseToEmpty()
        {
            //int count = 0;
            //for(var node = useHead.next; node != null; node = node.next)
            //    count++;
            //if(count == 2)
            //{
            //    count = 0;
            //}
            //让空闲的后面连接使用的第一个
            emptyTail.next = useHead.next;
            if (emptyTail.next != null)
            {
                emptyTail.next.prev = emptyTail;
                emptyTail = useTail;
            }
            useTail = useHead;
            useHead.next = null;
            //CheckCount();
        }

        public int GetEmptyCount()
        {
            int count = 0;
            CacheNode tem = emptyHead.next;
            while(tem != null)
            {
                count++;
                tem = tem.next;
            }
            return count;
        }

        public int GetUseCount()
        {
            int count = 0;
            CacheNode tem = useHead.next;
            while (tem != null)
            {
                count++;
                tem = tem.next;
            }
            return count;
        }

        public void CheckCount()
        {
            int[][] arr = new int[maxId][];
            for (int i = 0; i < maxId; i++)
            {
                arr[i] = new int[maxId];
                for (int j = 0; j < maxId; j++)
                    arr[i][j] = 0;
            }
            for (CacheNode tem = emptyHead.next; tem != null; tem = tem.next)
            {
                arr[tem.WidthId][tem.HeightId] = 1;
            }
            string str = "";
            int count = 0;
            for (int i = 0; i < maxId; i++)
            {
                for (int j = 0; j < maxId; j++)
                {
                    if (arr[i][j] == 0)
                    {
                        str += $"缺少了 width = {i} height = {j} Mips = {emptyHead.MipsLevel}\n";
                        count++;
                    }
                }
            }
            if (count != 0)
                Debug.LogError(str);
        }

    }
}