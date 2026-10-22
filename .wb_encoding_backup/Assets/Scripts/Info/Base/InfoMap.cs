using System.Collections.Generic;
using UnityEngine;
using System.Text;

namespace Info
{

    public class InfoMap
    {
        private static InfoMap instance;
        public static InfoMap Instance
        {
            get
            {
                if(instance == null)
                    instance = new InfoMap();
                return instance;
            }
        }

        Dictionary<string, string> infoMap;

        private InfoMap()
        {
        }

        /// <summary>
        /// 加载角色信息表，存储格式为：属性名称 与 属性值
        /// </summary>
        /// <param name="infos">信息内容</param>
        public void LoadInfoMap(List<KeyValuePair<string, string>> infos)
        {
            infoMap?.Clear();
            infoMap = new Dictionary<string, string>();
            if (infos == null) return;
            for(int i=0; i<infos.Count; i++)
            {
                infoMap.Add(infos[i].Key, infos[i].Value);
            }
        }

        /// <summary>   /// 改变某个信息的值    /// </summary>
        /// <param name="key">信息名称</param>
        /// <param name="value">值大小</param>
        public void ChangeInfo(string key, string value)
        {
            if (infoMap.ContainsKey(key))
            {
                infoMap[key] = value;
            }
            else
            {
                Debug.LogError(key + "不存在");
            }
        }

        /// <summary>  /// 获得字符串数据，也就是在表中查找该数据  /// </summary>
        /// <param name="key">数据名称</param>
        /// <returns>该数据的字符串</returns>
        public string GetStringData(string key)
        {
            if(infoMap == null)
            {
                Debug.Log("表未加载 " + key);
                return null;
            }
            string value;
            if(infoMap.TryGetValue(key, out value))
            {
                return value;
            }
            else
            {
                Debug.LogError(key + " 不存在");
                return null;
            }
        }

        /// <summary>  /// 获得数据，且转化为浮点格式 /// </summary>
        /// <param name="key">数据名称</param>
        /// <returns>该数据的字符串</returns>
        public float GetFloatData(string key)
        {
            if (infoMap == null)
            {
                Debug.Log("表未加载 " + key);
                return 0;
            }
            string value;
            if (infoMap.TryGetValue(key, out value))
            {
                float re;
                if(float.TryParse(value, out re))
                    return re;
                else
                {
                    Debug.LogError(value + " 不可转化");
                    return 0;
                }
            }
            else
            {
                Debug.LogError(key + " 不存在");
                return 0;
            }
        }

        /// <summary>  /// 获得数据，且转化为整型格式 /// </summary>
        /// <param name="key">数据名称</param>
        /// <returns>该数据的字符串</returns>
        public int GetIntData(string key)
        {
            if (infoMap == null)
            {
                Debug.Log("表未加载 " + key);
                return 0;
            }
            string value;
            if (infoMap.TryGetValue(key, out value))
            {
                int re;
                if (int.TryParse(value, out re))
                    return re;
                else
                {
                    Debug.LogError(value + " 不可转化");
                    return 0;
                }
            }
            else
            {
                Debug.LogError(key + " 不存在");
                return 0;
            }
        }

        /// <summary>     /// 得到信息保存字符串      /// </summary>
        public string GetSaveString()
        {
            StringBuilder strs = new StringBuilder();
            foreach(var pair in infoMap)
            {
                strs.Append("<" + pair.Key + "=" + pair.Value +">\n");
            }
            return strs.ToString();
        }
    }
}