
using UnityEditor;
using UnityEngine;


namespace Common.ResetInput
{
    public class MyInputEditorControl : Editor
    {
        public static MyInput myInputValues;
        [MenuItem("MyProjectSetting/Input/Save")]
        public static void Save()
        {
            MyInput myInput = MyInput.Instance;
            GameObject go = GameObject.Find("Input");
            if(go == null)
            {
                Debug.LogError("没有设置显示对象");
                return;
            }
            ExternalSetInput externalSetInput = go.GetComponent<ExternalSetInput>();
            myInput.SetInputStruct(new MyInputValueStruct[externalSetInput.inputValues.Length]);
            for (int i=0; i<externalSetInput.inputValues.Length; i++)
            {
                myInput.MyInputValues[i].valueType = externalSetInput.inputValues[i].valueType;
                myInput.MyInputValues[i].value = externalSetInput.inputValues[i].value;
                
                int value = EnsureValue(externalSetInput.inputValues[i].valueUp);
                if (value == -1) return;
                myInput.MyInputValues[i].valueUp = (KeyCode)value;
                value = EnsureValue(externalSetInput.inputValues[i].valueDown);
                if (value == -1) return;
                myInput.MyInputValues[i].valueDown = (KeyCode)value;

                myInput.MyInputValues[i].asisName = externalSetInput.inputValues[i].asisName;
                myInput.MyInputValues[i].changeSpeed = externalSetInput.inputValues[i].changeSpeed;
            }
            myInput.ResetInputValue();
        }

        /// <summary>
        /// 根据输入的字符串判断其对应按钮
        /// </summary>
        private static int EnsureValue(string str)
        {
            if (str == null || str.Length == 0) return 0;
            str = str.ToLower();
            if(str.Length == 1)
            {
                return str[0];
            }
            if(str.Length > 5 && str.Substring(0, 5) == "alpha")
            {
                return (int)(KeyCode.Alpha0 + int.Parse(str[5].ToString()));
            }
            switch (str)
            {
                case "tab":
                    return (int)KeyCode.Tab;
                case "rightshift":
                    return (int)KeyCode.RightShift;
                case "leftshift":
                    return (int)KeyCode.LeftShift;
                case "escape":
                    return (int)KeyCode.Escape;
                case "none":
                    return 0;
                case "space":
                    return (int)KeyCode.Space;
                default:
                    Debug.LogError("存在数据输入错误" + "  " + str);
                    return -1;
            }
        }

        [MenuItem("MyProjectSetting/Input/Create")]
        public static void Create()
        {
            MyInput.Instance.LoadInputValue();
            GameObject go = GameObject.Find("Input");
            ExternalSetInput externalSetInput;
            if (go == null)
                go = new GameObject("Input");
            externalSetInput = go.GetComponent<ExternalSetInput>();
            if(externalSetInput == null)
                externalSetInput = go.AddComponent<ExternalSetInput>();
            if (externalSetInput == null) {
                Debug.LogWarning("无法加载Input");
                return;
            }
            externalSetInput.inputValues = new InputValueOutReadStruct[MyInput.Instance.MyInputValues.Length];
            for (int i=0; i<MyInput.Instance.MyInputValues.Length; i++)
            {
                string keyCode = MyInput.Instance.MyInputValues[i].valueDown.ToString();
                //赋值对应轴的值
                if (keyCode[0] >= '0' && keyCode[0] <= '9')
                    externalSetInput.inputValues[i].valueDown = ((char)MyInput.Instance.MyInputValues[i].valueDown).ToString();
                else externalSetInput.inputValues[i].valueDown = keyCode;

                keyCode = MyInput.Instance.MyInputValues[i].valueUp.ToString();
                if(keyCode[0] >= '0' && keyCode[0] <= '9')
                    externalSetInput.inputValues[i].valueUp = ((char)MyInput.Instance.MyInputValues[i].valueUp).ToString();
                else externalSetInput.inputValues[i].valueUp = keyCode;

                externalSetInput.inputValues[i].valueType = MyInput.Instance.MyInputValues[i].valueType;
                externalSetInput.inputValues[i].changeSpeed = MyInput.Instance.MyInputValues[i].changeSpeed;
                externalSetInput.inputValues[i].asisName = MyInput.Instance.MyInputValues[i].asisName;
            }
        }

        [MenuItem("MyProjectSetting/Input/Destory")]
        public static void Destory()
        {
            MyInput.DestoryNow();
        }
    }
}