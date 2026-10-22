using UnityEngine;
using Common;
using System.Text;
using System.Collections.Generic;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// 角色对话款，也就是显示在角色头上的对话框
    /// </summary>
    public class CharacterDialog : ObjectPoolingBase
    {
        //回调方法
        private INonReturnAndNonParam recall;

        Queue<string> readyStrings;
        /// <summary>  /// 存储所有文本用的结构   /// </summary>
        protected StringBuilder sb;
        /// <summary>  /// 当前改变中的文字颜色  /// </summary>
        Color changeColor;
        /// <summary>  /// 当前透明中的文本   /// </summary>
        protected string alphaChar;
        /// <summary> /// 待添加的字符在当前显示的文本的编号   /// </summary>
        protected int nowIndex;
        /// <summary>   /// 每一个字符显示时需要的时间   /// </summary>
        float perCharWaitTime = 0.05f;
        /// <summary>  /// 当前显示的字符   /// </summary>
        protected StringBuilder nowShowString;

        Text textUI;

        private float nowWaitTime = 0;


        public void BeginDialog(string dialogs, INonReturnAndNonParam recall)
        {
            this.recall = recall;
            string[] strs = dialogs.Split('\n');
            nowIndex = 0;
            readyStrings = new Queue<string>();
            for (int i = 0; i < strs.Length; i++)
            {
                readyStrings.Enqueue(strs[i]);
            }
            alphaChar = null;

            sb = new StringBuilder(readyStrings.Dequeue());
            nowShowString = new StringBuilder();
            nowIndex = 0;
            textUI = GetComponentInChildren<Text>();

            SustainCoroutine.Instance.AddCoroutine(CircleShowDialog, false);
        }

        private bool CircleShowDialog()
        {
            if (sb == null)     //当前显示文本不存在
            {
                //为空有两种可能，一种是还在等待加载中
                if (readyStrings != null && readyStrings.Count > 0)
                {
                    if (WaitEnd())
                    {
                        sb = new StringBuilder(readyStrings.Dequeue());
                        nowShowString = new StringBuilder("");
                        nowIndex = 0;           //初始化读取到的支付
                        nowWaitTime = 0;        //清除等待时间
                    }
                    else return false;  //继续显示该行，不进行改变
                }
                //一种是结束了，需要死亡
                else
                {
                    if (WaitEnd())
                    {
                        CloseObject();
                        return true;    //可以停止协程了
                    }
                    return false;
                }
            }

            if (alphaChar == null)
            {
                changeColor = textUI.color; changeColor.a = 0;
                if (nowIndex >= sb.Length)      //这行显示结束了，移除该行
                {
                    sb = null;
                    return false;
                }
                alphaChar = sb[nowIndex].ToString();    //显示下一个字符
                nowIndex++;
            }

            changeColor.a += Time.deltaTime * (1.0f / perCharWaitTime);
            if (changeColor.a >= 1)     //透明显示结束，将支付插入到已显示好的字符中
            {
                nowShowString.Append(alphaChar);
                alphaChar = null;
                textUI.text = nowShowString.ToString();
            }
            else
            {
                textUI.text = nowShowString + "<color=\"#" + ColorUtility.ToHtmlStringRGBA(changeColor)
                    + "\">" + alphaChar + "</color>";
            }
            return false;
        }


        private bool WaitEnd()
        {
            nowWaitTime += Time.deltaTime;
            if (nowWaitTime > nowShowString.Length * perCharWaitTime * 10)
                return true;
            return false;
        }

        public override void CloseObject()
        {
            base.CloseObject();
            if (recall != null) recall();
            textUI.text = "";       //清除现有数据
        }

        public override void OnInitialize()
        {
            recall = null;
        }
    }
}