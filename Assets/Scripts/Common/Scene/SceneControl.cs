using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common
{
    /// <summary>/// 场景开始时需要的初始化数据，比如主角位置/// </summary>
    public struct SceneBeginData
    {
        public bool haveBeginData;
        public Vector3 beginPos;
    }

    /// <summary>
    /// 场景控制类，用来控制场景切换，也就是切换到加载场景，
    /// 然后根据文本中的数据进行场景控制
    /// </summary>
    public class SceneControl : MonoBehaviour
    {
        private static SceneControl instance;
        public static SceneControl Instance
        {
            get
            {
                if(instance == null)
                {
                    GameObject gameObject = new GameObject("SceneChange");
                    gameObject.AddComponent<SceneControl>();
                }
                return instance;
            }
        }

        private string targetScene;
        private int beginIndex;

        AsyncOperation asyncStatic;

        SceneBeginData beginData;
        public SceneBeginData BeginData => beginData;

        private void Awake()
        {
            if(instance != null)
            {
                Destroy(gameObject);
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// 改变场景，只会加载到加载场景，具体接下来的加载情况由加载场景决定，
        /// 同时会保存目标场景名称以及起始位置标号，可能用的上
        /// </summary>
        /// <param name="targetSceneName">目标场景名称</param>
        /// <param name="beginIndex">场景起始位置编号</param>
        public void ChangeScene(string targetSceneName, int beginIndex)
        {
            targetScene = targetSceneName;
            this.beginIndex = beginIndex;
            SceneManager.LoadScene("LoadScene");
        }

        /// <summary>
        /// 加载初始化的场景数据，由加载场景第一次启动时调用，
        /// 读取场景数据以及角色存储的初始坐标
        /// </summary>
        public void LoadSceneDataByFile(List<KeyValuePair<string, string>> sceneDatas,
            string beginScene)
        {
            //是游戏第一次加载，读取初始场景
            if (sceneDatas == null)
            {
                ChangeSceneDirect(beginScene);    //直接加载默认场景
                return;
            }
            targetScene = null;
            Vector3 beginPos = Vector3.zero;
            //枚举所有数据
            for(int i = 0; i < sceneDatas.Count; i++)
            {
                switch (sceneDatas[i].Key)
                {
                    case "SceneName":
                        targetScene = sceneDatas[i].Value;
                        break;
                    case "Position":
                        string[] strs = sceneDatas[i].Value.Split(',');
                        beginPos = new Vector3(float.Parse(strs[0]),
                            float.Parse(strs[1]), float.Parse(strs[2]));
                        break;
                }
            }
            if(targetScene == null)
            {
                Debug.LogError("数据有误");
                return;
            }
            //标记为正常加载
            beginData = new SceneBeginData
            {
                beginPos = beginPos,
                haveBeginData = true,
            };
            //切换场景
            ChangeScene();
        }

        /// <summary>
        /// 非游戏场景的第一次加载，读取设置的目标值即可
        /// </summary>
        /// <param name="prefix">包含所有前缀的文本路径</param>
        public void LoadSceneDataBySet(string prefix)
        {
            string path = prefix + targetScene + ".scene";
            List<string> strings = FileReadAndWrite.ReadFileByAngleBrackets(path);
            if(strings == null)
            {
                Debug.LogError("场景位置文件出错，没有找到" + beginIndex.ToString());
                return;
            }
            for(int i=0; i<strings.Count; i++)
            {
                string[] strs = strings[i].Split('=');
                int index;
                if (int.TryParse(strs[0], out index))
                {
                    if (index == beginIndex)//找到了
                    {
                        string[] strs2 = strs[1].Split(',');
                        Vector3 beginPos = new Vector3(float.Parse(strs2[0]),
                            float.Parse(strs2[1]), float.Parse(strs2[2]));
                        beginData = new SceneBeginData
                        {
                            beginPos = beginPos,
                            haveBeginData = true,
                        };
                        ChangeScene();
                        return;
                    }
                }
                else
                {
                    Debug.LogError("数据出错 " + strs[0]);
                    return;
                }
            }
            //到这里就是失败了
            Debug.LogError("场景位置文件出错，没有找到编号" + beginIndex.ToString());
            return;
        }

        /// <summary>   /// 直接加载场景，不读取所有数据    /// </summary>
        public void ChangeSceneDirect(string targetSceneName)
        {
            //标识为空数据，没有初始化数据
            beginData = new SceneBeginData()
            {
                beginPos = Vector3.zero, haveBeginData = false,
            };
            targetScene = targetSceneName;
            ChangeScene();
        }

        /// <summary> /// 切换场景的调用函数，多线程加载 /// </summary>
        private void ChangeScene()
        {
            SustainCoroutine.Instance.AddCoroutine(ReadyChangeScene);
        }
        //准备改变场景的方法
        bool ReadyChangeScene()
        {
            asyncStatic = SceneManager.LoadSceneAsync(targetScene);
            asyncStatic.allowSceneActivation = false;
            SustainCoroutine.Instance.AddCoroutine(AsynLoadScene);
            return true;
        }

        //等待场景改变
        bool AsynLoadScene()
        {
            if(asyncStatic.progress < 0.9f)
            {
                Debug.Log(asyncStatic.progress);
                return false;
            }
            SceneManager.LoadScene(targetScene, LoadSceneMode.Single);
            return true;
        }

        /// <summary>        /// 获得正在运行中的场景的名称        /// </summary>
        public string GetRuntimeSceneName()
        {
            return SceneManager.GetActiveScene().name;
        }

        public void GameExit()
        {
            Application.Quit();
        }
    }
}