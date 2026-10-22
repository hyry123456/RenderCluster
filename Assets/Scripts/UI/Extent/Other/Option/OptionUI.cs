using System.Collections.Generic;
using UnityEngine;

namespace UI
{
    /// <summary> /// 选项UI/// </summary>
    public class OptionUI : MonoBehaviour
    {
        private static OptionUI instance;
        public static OptionUI Instance
        {
            get { return instance; }
        }

        [SerializeField]
        GameObject optionBarOrigin;
        Common.PoolingList<OptionBarPooling> optionBars;
        public Common.PoolingList<OptionBarPooling> OptionBars => optionBars;
        Common.INonReturnOneParam<int> recall;

        private void Awake()
        {
            instance = this;
            optionBars = new Common.PoolingList<OptionBarPooling>();
        }

        private void OnDestroy()
        {
            //移除现有的选项，这种情况出现时选项就不重要了
            while(optionBars.Count > 0)
            {
                optionBars.GetValue(0).CloseObject();
                optionBars.RemoveAt(0);
            }
        }

        private void Start()
        {
            gameObject.SetActive(false);
        }

        void BarRecall(int index)
        {
            recall(index);
            optionBars.RemoveAll();
            gameObject.SetActive(false);
        }

        /// <summary>/// 启动选项/// </summary>
        /// <param name="options">选项内容</param>
        /// <param name="reCall">回调函数，用来说明选择了哪个</param>
        public void AddOptions(string[] options, Common.INonReturnOneParam<int> recall)
        {
            //如果当前以及有值，直接退出
            if (optionBars.Count != 0)
            {
                Debug.LogError("重复");
                return;
            }

            gameObject.SetActive(true);
            this.recall = recall;
            for (int i=0; i<options.Length; i++)
            {
                OptionBarPooling optionBar = Common.SceneObjectPool.
                    Instance.GetObject<OptionBarPooling>("OptionBar", 
                    optionBarOrigin, Vector3.zero, Quaternion.identity);
                optionBar.SetText(options[i], i, BarRecall);
                optionBars.Add(optionBar);
                optionBar.transform.parent = transform;
            }
        }

        public void RemoveNowOptions()
        {
            //移除现有的选项，这种情况出现时选项就不重要了
            while (optionBars.Count > 0)
            {
                optionBars.GetValue(0).CloseObject();
                optionBars.RemoveAt(0);
            }
        }
    }
}