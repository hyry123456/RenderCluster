using System;
using System.Collections;

namespace Common
{
    //节点
    class LinkNode<T> where T : IComparable
    {
        public LinkNode(T value)
        {
            next = null;
            this.value = value;
        }
        public LinkNode()
        {
            next = null;
            value = default(T);
        }
        public T value;
        public LinkNode<T> next;
    }

    public enum SortType
    {
        /// <summary>
        /// 大的在前面
        /// </summary>
        Greater,
        /// <summary>
        /// 小的在前面
        /// </summary>
        Less,
    }


    /// <summary> 
    /// 先序链表，对插入的数据进行自动排序,这个只是最基础的链表，
    /// 插入和删除的复杂度都是O(n)
    /// </summary>
    public class PreorderLinkList<T> : IEnumerator, IEnumerable where T : IComparable 
    {
        readonly LinkNode<T> head = new LinkNode<T>();
        LinkNode<T> current;
        SortType sortType;

        int size;

        public int Length => size;

        /// <summary>    /// 先序链表，默认是大顶队    /// </summary>
        /// <param name="sortType">排序类型，可以设置为小顶队</param>
        public PreorderLinkList(SortType sortType = SortType.Greater)
        {
            this.sortType = sortType;
            current = head;
            size = 0;
        }

        public object Current
        {
            get
            {
                return current.value;
            }
        }
        public T this[int index]
        {
            get
            {
                LinkNode<T> cur = head;
                for(int i = 0; i <= index; i++)
                {
                    cur = cur.next;
                    if(cur == null)
                        throw new IndexOutOfRangeException();
                }
                return cur.value;
            }
            set
            {
                LinkNode<T> cur = head;
                //走到前一个
                for (int i = 0; i < index; i++)
                {
                    cur = cur.next;
                }
                T temp = cur.next.value;
                cur.next = cur.next.next;
                Add(temp);
            }
        }


        public void Add(T value)
        {
            LinkNode<T> cur = head;
            //遍历到对应位置
            while (cur.next != null)
            {
                if (sortType == SortType.Greater)
                {
                    if (value.CompareTo(cur.next.value) <= 0)
                        cur = cur.next;
                    else
                        break;
                }
                else
                {
                    if (value.CompareTo(cur.next.value) >= 0)
                        cur = cur.next;
                    else
                        break;
                }
            }
            //插入
            LinkNode<T> temp = cur.next;
            cur.next = new LinkNode<T>(value);
            cur.next.next = temp;
            size++;
        }
        public bool Remove(T value)
        {
            LinkNode<T> cur = head;
            while (cur.next != null)
            {
                //找到就移除
                if (cur.next.value.Equals(value))
                {
                    cur.next = cur.next.next;
                    size--;
                    return true;
                }
                cur = cur.next;
            }
            return false;
        }

        /// <summary>    /// 检查链表中是否包含该值     /// </summary>
        public bool Contains(T value)
        {
            LinkNode<T> temp = head.next;
            while(temp != null)
            {
                if (temp.value.Equals(value))
                    return true;
                temp = temp.next;
            }
            return false;
        }

        public IEnumerator GetEnumerator()
        {
            //这个函数要主动调用。。。
            Reset();
            return this;
        }

        public bool MoveNext()
        {
            current = current.next;

            return current != null;
        }

        public void Reset()
        {
            //从第一个的前一个开始
            current = head;
        }


    }
}