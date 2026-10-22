import io, re, sys

files = [
    r"Assets\CustomRenderPipeline\Runtime\ClusterRender\ClusterCore.cs",
    r"Assets\CustomRenderPipeline\Runtime\ClusterRender\ClusterObject.cs",
    r"Assets\CustomRenderPipeline\Runtime\ClusterRender\BatchRenderManager.cs",
    r"Assets\CustomRenderPipeline\Runtime\ClusterRender\ClusterControl.cs",
    r"Assets\CustomRenderPipeline\Runtime\Defferrd Render\Core\DefferPipeline.cs",
    r"Assets\CustomRenderPipeline\Runtime\Core\Shadows.cs",
    r"Assets\CustomRenderPipeline\Runtime\RenderPiplineBase\RenderPipelineBase.cs",
    r"Assets\CustomRenderPipeline\Runtime\ClusterRender\Datas\ClusterAssembleAsset.cs",
]
root = r"C:\Study\UnityProjects\RenderCluster\\"

def strip(src):
    out = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if c == '/' and i + 1 < n and src[i+1] == '/':
            while i < n and src[i] != '\n':
                i += 1
        elif c == '/' and i + 1 < n and src[i+1] == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i+1] == '/'):
                i += 1
            i += 2
        elif c == '"':
            # 普通字符串（含 $"" 与 @""），跳过整个字面量
            verbatim = i > 0 and src[i-1] == '@'
            i += 1
            while i < n:
                if src[i] == '\\' and not verbatim:
                    i += 2
                    continue
                if src[i] == '"':
                    if verbatim and i + 1 < n and src[i+1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
        elif c == "'":
            i += 1
            while i < n:
                if src[i] == '\\':
                    i += 2
                    continue
                if src[i] == "'":
                    i += 1
                    break
                i += 1
        else:
            out.append(c)
            i += 1
    return "".join(out)

bad = 0
for f in files:
    with io.open(root + f, "r", encoding="utf-8-sig") as fh:
        src = fh.read()
    s = strip(src)
    o, c = s.count("{"), s.count("}")
    tag = "OK " if o == c else "BAD"
    if o != c:
        bad += 1
    print("%s {%d} }{%d}  %s" % (tag, o, c, f))
sys.exit(1 if bad else 0)
