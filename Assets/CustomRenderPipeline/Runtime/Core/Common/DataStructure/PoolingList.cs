using System.Collections;
using UnityEngine;
namespace Common
{
    [System.Serializable]
    /// <summary>
    /// 池化数组案例，较快的插入删除的数组
    /// </summary>
    public class PoolingList<T> : IEnumerator, IEnumerable
    {
        [SerializeField]
        private T[] list;
        private int size;

        private int index;

        public PoolingList(int capcity)
        {
            list = new T[capcity];
            this.size = 0;
        }
        public PoolingList()
        {
            list = new T[1];
            size = 0;
        }

        public T[] Arrays => list;
        public int Count => size;


        public object Current
        {
            get
            {
                return list[index];
            }
        }

        /// <summary> /// 获取数值中的第index个元素    /// </summary>
        public T GetValue(int index)
        {
            if (index >= size)
                Debug.LogError("读取超过范围" + index.ToString());
            return list[index];
        }

        public T this[int index]
        {
            get
            {
                return list[index];
            }
            set
            {
                list[index] = value;
            }
        }

        /// <summary>   /// 设置数组中的值   /// </summary>
        public void SetValue(int index, T value)
        {
            list[index] = value;
        }


        public void SetCapacity(int capacity)
        {
            if (list.Length >= capacity)
                return;
            T[] newList = new T[capacity];
            //拷贝当前数据
            for (int i = 0; i < size; i++)
                newList[i] = list[i];
            list = newList;
        }

        public bool Have(T node)
        {
            for(int i = 0; i < size; i++)
            {
                if (list[i].Equals(node))
                    return true;
            }
            return false;
        }
        public void Add(T node)
        {
            if(size == list.Length)
            {
                T[] newList = new T[(size + 1) * 2];
                for(int i=0; i<size; i++)
                    newList[i] = list[i];
                list = newList;
            }
            list[size] = node;
            size++;
        }

        public void AddRange(PoolingList<T> ranges)
        {
            //设置大小
            this.SetCapacity(Count + ranges.Count + 5);
            //加入数据
            for(int i=0; i<ranges.Count; i++)
            {
                this.Add(ranges.GetValue(i));
            }
        }

        public void RemoveAt(int removeIndex)
        {
            if (removeIndex >= size) return;
            //将最后一个替换要删除的一个，达到复杂度为1的删除
            list[removeIndex] = list[size - 1];
            size--;
        }

        public void RemoveIndexSave(int removeIndex)
        {
            if (removeIndex >= size) return;
            for(int i = removeIndex; i < size - 1; i++)
            {
                list[i] = list[i + 1];
            }
            size--;
        }

        /// <summary>        /// 移除单个节点        /// </summary>
        /// <param name="node">移除的根据点</param>
        public void Remove(T node)
        {
            int reIndex = 0;
            for (; reIndex < size; reIndex++)
            {
                if (node.Equals(list[reIndex]))
                    break;
            }
            if (reIndex >= size) return;
            list[reIndex] = list[size - 1];
            size--;
        }
        public void RemoveAll()
        {
            size = 0;
        }


        public bool MoveNext()
        {
            index++;
            return index < size;
        }

        public void Reset()
        {
            index = -1;
        }

        public IEnumerator GetEnumerator()
        {
            return this;
        }
    }
}