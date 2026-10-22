
using UnityEngine;


namespace Common.ResetInput
{
    /// <summary>
    /// 用来在外部用来读取以及设置input属性用的类
    /// </summary>
    public class ExternalSetInput : MonoBehaviour
    {
        public InputValueOutReadStruct[] inputValues;
        public MyInput myInput;

        private void Start()
        {
            myInput = MyInput.Instance;
        }

        private void FixedUpdate()
        {
            inputValues = new InputValueOutReadStruct[MyInput.Instance.MyInputValues.Length];
            for (int i = 0; i < MyInput.Instance.MyInputValues.Length; i++)
            {
                string keyCode = MyInput.Instance.MyInputValues[i].valueDown.ToString();
                //赋值对应轴的值
                if (keyCode[0] >= '0' && keyCode[0] <= '9')
                    inputValues[i].valueDown = ((char)MyInput.Instance.MyInputValues[i].valueDown).ToString();
                else inputValues[i].valueDown = keyCode;

                keyCode = MyInput.Instance.MyInputValues[i].valueUp.ToString();
                if (keyCode[0] >= '0' && keyCode[0] <= '9')
                    inputValues[i].valueUp = ((char)MyInput.Instance.MyInputValues[i].valueUp).ToString();
                else inputValues[i].valueUp = keyCode;

                inputValues[i].valueType = MyInput.Instance.MyInputValues[i].valueType;
                inputValues[i].changeSpeed = MyInput.Instance.MyInputValues[i].changeSpeed;
                inputValues[i].asisName = MyInput.Instance.MyInputValues[i].asisName;

                inputValues[i].value = MyInput.Instance.MyInputValues[i].value;
                inputValues[i].forValue = MyInput.Instance.MyInputValues[i].forValue;
                inputValues[i].forValueIsChange = MyInput.Instance.MyInputValues[i].forValueIsChange;
            }
        }

        private static int EnsureValue(string str)
        {
            if (str == null || str.Length == 0) return 0;
            if (str.Length == 1)
            {
                return (int)str[0];
            }
            str = str.ToUpper();
            switch (str)
            {
                case "TAB":
                    return (int)KeyCode.Tab;
                case "RIGHTSHIFT":
                    return (int)KeyCode.RightShift;
                case "LEFTSHIFT":
                    return (int)KeyCode.LeftShift;
                case "ESC":
                    return (int)KeyCode.Escape;
                case "NONE":
                    return 0;
                case "SPACE":
                    return (int)KeyCode.Space;
                default:
                    Debug.LogError("存在数据输入错误" + "  " + str);
                    return -1;
            }
        }
    }
}