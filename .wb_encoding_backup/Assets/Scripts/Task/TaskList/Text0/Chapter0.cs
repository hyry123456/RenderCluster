using UnityEngine;


namespace GameTask
{
    public class Chapter0 : AsynChapterBase
    {
        public Chapter0()
        {
            chapterName = "测试章节";
            chapterID = 0;
            targetPart = targetPart + "Chapter0Part";
            chapterSavePath = Application.streamingAssetsPath + "/Task/Chapter/0.task";
            runtimeScene = "SimpleText";
        }

        //直接加载
        public override void CheckAndLoadChapter()
        {
            //初始章节，直接加载
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