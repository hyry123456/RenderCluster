using UnityEngine;
using System;
using System.IO;
using System.Diagnostics;
using System.Collections;

public class ExceptionHandler : MonoBehaviour
{
    //是否作为异常处理者
    public bool IsHandler = false;
    //是否退出程序当异常发生时
    public bool IsQuitWhenException = true;
    //异常日志保存路径（文件夹）
    private string LogPath;
    //Bug反馈程序的启动路径
    private string BugExePath;

    void Awake()
    {
        DontDestroyOnLoad(this);
        LogPath = Application.dataPath.Substring(0, Application.dataPath.LastIndexOf("/"));
        BugExePath = Application.dataPath.Substring(0, Application.dataPath.LastIndexOf("/")) + "\\Bug.exe";

        //注册异常处理委托
        if (IsHandler)
        {
            Application.logMessageReceived += Handler;
        }
    }

    void OnDestory()
    {
        //清除注册
        Application.logMessageReceived -= Handler;
    }

    void Handler(string logString, string stackTrace, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
        {
            string logPath = LogPath + "\\" + DateTime.Now.ToString("yyyy_MM_dd HH_mm_ss") + ".log";
            //打印日志
            if (Directory.Exists(LogPath))
            {
                File.AppendAllText(logPath, "[time]:" + DateTime.Now.ToString() + "\r\n");
                File.AppendAllText(logPath, "[type]:" + type.ToString() + "\r\n");
                File.AppendAllText(logPath, "[exception message]:" + logString + "\r\n");
                File.AppendAllText(logPath, "[stack trace]:" + stackTrace + "\r\n");
            }
            //启动bug反馈程序
            if (File.Exists(BugExePath))
            {
                ProcessStartInfo pros = new ProcessStartInfo();
                pros.FileName = BugExePath;
                pros.Arguments = "\"" + logPath + "\"";
                Process pro = new Process();
                pro.StartInfo = pros;
                pro.Start();
            }
            //退出程序，bug反馈程序重启主程序
            if (IsQuitWhenException)
            {
                Application.Quit();
            }
        }
    }
}