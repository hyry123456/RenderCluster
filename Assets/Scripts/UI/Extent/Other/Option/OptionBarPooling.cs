using Common;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UI
{

    public class OptionBarPooling : ObjectPoolingBase, IPointerDownHandler
    {
        private Text text; int index;
        INonReturnOneParam<int> reCall;
        public void OnPointerDown(PointerEventData eventData)
        {
            reCall(index);      //点击就进行呼叫
        }


        public override void CloseObject()
        {
            base.CloseObject();
            index = 0;
            reCall = null;
        }

        public void SetText(string optionStr, int index, INonReturnOneParam<int> reCall)
        {
            text.text = optionStr;
            this.index = index;
            this.reCall = reCall;
        }

        public override void OnInitialize()
        {
            if (text == null)
                text = GetComponentInChildren<Text>();
        }
    }
}