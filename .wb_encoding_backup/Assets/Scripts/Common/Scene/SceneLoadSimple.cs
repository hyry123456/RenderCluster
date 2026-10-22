using Common;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SceneLoadSimple : MonoBehaviour
{
    public const string allTaskName = "AllTask";
    public const string completeTaskName = "CompleteTask";
    public const string runtimeTaskName = "RuntimeTask";
    public const string allPackagesName = "AllPackages";
    public const string obtainPackagesName = "ObtainPackages";
    public const string playerInfo = "PlayerData";
    public const string scenePrefix = "ScenePrefix";
    public const string sceneData = "SceneData";

    [SerializeField]
    private FileLoadAsset asset = default;

    private static FileLoadAsset loadAsset;
    /// <summary>
    /// 文件保存时用到的映射表，该映射表可能为空，
    /// 因为只有正确读取数据时该值才会被赋予
    /// </summary>
    public static FileLoadAsset LoadAsset => loadAsset;

    private static bool isLoadComplete = false;
    public static bool IsLoadComplete => isLoadComplete;

    public string beginSceneName;

    /// <summary> /// 设置该项目需要重新加载，比如闯关失败之类的  /// </summary>
    public static void SetDataNeedReload()
    {
        isLoadComplete = false;
    }

    private void Awake()
    {
        if (loadAsset == null)
            loadAsset = asset;
        SceneControl control = SceneControl.Instance;   //提前加载一下
        SustainCoroutine sustain = SustainCoroutine.Instance;

        //只有第一次加载
        if (!isLoadComplete)
            AsyncLoad.Instance.AddAction(LoadList);     //加载任务
        else
            AsyncLoad.Instance.AddAction(AsynReady);     //加载任务
    }

    private void OnDestroy()
    {
    }

    /// <summary>
    /// 加载列表
    /// </summary>
    private void LoadList()
    {
        AsynLoadTask();
        AsynLoadPackage();
        AsynLoadInfo();
        //AsynLoadScene();        //加载场景同时切换场景
        isLoadComplete = true;      //表示加载完毕
    }

    /// <summary> /// 多线程加载任务  /// </summary>
    private void AsynLoadTask()
    {
        GameTask.TaskLoadData loadData = new GameTask.TaskLoadData();
        FileLoadMap loadMap = FileLoadMap.Instance(loadAsset);
        loadData.allChapter = loadMap.LoadFile(allTaskName);
        loadData.runtimeChapter = loadMap.LoadFileToPair(runtimeTaskName);
        loadData.completeChapter = loadMap.LoadFile(completeTaskName);
        GameTask.AsynTaskControl.Instance.LoadTaskData(loadData);
    }

    /// <summary>  /// 多线程加载背包 /// </summary>
    private void AsynLoadPackage()
    {
        Package.PackageLoadData loadData = new Package.PackageLoadData();
        FileLoadMap loadMap = FileLoadMap.Instance(loadAsset);
        loadData.packageClassNames = loadMap.LoadFile(allPackagesName);
        loadData.obtainItemAndCounts = loadMap.LoadFileToPair(obtainPackagesName);
        Package.PackageControl.Instance.LoadPackageData(loadData);
    }

    /// <summary>  /// 多线程加载角色信息 /// </summary>
    private void AsynLoadInfo()
    {
        FileLoadMap loadMap = FileLoadMap.Instance(loadAsset);
        List<KeyValuePair<string, string>> loadData =
            loadMap.LoadFileToPair(playerInfo);
        Info.InfoMap.Instance.LoadInfoMap(loadData);
    }

    /// <summary>  /// 多线程加载场景信息   /// </summary>
    private void AsynLoadScene()
    {
        FileLoadMap loadMap = FileLoadMap.Instance(loadAsset);
        List<KeyValuePair<string, string>> loadData =
            loadMap.LoadFileToPair(sceneData);
        SceneControl.Instance.LoadSceneDataByFile(loadData, beginSceneName);
    }

    private void AsynReady()
    {
        AsyncLoad.Instance.AddAction(ReadySceneData);
    }

    /// <summary>  /// 准备场景数据，用来在非第一次加载游戏场景时调用  /// </summary>
    private void ReadySceneData()
    {
        SceneControl.Instance.LoadSceneDataBySet(
            Application.streamingAssetsPath + asset.FindPath(scenePrefix));
    }
}
