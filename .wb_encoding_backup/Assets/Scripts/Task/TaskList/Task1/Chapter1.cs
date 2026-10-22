using UnityEngine;

namespace GameTask
{

    public class Chapter1 : AsynChapterBase
    {
        public Chapter1()
        {
            chapterName = "第一章";
            chapterID = 1;
            targetPart = targetPart + "Chapter1Part";
            chapterSavePath = Application.streamingAssetsPath + "/Task/Chapter/1.task";
            runtimeScene = "The_Viking_Village";
        }

        public override void CheckAndLoadChapter()
        {
            Debug.Log("One");
            AsynTaskControl.Instance.AddChapter(this);
        }

        public override void CompleteChapter(bool isInThisScene)
        {
            Debug.Log("章节完成");
        }

        public override void ExitChapter()
        {
        }
    }
}