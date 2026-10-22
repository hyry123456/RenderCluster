using System.Collections.Generic;
using UnityEngine;

namespace Common
{
    [CreateAssetMenu(menuName = "Custom/Common/FileLoadAsset")]
    /// <summary> /// 文件读取时需要的映射文件 /// </summary>
    public class FileLoadAsset : ScriptableObject
    {
        [SerializeField]
        /// <summary>/// 所有的文件名称 /// </summary>
        private List<string> names;
        [SerializeField]
        /// <summary>/// 所有的文件路径 /// </summary>
        private List<string> paths;

        private Dictionary<string, string> fileMaps;

        public string FindPath(string names)
        {
            if(fileMaps == null)
            {
                CreateDictionary();
            }
            string path;
            if(fileMaps.TryGetValue(names, out path))
            {
                return path;
            }
            else
            {
                Debug.Log(names + " is null");
                return null;
            }
        }

        private void CreateDictionary()
        {
            fileMaps?.Clear();
            fileMaps = new Dictionary<string, string>();
            for(int i=0; i<names.Count; i++)
            {
                fileMaps.Add(names[i], paths[i]);
            }
        }

    }
}