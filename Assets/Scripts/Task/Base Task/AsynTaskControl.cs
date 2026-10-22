using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using System.Text;

namespace GameTask
{
    public enum TaskMode
    {
        /// <summary>   /// 任务未开始   /// </summary>
        NotStart = 0,
        /// <summary>   /// 任务开始中   /// </summary>
        Start = 1,
        /// <summary>   /// 任务完成     /// </summary>
        Finish = 2,
    }
    struct TaskInfo
    {
        public string Name;
        public TaskMode state;
    }

    public struct TaskLoadData
    {
        /// <summary>  /// 所有的任务名    /// </summary>
        public List<string> allChapter;
        /// <summary>  ///获得的任务以及该任务执行到的位置    /// </summary>
        public List<KeyValuePair<string, string>> runtimeChapter;
        /// <summary>  /// 完成了的任务名    /// </summary>
        public List<string> completeChapter;
    }

    public class AsynTaskControl
    {
        private static AsynTaskControl instance;
        public static AsynTaskControl Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new AsynTaskControl();
                }
                return instance;
            }
        }

        /// <summary>        /// 进行反射查找类的名称前缀        /// </summary>
        const string chapterPrefix = "Task.";

        /// <summary>        /// 进行中的任务        /// </summary>
        private List<Chapter> exectuteTasks = new List<Chapter>();

        /// <summary>        /// 所有任务的映射容器，<编号，名称>        /// </summary>
        private Dictionary<int, TaskInfo> taskMap;
        /// <summary>
        /// 当前运行的场景名称，因为任务是在子线程中加载的，因此需要在协程中进行名称获取
        /// </summary>
        string currentSceneName;
        Assembly assembly = Assembly.GetExecutingAssembly();

        private bool isLoading = true;
        public bool IsLoading => isLoading;

        private TaskLoadData taskData;

        /// <summary>        /// 返回开始状态，重置所有任务        /// </summary>
        public static void ClearData()
        {
            //Common.FileReadAndWrite.WriteFile(completeTaskPath, "");
            //Common.FileReadAndWrite.WriteFile(obtainTaskPath, "");
            Common.DataSave.ClearData();
        }

        /// <summary>
        /// 确定任务数据，该方法只是用来存储任务信息，还没有进行任务的加载行为
        /// </summary>
        public void LoadTaskData(TaskLoadData taskData)
        {
            isLoading = true;
            this.taskData = taskData;       //设置任务数据
            AsyncLoad.Instance.AddAction(AsynLoadData);     //多线程加载
        }

        /// <summary>
        /// 执行章节行为，也就是进入游戏场景时的任务初始化
        /// </summary>
        public void RuntimeTask()
        {
            currentSceneName = Common.SceneControl.Instance.GetRuntimeSceneName();
            AsyncLoad.Instance.AddAction(AsynExectueChapter);       //协程加载任务
        }


        /// <summary>
        /// 检查任务是否完成，任务加载是以分支为根据的，所以只需要检查前面是否已经完成就够了，
        /// 因此这里只提供检查的方法
        /// </summary>
        /// <param name="chapterId">任务的编号，注意该编号值要唯一</param>
        public bool CheckChapterIsComplete(int chapterId)
        {
            return taskMap[chapterId].state == TaskMode.Finish;
        }

        /// <summary>
        /// 检测任务是否正在运行
        /// </summary>
        /// <param name="chaptedId"></param>
        public bool CheckChapterIsRuntime(int chaptedId)
        {
            return taskMap[chaptedId].state == TaskMode.Start;
        }

        /// <summary>        /// 任务完成的通用行为，将该任务退出,然后标记为完成        /// </summary>
        /// <param name="chapter">要完成的任务</param>
        public void CompleteChapter(Chapter chapter)
        {
            exectuteTasks.Remove(chapter);
            //调用退出函数
            chapter.ExitChapter();
            TaskInfo info = taskMap[chapter.ChapterID];
            info.state = TaskMode.Finish;         //完成任务
            taskMap[chapter.ChapterID] = info;
        }

        /// <summary>        /// 获取目前正在运行的所有任务        /// </summary>
        public List<Chapter> GetExecuteChapter()
        {
            return exectuteTasks;
        }

        /// <summary>        /// 获取单个真正运行的任务        /// </summary>
        public Chapter GetExecuteChapterByIndex(int index)
        {
            return exectuteTasks[index];
        }

        /// <summary>        /// 检查任务的调用函数        /// </summary>
        public void CheckChapter(int chaptherId, Interaction.InteracteInfo data)
        {
            for (int i = 0; i < exectuteTasks.Count; i++)
            {
                if (exectuteTasks[i].ChapterID == chaptherId)
                {
                    exectuteTasks[i].CheckTask(data);
                    return;
                }
            }
        }

        /// <summary>
        /// 注册章节函数，用来将章节加入到已获取的章节中，
        /// 同时进行判断，如果是该场景的章节就会进行初始化
        /// </summary>
        /// <param name="chapter">章节名称</param>
        /// <returns>是否添加成功</returns>
        public bool AddChapter(Chapter chapter)
        {
            if (exectuteTasks == null)
            {
                exectuteTasks = new List<Chapter> { chapter };
            }
            else
            {
                for (int i = 0; i < exectuteTasks.Count; i++)
                {
                    if (exectuteTasks[i].ChapterID == chapter.ChapterID)
                    {
                        Debug.Log("出现重复任务");
                        return false;
                    }
                }
                exectuteTasks.Add(chapter);
            }
            //检查是否运行在该场景
            if(chapter.RuntimeScene == currentSceneName)
                chapter.BeginChapter();

            var temp = taskMap[chapter.ChapterID];
            temp.state = TaskMode.Start;    //标记为开始
            taskMap[chapter.ChapterID] = temp;
            return true;
        }

        /// <summary>
        /// 注册章节函数，用来将章节加入到已获取的章节中，
        /// 同时进行判断，如果是该场景的章节就会进行初始化
        /// </summary>
        /// <param name="chapterId">章节编号</param>
        /// <returns>是否添加成功</returns>
        public bool AddChapter(int chapterId)
        {
            TaskInfo info = taskMap[chapterId];
            if (info.state != 0)
                return false;
            info.state = TaskMode.Start;
            Chapter chapter = GetChapter(info.Name);

            if (exectuteTasks == null)
            {
                exectuteTasks = new List<Chapter> { chapter };
            }
            else
            {
                for (int i = 0; i < exectuteTasks.Count; i++)
                {
                    if (exectuteTasks[i].ChapterID == chapter.ChapterID)
                    {
                        Debug.Log("出现重复任务");
                        return false;
                    }
                }
                exectuteTasks.Add(chapter);
            }
            //检查是否运行在该场景
            if (chapter.RuntimeScene == Common.SceneControl.Instance.GetRuntimeSceneName())
                chapter.BeginChapter();

            var temp = taskMap[chapter.ChapterID];
            temp.state = TaskMode.Start;    //标记为开始
            taskMap[chapter.ChapterID] = temp;
            return true;
        }

        /// <summary> /// 获取当前已获取的任务数据   /// </summary>
        /// <returns>已获取的任务数据的字符串</returns>
        public string GetRuntimeTaskData()
        {
            StringBuilder re = new StringBuilder("");
            for(int i=0; i<exectuteTasks.Count; i++)
            {
                re = re.Append("<" + exectuteTasks[i].ChapterID.ToString() + 
                    "=" + exectuteTasks[i].Part.GetThisPartString() + ">\n");
            }
            return re.ToString();
        }

        /// <summary>  /// 获取当前已经完成的任务数据   /// </summary>
        /// <returns>已经完成的任务数据</returns>
        public string GetCompleteTaskData()
        {
            StringBuilder str = new StringBuilder("");
            foreach(var info in taskMap)
            {
                if(info.Value.state == TaskMode.Finish)
                {
                    str = str.Append("<" + info.Key.ToString() + ">\n");
                }
            }
            return str.ToString();
        }


        private AsynTaskControl()
        {
        }
        //多线程存储数据
        private void AsynLoadData()
        {
            LoadAllTask(taskData.allChapter);           //加载所有任务
            LoadRuntimeTask(taskData.runtimeChapter);   //加载运行中的任务
            LoadCompleteTask(taskData.completeChapter); //加载完成了的任务
            isLoading = false;      //加载结束
        }

        //多线程执行任务行为
        private void AsynExectueChapter()
        {
            for(int i=0; i < exectuteTasks.Count; i++)
            {
                //只有在该场景的任务才会加载
                if (exectuteTasks[i].RuntimeScene == currentSceneName)
                    exectuteTasks[i].ExecuteNowPart();    //选中的任务初始化
            }
            if (taskMap == null)
                return;

            Debug.Log("123");

            List<int> keys = new List<int>(taskMap.Keys);
            foreach(var key in keys)
            {
                Chapter temp;
                var chapter = taskMap[key];
                switch (chapter.state)
                {
                    case TaskMode.NotStart:     //未开始的任务检查一下可不可以开始
                        temp = GetChapter(chapter.Name);
                        temp.CheckAndLoadChapter();
                        break;
                    case TaskMode.Finish:       //初始化已经完成了的方法
                        temp = GetChapter(chapter.Name);
                        temp.CompleteChapter(temp.RuntimeScene == currentSceneName);
                        break;
                }
            }
        }

        /// <summary>        /// 通过反射创建章节对象        /// </summary>
        /// <param name="chapterName">章节名称</param>
        private Chapter GetChapter(string chapterName)
        {
            return (Chapter)assembly.CreateInstance(chapterPrefix + chapterName);
        }

        /// <summary>
        /// 生成所有任务的映射关系表
        /// </summary>
        /// <param name="allTasks">所有任务的名称</param>
        private void LoadAllTask(List<string> allTasks)
        {
            taskMap?.Clear();
            taskMap = new Dictionary<int, TaskInfo>();
            if (allTasks == null) return;
            for(int i=0; i<allTasks.Count; i++)
            {
                Chapter chapter = GetChapter(allTasks[i]);
                //对所有任务进行初始化
                taskMap.Add(chapter.ChapterID, new TaskInfo
                {
                    state = TaskMode.NotStart,
                    Name = allTasks[i],
                });
            }
        }

        /// <summary>
        /// 加载获取了的任务的文件
        /// 任务存储格式：<ChapterId nowPartIndex>，章节编号+当前子任务的编号
        /// </summary>
        private void LoadRuntimeTask(List<KeyValuePair<string, string>> runtimeTask)
        {
            if(runtimeTask == null) return;
            for(int i=0; i<runtimeTask.Count; i++)
            {
                int chapterId = int.Parse(runtimeTask[i].Key);
                TaskInfo info = taskMap[chapterId];
                info.state = TaskMode.Start;
                taskMap[chapterId] = info;
                //注册任务进入列表中
                LoadRuntimeChapter(GetChapter(taskMap[chapterId].Name), runtimeTask[i].Value);
            }
        }

        /// <summary>        /// 加载所有完成了的任务        /// </summary>
        private void LoadCompleteTask(List<string> completeTasks)
        {
            if (completeTasks == null) return;
            for(int i=0; i<completeTasks.Count; i++)
            {
                int chapterId = int.Parse(completeTasks[i]);
                TaskInfo info = taskMap[chapterId];
                info.state = TaskMode.Finish;
                taskMap[chapterId] = info;
            }
        }

        /// <summary>
        /// 加载运行中的任务，也就是已经获取的任务进行初始化
        /// </summary>
        /// <param name="chapter">章节</param>
        /// <param name="taskId">小节编号</param>
        private void LoadRuntimeChapter(Chapter chapter, string partString)
        {
            if(exectuteTasks == null)
                exectuteTasks = new List<Chapter>();
            for(int i=0; i<exectuteTasks.Count; i++)
            {
                if (exectuteTasks[i].ChapterID == chapter.ChapterID)
                {
                    Debug.LogError("重复添加任务");
                    return;
                }
            }
            chapter.PartName = partString;       //设置章节编号
            exectuteTasks.Add(chapter);
        }

    }
}