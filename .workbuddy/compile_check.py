import os, subprocess, glob, io

UNITY = r"D:/Software/UnityHub/2022.3.62/2022.3.62f3c1/Editor/Data"
PROJ = r"C:/Study/UnityProjects/RenderCluster"
OUT = os.path.join(PROJ, ".workbuddy", "tmp")
os.makedirs(OUT, exist_ok=True)

DOTNET = os.path.join(UNITY, "NetCoreRuntime", "dotnet.exe")
CSC = os.path.join(UNITY, "DotNetSdkRoslyn", "csc.dll")

refs = []
# .NET 标准库（Unity 的 ScriptAssemblies 是针对 netstandard2.1 编译的）
for f in glob.glob(os.path.join(UNITY, "NetStandard", "ref", "2.1.0", "*.dll")):
    refs.append(f)
# UnityEngine / UnityEditor
for f in glob.glob(os.path.join(UNITY, "Managed", "UnityEngine", "*.dll")):
    refs.append(f)
# 第三方包与工程程序集（排除 Assembly-CSharp 自身，避免自引用）
for f in glob.glob(os.path.join(PROJ, "Library", "ScriptAssemblies", "*.dll")):
    if os.path.basename(f) in ("Assembly-CSharp.dll", "Assembly-CSharp-Editor.dll"):
        continue
    refs.append(f)

srcs = []
for root, dirs, files in os.walk(os.path.join(PROJ, "Assets")):
    dirs[:] = [d for d in dirs if d != "Editor"]
    for f in files:
        if f.endswith(".cs"):
            srcs.append(os.path.join(root, f))
srcs.sort()
print("源文件数：", len(srcs), " 引用数：", len(refs))

rsp = os.path.join(OUT, "build.rsp")
with io.open(rsp, "w", encoding="utf-8-sig", newline="\n") as fp:
    fp.write("-nologo\n")
    fp.write("-target:library\n")
    fp.write("-nostdlib+\n")
    fp.write("-unsafe\n")
    fp.write('-out:"%s"\n' % os.path.join(OUT, "Assembly-CSharp.dll").replace("\\", "/"))
    fp.write("-langversion:9.0\n")
    # 工程里不少类体被 #if UNITY_EDITOR 包住，不定义会让 #endif 位置错乱
    fp.write("-define:UNITY_EDITOR\n")
    fp.write("-nowarn:0169,0649,0067,0414,0162,0108,0618,0219,1701,1702,0436\n")
    for r in refs:
        fp.write('-r:"%s"\n' % r.replace("\\", "/"))
    for s in srcs:
        fp.write('"%s"\n' % s.replace("\\", "/"))

p = subprocess.run([DOTNET, CSC, "@" + rsp], capture_output=True, text=True,
                   encoding="utf-8", errors="replace")
out = (p.stdout or "") + (p.stderr or "")
lines = [l for l in out.splitlines() if l.strip()]
if not lines:
    print("✅ 编译通过：无错误无警告")
else:
    print("❌ 编译输出（前 50 行，共 %d 行）：" % len(lines))
    for l in lines[:50]:
        print("   ", l)
print("退出码:", p.returncode)
