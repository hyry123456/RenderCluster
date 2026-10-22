using System.Collections.Generic;
using UnityEngine;

namespace Common
{
    /// <summary> 
    /// 文件加载表，用来根据在已经定义好的文本表中进行文本数据的读取，
    /// 且提供了一系列的内容切割方式进行数据切割，其中的数据都是实时读取的，
    /// 因此建议在多线程中调用该类
    /// </summary>
    public class FileLoadMap
    {
        private static FileLoadMap instance;
        public static FileLoadMap Instance(FileLoadAsset loadAsset)
        {
            if (instance == null)
                instance = new FileLoadMap(loadAsset);
            return instance;
        }

        private FileLoadAsset asset;
        
        private FileLoadMap(FileLoadAsset loadAsset)
        {
            asset = loadAsset;
        }

        /// <summary>
        /// 根据文件名称自动读取文本文件夹中的文件数据，
        /// 将每一个用‘<>’区分的数据作为一个Pair，其中
        /// ‘=’前的是name，后面的是value
        /// </summary>
        /// <param name="fileName">文件名称</param>
        /// <returns>所有的pair</returns>
        public List<KeyValuePair<string, string>> LoadFileToPair(string fileName)
        {
            string path = asset.FindPath(fileName);
            List<string> allPairs = 
                FileReadAndWrite.ReadFileByAngleBrackets(
                    Application.streamingAssetsPath + "/" + path);
            if(allPairs == null) return null;
            List<KeyValuePair<string, string>> pairs = 
                new List<KeyValuePair<string, string>>(allPairs.Count);
            for(int i=0; i<allPairs.Count; i++)
            {
                string[] strs = allPairs[i].Split('=');
                pairs.Add(new KeyValuePair<string, string>(strs[0], strs[1]));
            }
            return pairs;
        }

        /// <summary>
        /// 读取文本数据，这些数据每一个都用<>括住，只会读括住的数据
        /// </summary>
        /// <param name="fileName">文件名称</param>
        public List<string> LoadFile(string fileName)
        {
            if(fileName == null)
            {
                Debug.Log("NULL");
            }
            string path = asset.FindPath(fileName);
            return
                FileReadAndWrite.ReadFileByAngleBrackets(
                    Application.streamingAssetsPath + "/" + path);
        }
    }
}