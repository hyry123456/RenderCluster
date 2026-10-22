using Common;
using Interaction;
using UI;
using UnityEngine;

namespace GameTask
{

    public class Chapter1Part0 : ChapterPart
    {
        public override void EnterTaskEvent(Chapter chapter, bool isLoaded)
        {
            base.EnterTaskEvent(chapter, isLoaded);
            //创造一系列的可交互物品
            SustainCoroutine.Instance.AddCoroutine(ShadowDialog);
        }

        bool ShadowDialog()
        {
            BigDialog.Instance.ShowBigdialog(chapter.GetDiglogText(0), SetTriggerEvent);
            return true;
        }

        void SetTriggerEvent()
        {
            GameObject trigger = SceneObjectMap.Instance.FindControlObject("WaterTriger1");
            var eve1 = trigger.AddComponent<TriggerEvent>();
            eve1.SetCollsionEnterEvent(
                (collider, obj) =>
                {
                    if (collider.gameObject.tag == "Player")
                    {
                        BigDialog.Instance.ShowBigdialog(chapter.GetDiglogText(1), null);
                        return true;
                    }
                    return false;
                });


        }

        public override void ExitTaskEvent(Chapter chapter, bool isInterrupt)
        {
            throw new System.NotImplementedException();
        }

        public override string GetNextPartString()
        {
            throw new System.NotImplementedException();
        }

        public override string GetThisPartString()
        {
            return "0";
        }

        public override bool IsCompleteTask(Chapter chapter, InteracteInfo info)
        {
            //当完成了就成功
            return true;
        }
    }
}