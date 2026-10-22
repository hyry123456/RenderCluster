using GameTask;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// UI的单个章节的显示按键，当点击该按键后会进行
    /// </summary>
    public class UITaskButton : Common.ObjectPoolingBase, IPointerClickHandler
    {
        private UITaskSettings settings;
        [SerializeField]
        private Text chapterName, partName;

        private string chapterDescription, partDescription;
        public string ChapterDescription => chapterDescription;
        public string PartDescription => partDescription;

        public override void OnInitialize()
        {
        }

        /// <summary>
        /// 点击事件，当点击后切换任务
        /// </summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            settings.ChangeChapter(this);
        }

        /// <summary>   /// 确定改按钮对应的章节     /// </summary>
        /// <param name="index"></param>
        public void SetChapterIndex(int index, UITaskSettings settings)
        {
            this.settings = settings;
            Chapter chapter = AsynTaskControl.Instance.GetExecuteChapter()[index];
            chapterDescription = chapter.ChapterDescription;
            partDescription = chapter.GetPartDescribe();

            chapterName.text = chapter.ChapterName;
            partName.text = chapter.GetPartName();
        }


    }
}