using UnityEngine;

namespace Common
{
    /// <summary> /// 数据保存，使用多线程读取数据写入文件中 /// </summary>
    public class DataSave
    {
        private static DataSave instance;
        //private const string completeTaskName = "CompleteTask";
        //private const string runtimeTaskName = "RuntimeTask";
        //private const string obtainPackagesName = "ObtainPackages";

        public static DataSave Instance
        {
            get
            {
                if(instance == null)
                {
                    instance = new DataSave();
                }
                return instance;
            }
        }

        private bool isSaveComplete;
        public bool IsSaveComplete => isSaveComplete;
        private DataSave()
        {
        }

        /// <summary> /// 保存数据的呼叫接口 /// </summary>
        public void SaveData()
        {
            isSaveComplete = false;
            AsyncLoad.Instance.AddAction(AsynSaveData);
        }

        //多线程保存数据
        private void AsynSaveData()
        {
            if (DataLoad.LoadAsset == null)
                return;
            SaveTaskData();
            SaveInfoData();
            isSaveComplete =true;
        }

        private void SaveTaskData()
        {
            string runtimeTask = GameTask.AsynTaskControl.Instance.GetRuntimeTaskData();
            string completeTask = GameTask.AsynTaskControl.Instance.GetCompleteTaskData();
            FileLoadAsset loadAsset = DataLoad.LoadAsset;
            string prefit = Application.streamingAssetsPath;
            FileReadAndWrite.WriteFile(prefit + loadAsset.FindPath(DataLoad.runtimeTaskName),
                runtimeTask);
            FileReadAndWrite.WriteFile(prefit + loadAsset.FindPath(DataLoad.completeTaskName),
                completeTask);
            Debug.Log("保存完毕");
        }

        private void SaveInfoData()
        {
            string prefit = Application.streamingAssetsPath;
            string infoData = Info.InfoMap.Instance.GetSaveString();
            FileLoadAsset loadAsset = DataLoad.LoadAsset;
            FileReadAndWrite.WriteFile(prefit + 
                loadAsset.FindPath(DataLoad.playerInfo), infoData);
        }

        public static void ClearData()
        {
            FileLoadAsset loadAsset = Resources.Load<FileLoadAsset>("Common/FileLoadAsset");
            string prefit = Application.streamingAssetsPath;
            //清理任务数据
            FileReadAndWrite.WriteFile(prefit + 
                loadAsset.FindPath(DataLoad.runtimeTaskName), "");
            FileReadAndWrite.WriteFile(prefit + 
                loadAsset.FindPath(DataLoad.completeTaskName), "");
            //清除背包数据
            FileReadAndWrite.WriteFile(prefit +
                loadAsset.FindPath(DataLoad.obtainPackagesName), "");
        }



    }
}