

using UnityEngine;

namespace GameTask
{
    public abstract class ChapterPart
    {
        public string partName;
        public string partDescription;
        /// <summary>/// 所属章节的赋值位置，太常用了，放在这里方便赋值/// </summary>
        protected Chapter chapter;

        /// <summary>
        /// 进入一章任务的一部分，该方法是初始化方法，在任务时会调用，
        /// 分第一次进入和已经进入过在加载时进入，用来区分任务的一些内容是否加载
        /// </summary>
        /// <param name="chapter">所属章节</param>
        /// <param name="isLoaded">是否加载过，false就是第一次加载</param>
        public virtual void EnterTaskEvent(Chapter chapter, bool isLoaded)
        {
            this.chapter = chapter;
        }

        /// <summary>
        /// 是否任务完成任务，用于检测任务是否完成，一般只有在传入一个任务交互且该交互的对应章节ID
        /// 就是该任务时才会进行使用，一般不用担心是否会交互出错，也就是被其他任务调用了
        /// </summary>
        /// <param name="chapter">章节信息</param>
        /// <param name="info">交互信息</param>
        /// <returns>是否完成任务</returns>
        public abstract bool IsCompleteTask(Chapter chapter, Interaction.InteracteInfo info);

        /// <summary>
        /// 章节退出事件，当任务结束后的退出行为，当任务被打断时也会执行
        /// </summary>
        /// <param name="chapter">章节</param>
        /// <param name="isInterrupt">是否被打断，是就传入true</param>
        public abstract void ExitTaskEvent(Chapter chapter, bool isInterrupt);

        /// <summary>
        /// 一个封装好的函数，用于对于一些刚刚获取的物体，进行交互数据清空
        /// </summary>
        /// <param name="gameObject">需要进行清空的物体</param>
        public void DestoryObjAllInteracte(GameObject gameObject)
        {
            Interaction.InteractionBase[] interactionInfos = gameObject.GetComponentsInParent<Interaction.InteractionBase>();
            for(int i=0; i<interactionInfos.Length; i++)
            {
                GameObject.Destroy(interactionInfos[i]);
            }
        }

        /// <summary>/// 获得下一个任务子章节的名称，当返回null时，表示没有下一个 /// </summary>
        public abstract string GetNextPartString();

        /// <summary>/// 获得当前的子章节名 /// </summary>
        public abstract string GetThisPartString();

    }
}