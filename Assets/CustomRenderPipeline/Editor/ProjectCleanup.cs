using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CustomRenderPipline.EditorTools
{
    /// <summary>
    /// 一次性清理工程里的历史遗留问题（Viking Village 资产 + 本项目归档代码共同造成）。
    ///
    /// A. "The referenced script on this Behaviour is missing!" / "(Unknown)"
    ///    组件序列化里记录的脚本 GUID 已经不存在（脚本被删除或移出 Assets/ 归档）。
    ///    说明：日志里看到的只是「运行时被实例化到」的那几个；预制体资产里的虽然不报警，
    ///    一旦被实例化就会冒出来，而且 Unity 每帧/每次加载都可能重复刷屏，所以必须全工程清。
    ///
    /// B. 悬空的对象引用（"丢失的 Component" 的另一种形态）
    ///    某个组件的序列化字段原本引用一个 Component / GameObject / Asset，但目标已被删除。
    ///    特征：objectReferenceInstanceIDValue != 0 却取不到对象。Unity 不会为此报警，
    ///    只会在 Inspector 里显示 Missing，运行时可能直接 NullReferenceException。
    ///
    /// C. "BoxCollider does not support negative scale or size."
    ///    某个 collider 的累积（lossy）缩放出现负分量。只自动修“纯碰撞体节点”
    ///    （无 Renderer / MeshFilter 且局部旋转为单位四元数）——盒子关于自身中心镜像后
    ///    是完全相同的盒，取反不改变碰撞体；若负缩放来自带渲染体的节点则只报告不自动改，
    ///    避免镜像视觉模型。
    ///
    /// 用法：菜单 Tools/Project Cleanup/...
    /// 注意：工程没有版本控制，执行前请确认已备份（或先用“仅扫描”看清单）。
    /// </summary>
    public static class ProjectCleanup
    {
        private const string MENU_ROOT = "Tools/Project Cleanup/";
        private const string MENU_SCAN = MENU_ROOT + "1. 仅扫描（不改动任何文件）";
        private const string MENU_CLEAN_SCRIPTS = MENU_ROOT + "2. 清理缺失脚本（全工程场景+预制体）";
        private const string MENU_CLEAN_DANGLING = MENU_ROOT + "3. 清理悬空对象引用（危险，可选）";

        private const string BuiltinGuid = "0000000000000000f000000000000000";

        /// <summary>
        /// Unity 内部序列化字段。这些字段即使取不到对象也绝不能清：
        /// m_PrefabInstance / m_PrefabAsset / m_CorrespondingSourceObject 是预制体关联，
        /// m_GameObject / m_Script 是组件自身归属，清掉会直接破坏场景结构。
        /// </summary>
        private static readonly HashSet<string> InternalProps = new HashSet<string>
        {
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_CorrespondingSourceObject",
            "m_PrefabParentObject",
            "m_PrefabInternalAssetPath",
            "m_SourcePrefab",
            "m_TransformParent",
            "m_Modifications",
            "m_GameObject",
            "m_Script",
        };

        private static readonly Regex ScriptGuidRegex = new Regex(
            @"m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-fA-F]+),\s*type:\s*-?\d+\}",
            RegexOptions.Compiled);

        private static int removedScripts;
        private static int danglingFound;
        private static int danglingCleared;
        private static int fixedColliders;
        private static int skippedColliders;

        // ============================================================ 菜单

        [MenuItem(MENU_SCAN, false, 10)]
        public static void Scan()
        {
            removedScripts = 0;
            danglingFound = 0;
            fixedColliders = 0;
            skippedColliders = 0;
            var report = new List<string>();
            report.Add("====== ProjectCleanup 扫描（只读，不改动任何文件） ======");

            foreach (var target in BuildTargets(false))
                ScanTarget(target, report);

            report.Add("");
            report.Add("====== 扫描汇总 ======");
            report.Add("缺失脚本组件        : " + removedScripts);
            report.Add("悬空对象引用        : " + danglingFound);
            report.Add("负缩放 Collider     : " + (fixedColliders + skippedColliders)
                       + "（其中需人工处理 " + skippedColliders + " 个）");
            report.Add("");
            report.Add("提示：执行菜单第 2 项清理缺失脚本；第 3 项才会清空悬空引用（有风险，请先看清单）。");
            Debug.Log(string.Join("\n", report.ToArray()));
        }

        [MenuItem(MENU_CLEAN_SCRIPTS, false, 11)]
        public static void CleanMissingScripts()
        {
            removedScripts = 0;
            danglingFound = 0;
            fixedColliders = 0;
            skippedColliders = 0;
            var log = new List<string>();
            log.Add("====== ProjectCleanup：清理缺失脚本（全工程） ======");

            foreach (var target in BuildTargets(false))
                CleanTarget(target, log, false);

            SaveAll();

            log.Add("");
            log.Add("====== 汇总 ======");
            log.Add("移除缺失脚本组件    : " + removedScripts);
            log.Add("顺带修正负缩放 Collider : " + fixedColliders
                    + "（需人工处理 " + skippedColliders + " 个，见上方日志）");
            log.Add("悬空对象引用        : " + danglingFound + " 个（本次未清除，需要请跑菜单第 3 项）");
            Debug.Log(string.Join("\n", log.ToArray()));
        }

        [MenuItem(MENU_CLEAN_DANGLING, false, 12)]
        public static void CleanDanglingReferences()
        {
            if (!EditorUtility.DisplayDialog("确认清理悬空对象引用",
                    "将把「引用了已删除对象」的序列化字段全部置空。\n" +
                    "这些字段在 Inspector 里显示为 Missing，但某些可能是脚本在运行时动态赋值的，\n" +
                    "置空后需要重新指定。\n\n工程没有版本控制，确定已备份？",
                    "确认清理", "取消"))
                return;

            removedScripts = 0;
            danglingFound = 0;
            danglingCleared = 0;
            fixedColliders = 0;
            skippedColliders = 0;
            var log = new List<string>();
            log.Add("====== ProjectCleanup：清理悬空对象引用 ======");

            foreach (var target in BuildTargets(true))
                CleanTarget(target, log, true);

            SaveAll();

            log.Add("");
            log.Add("====== 汇总 ======");
            log.Add("发现悬空引用        : " + danglingFound);
            log.Add("已置空              : " + danglingCleared);
            Debug.Log(string.Join("\n", log.ToArray()));
        }

        // ============================================================ 目标收集

        private struct Target
        {
            public string Path;
            public bool IsPrefab;
            public bool IsActiveScene;
        }

        /// <summary>
        /// 收集需要处理的目标：当前打开的场景 + 全工程所有「确实含缺失脚本」的场景与预制体。
        /// activeSceneOnly=true 时只返回当前场景（用于危险的悬空引用清理，默认保守）。
        /// 含静态预筛（读文件文本 + AssetDatabase.GUIDToAssetPath 判定 GUID 是否可解析），
        /// 避免为了几个组件去 LoadContents 全部 105 个预制体。
        /// 注意：预筛只是优化，真正的删除由 Unity 自己的解析结果驱动
        /// （m_Component 里取不到对象的才删），所以预筛误报不会误删组件。
        /// </summary>
        private static List<Target> BuildTargets(bool activeSceneOnly)
        {
            var targets = new List<Target>();
            var seen = new HashSet<string>();

            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            string activePath = string.IsNullOrEmpty(active.path) ? null : active.path;
            if (activePath != null)
            {
                targets.Add(new Target { Path = activePath, IsPrefab = false, IsActiveScene = true });
                seen.Add(activePath);
            }

            if (activeSceneOnly)
                return targets;   // 保守模式：只看当前场景

            foreach (var guid in AssetDatabase.FindAssets("t:Scene t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || seen.Contains(path)) continue;
                if (!path.EndsWith(".unity") && !path.EndsWith(".prefab")) continue;
                if (!FileHasMissingScript(path)) continue;   // 静态预筛：干净的直接跳过

                seen.Add(path);
                targets.Add(new Target
                {
                    Path = path,
                    IsPrefab = path.EndsWith(".prefab"),
                    IsActiveScene = false,
                });
            }
            return targets;
        }

        /// <summary>静态判断文件里是否存在不可解析的脚本 GUID。</summary>
        private static bool FileHasMissingScript(string path)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch { return false; }

            foreach (Match m in ScriptGuidRegex.Matches(text))
            {
                var g = m.Groups[1].Value;
                if (string.Equals(g, BuiltinGuid, StringComparison.OrdinalIgnoreCase)) continue;
                // m_Script 必须指向 MonoScript（.cs）。GUID 解析不到、或解析出来不是 .cs，
                // 都说明这个脚本引用已经失效。
                var assetPath = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(assetPath)) return true;
                if (!assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ============================================================ 扫描 / 清理

        private static void ScanTarget(Target target, List<string> report)
        {
            report.Add("");
            report.Add("---- " + target.Path + (target.IsActiveScene ? "  (当前打开的场景)" : ""));
            if (target.IsPrefab)
            {
                var root = PrefabUtility.LoadPrefabContents(target.Path);
                if (root == null) { report.Add("   加载失败，跳过"); return; }
                try { ScanHierarchy(root, Path.GetFileName(target.Path), report); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            else
            {
                if (target.IsActiveScene)
                {
                    foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                        ScanHierarchy(go, "scene", report);
                }
                else
                {
                    var sc = EditorSceneManager.OpenScene(target.Path, OpenSceneMode.Additive);
                    try
                    {
                        foreach (var go in sc.GetRootGameObjects())
                            ScanHierarchy(go, Path.GetFileName(target.Path), report);
                    }
                    finally { EditorSceneManager.CloseScene(sc, true); }
                }
            }
        }

        private static void CleanTarget(Target target, List<string> log, bool clearDangling)
        {
            log.Add("");
            log.Add("---- " + target.Path);
            if (target.IsPrefab)
                CleanPrefabAsset(target.Path, log, clearDangling);
            else
                CleanSceneAsset(target.Path, target.IsActiveScene, log, clearDangling);
        }

        private static void CleanPrefabAsset(string path, List<string> log, bool clearDangling)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            if (root == null) { log.Add("   加载失败，跳过"); return; }
            try
            {
                int before = removedScripts;
                int dcBefore = danglingCleared;
                int cBefore = fixedColliders;
                CleanHierarchy(root, Path.GetFileName(path), log, clearDangling);

                // 只有真的改了东西才保存，避免无谓重写预制体
                bool changed = removedScripts != before
                               || danglingCleared != dcBefore
                               || fixedColliders != cBefore;
                if (!changed) { log.Add("   无需改动"); return; }

                if (HasNestedPrefabInstances(path))
                {
                    log.Add("   [跳过保存] 该预制体含嵌套预制体实例，LoadContents+SaveAs 会拍平它，需人工处理。");
                    return;
                }
                PrefabUtility.SaveAsPrefabAsset(root, path);
                log.Add("   已保存");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void CleanSceneAsset(string path, bool isActive, List<string> log, bool clearDangling)
        {
            if (isActive)
            {
                foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    CleanHierarchy(go, "scene", log, clearDangling);
                return;
            }
            var sc = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                foreach (var go in sc.GetRootGameObjects())
                    CleanHierarchy(go, Path.GetFileName(path), log, clearDangling);
                EditorSceneManager.SaveScene(sc);
                log.Add("   已保存");
            }
            finally { EditorSceneManager.CloseScene(sc, true); }
        }

        // ============================================================ 核心

        private static void ScanHierarchy(GameObject root, string label, List<string> report)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                int n = RemoveMissingScripts(t.gameObject, false);
                if (n > 0)
                {
                    removedScripts += n;
                    report.Add(string.Format("   [{0}] {1} -> {2} 个缺失脚本组件",
                        label, FullPath(t.gameObject), n));
                }
                CheckDanglingReferences(t.gameObject, label, report, false);
                ReportNegativeCollider(t, report);
            }
        }

        private static void CleanHierarchy(GameObject root, string label, List<string> log, bool clearDangling)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                int n = RemoveMissingScripts(go, true);
                if (n > 0)
                {
                    removedScripts += n;
                    log.Add(string.Format("   [{0}] {1} -> 移除 {2} 个缺失脚本组件",
                        label, FullPath(go), n));
                }
                CheckDanglingReferences(go, label, log, clearDangling);
                FixNegativeColliderScale(t, log);
            }
        }

        /// <summary>移除 GameObject 上所有“缺失脚本/缺失组件”。返回移除数量。</summary>
        /// <param name="apply">false 时只统计不落盘（供“仅扫描”使用）。</param>
        private static int RemoveMissingScripts(GameObject go, bool apply)
        {
            // 不能靠 GetComponents&lt;Component&gt;() + DestroyImmediate：缺失脚本在数组里就是 null，
            // DestroyImmediate(null) 是空操作。必须直接删 m_Component 数组里的空引用项。
            // 注意：这里删的是「所有」空引用项，既包括脚本被删的 MonoBehaviour，
            // 也包括引用失效的内建组件，两者在 Inspector 里都表现为 Missing。
            var so = new SerializedObject(go);
            var prop = so.FindProperty("m_Component");
            if (prop == null) return 0;

            int removed = 0;
            for (int i = prop.arraySize - 1; i >= 0; i--)
            {
                if (prop.GetArrayElementAtIndex(i).objectReferenceValue == null)
                {
                    if (apply) prop.DeleteArrayElementAtIndex(i);
                    removed++;
                }
            }
            if (removed > 0 && apply) so.ApplyModifiedProperties();
            return removed;
        }

        /// <summary>
        /// 找出组件里“悬空的对象引用”：字段原本引用某个 Component/GameObject/Asset，
        /// 但目标已被删除。特征：objectReferenceInstanceIDValue != 0 却取不到对象。
        /// 返回发现数量；clear=true 时把这些字段置空。
        /// </summary>
        private static int CheckDanglingReferences(GameObject go, string label,
            List<string> log, bool clear)
        {
            int found = 0;
            var comps = go.GetComponents<Component>();
            foreach (var comp in comps)
            {
                if (comp == null) continue;      // 缺失脚本，已由 RemoveMissingScripts 处理
                if (comp is Transform) continue; // Transform 没有用户字段，跳过省时间

                SerializedObject so;
                try { so = new SerializedObject(comp); }
                catch (Exception e)
                {
                    log.Add("   [跳过] " + label + " " + FullPath(go) + " 的 "
                            + comp.GetType().Name + " 无法序列化: " + e.Message);
                    continue;
                }

                bool dirty = false;
                var prop = so.GetIterator();
                while (prop.NextVisible(true))
                {
                    if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (prop.objectReferenceValue != null) continue;
                    int id = prop.objectReferenceInstanceIDValue;
                    if (id == 0) continue;                       // 本来就是空引用，不是丢失
                    if (InternalProps.Contains(prop.name)) continue;   // Unity 内部字段，绝不能清

                    found++;
                    danglingFound++;
                    log.Add(string.Format(
                        "   [{0}] {1} / <{2}> 字段 {3} 引用了已丢失的对象 (instanceID={4})",
                        label, FullPath(go), comp.GetType().Name, prop.propertyPath, id));

                    if (clear)
                    {
                        prop.objectReferenceValue = null;
                        dirty = true;
                        danglingCleared++;
                    }
                }
                if (dirty) so.ApplyModifiedProperties();
            }
            return found;
        }

        // ============================================================ 负缩放 Collider

        private static void ReportNegativeCollider(Transform t, List<string> report)
        {
            if (t.GetComponent<Collider>() == null) return;
            var lossy = t.lossyScale;
            if (lossy.x > 0f && lossy.y > 0f && lossy.z > 0f) return;
            report.Add(string.Format("   [!] {0} lossyScale=({1}) 含负分量",
                FullPath(t.gameObject), lossy));
        }

        private static void FixNegativeColliderScale(Transform t, List<string> log)
        {
            if (t.GetComponent<Collider>() == null) return;

            var lossy = t.lossyScale;
            if (lossy.x > 0f && lossy.y > 0f && lossy.z > 0f) return;

            // 沿祖先链找出第一个引入负分量的 Transform
            var src = t;
            while (src != null)
            {
                var s = src.localScale;
                if (s.x < 0f || s.y < 0f || s.z < 0f) break;
                src = src.parent;
            }

            if (src == null)
            {
                skippedColliders++;
                log.Add(string.Format(
                    "   [跳过] {0} lossyScale=({1}) 为负，但未在祖先链找到负分量来源，需人工处理。",
                    FullPath(t.gameObject), lossy));
                return;
            }

            var srcGo = src.gameObject;
            bool hasVisual = srcGo.GetComponent<Renderer>() != null
                             || srcGo.GetComponent<MeshFilter>() != null;
            bool identityRotation = src.localRotation == Quaternion.identity;

            if (hasVisual || !identityRotation)
            {
                // 带渲染体或带旋转的节点，取反会镜像/扭转视觉，不能自动改
                skippedColliders++;
                log.Add(string.Format(
                    "   [跳过] {0} 的负缩放来自 {1} (localScale={2})，但该节点带渲染体或非单位旋转，需人工处理。",
                    FullPath(t.gameObject), FullPath(srcGo), src.localScale));
                return;
            }

            // 纯碰撞体节点：Box 关于自身中心沿轴镜像后是完全相同的盒，取反不改变碰撞体
            var fixedScale = src.localScale;
            if (fixedScale.x < 0f) fixedScale.x = -fixedScale.x;
            if (fixedScale.y < 0f) fixedScale.y = -fixedScale.y;
            if (fixedScale.z < 0f) fixedScale.z = -fixedScale.z;

            log.Add(string.Format(
                "   [修正] {0} : localScale {1} -> {2}（纯碰撞体节点，盒关于中心镜像不变）",
                FullPath(srcGo), src.localScale, fixedScale));
            src.localScale = fixedScale;
            fixedColliders++;
        }

        // ============================================================ 工具

        /// <summary>预制体是否含嵌套预制体实例（读文件文本判断，避免 SaveAs 拍平结构）。</summary>
        private static bool HasNestedPrefabInstances(string path)
        {
            try
            {
                var text = File.ReadAllText(path);
                return text.Contains("\nPrefabInstance:") || text.StartsWith("PrefabInstance:");
            }
            catch { return true; }   // 读不到就当作有，保守跳过
        }

        private static void SaveAll()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(active.path))
                EditorSceneManager.SaveScene(active);
            AssetDatabase.SaveAssets();
        }

        private static string FullPath(GameObject go)
        {
            var t = go.transform;
            string path = go.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.gameObject.name + "/" + path;
            }
            return path;
        }
    }
}
