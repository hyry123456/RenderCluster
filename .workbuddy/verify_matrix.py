# 验证：写侧 unity_MatrixVP 产出的深度值 == 采样侧 ConvertToAtlasMatrix 的比较值
#
# 两条链路：
#   写侧：unity_MatrixVP = proj * view（引擎原样，我们不做任何改写）
#         -> shader 输出 o.pos，硬件存 depth = f(clip.z / clip.w)
#   采样侧：Shadows.ConvertToAtlasMatrix(proj*view, offset, scale) 的 z 分量
#         -> 与 shadow map 里存的值比较
#
# 平台约定（来自 core/ShaderLibrary/API/*.hlsl）：
#   reversed-Z（D3D11/Metal/Vulkan）：硬件直接存 clip.z/clip.w，近=1 远=0
#   非 reversed（OpenGL/GLES）    ：硬件存 0.5*(clip.z/clip.w + 1)
#
# 要回答的唯一问题：unity_MatrixVP 原样用，写进去的深度是否就等于采样侧比较的值？
# 如果两者不等，就必须显式调 GL.GetGPUProjectionMatrix；如果相等，说明
# SetViewProjectionMatrices + 直接用 unity_MatrixVP 就是对的（与 SRP 路径同源）。


def mat_mul(a, b):
    """4x4 行主序矩阵乘法：out[r][c] = sum_k a[r][k] * b[k][c]"""
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)]
            for r in range(4)]


def make_ortho_cascade(near, far, texel_size):
    """模拟 ComputeDirectionalShadowMatricesAndCullingPrimitives 返回的正交矩阵。
    Unity 正交投影（GL 约定）：z_ndc ∈ [-1, 1]，near→-1，far→+1。
    行主序，m[row][col]。"""
    s = texel_size
    m = [[0.0] * 4 for _ in range(4)]
    m[0][0] = 1.0 / s
    m[1][1] = 1.0 / s
    m[2][2] = -2.0 / (far - near)
    m[2][3] = -(far + near) / (far - near)
    m[3][3] = 1.0
    return m


def atlas_matrix(proj, view, uses_reversed_z, offset=(0.0, 0.0), scale=1.0):
    """复刻 Shadows.ConvertToAtlasMatrix（这里 offset/scale 取 0/1，只看 z 行关系）。"""
    m = [row[:] for row in mat_mul(proj, view)]
    if uses_reversed_z:
        m[2] = [-v for v in m[2]]
    m[2] = [0.5 * (m[2][i] + m[3][i]) for i in range(4)]
    return m


def mat_vec(m, v):
    return [sum(m[r][k] * v[k] for k in range(4)) for r in range(4)]


def check(uses_reversed_z, label):
    print("=" * 72)
    print(f"平台：{label}（reversed-Z = {uses_reversed_z}）")
    print(f"{'视空间 z':>12} | {'写侧存入 depth':>18} | {'采样侧比较值':>18} |   差")

    near, far, texel = 1.0, 500.0, 60.0
    proj = make_ortho_cascade(near, far, texel)
    view = [[1.0 if i == j else 0.0 for j in range(4)] for i in range(4)]
    view[2][3] = -100.0                       # 视图沿 -z 平移

    az = atlas_matrix(proj, view, uses_reversed_z)
    vp = mat_mul(proj, view)                 # ← 我们不做任何改写

    ok = True
    for zv in [0.0, 50.0, -50.0, 100.0, -100.0, 200.0, -200.0, 399.0, -399.0]:
        p = [0.0, 0.0, zv, 1.0]

        clip = mat_vec(vp, p)
        z_ndc = clip[2] / clip[3]
        stored = z_ndc if uses_reversed_z else 0.5 * (z_ndc + 1.0)

        sts = mat_vec(az, p)
        compare = sts[2] / sts[3] if abs(sts[3]) > 1e-9 else sts[2]

        d = abs(stored - compare)
        good = d < 1e-6
        ok = ok and good
        print(f"{zv:>12} | {stored:>18.6f} | {compare:>18.6f} | {d:.2e} {'OK' if good else 'MISMATCH'}")

    print(f"\n结论：{'一致 —— unity_MatrixVP 原样可用' if ok else '不一致 —— 必须显式转 GPU 矩阵'}")
    return ok


r_rev_raw = check(True, "D3D11 / Metal / Vulkan —— 【假设】unity_MatrixVP = proj*view 原样（错误假设）")
print()
r_gl_raw = check(False, "OpenGL / GLES —— 【假设】unity_MatrixVP = proj*view 原样")

print("\n" + "=" * 72)
print("上面两项若不一致（预期如此），说明：unity_MatrixVP 不能等于 proj*view 原样。")
print()
print("但 Unity 官方论坛明确答复：")
print('  "Only CommandBuffer.SetProjectionMatrix and .SetViewProjectionMatrices')
print('   calls it automatically."')
print("  ⇒ SetViewProjectionMatrices 内部已自动调用 GL.GetGPUProjectionMatrix，")
print("    拿到的 unity_MatrixVP 已经是【GPU 约定】矩阵（含 reversed-Z 变换）。")
print()
print("而 GL.GetGPUProjectionMatrix 在正交 + reversed-Z 下做的 z 行变换，")
print("恰好与工程 ConvertToAtlasMatrix 的 z 行完全相同：")
print("    0.5 * (z行 + w行)  —— 即下面这个等价验证。")
print()


def gpu_projection_z_row(proj, view, uses_reversed_z):
    """模拟 GL.GetGPUProjectionMatrix(proj, renderIntoTexture) 在正交下的 z 行处理，
    再乘 view —— 也就是 SetViewProjectionMatrices 之后 unity_MatrixVP 的 z 行。"""
    p = [row[:] for row in proj]
    if uses_reversed_z:
        # Unity 的 reversed-Z：z_ndc 翻转并重映射到 [0,1]
        # 等价形式：z_gpu = 0.5*(w - z_cpu)
        for r in (2,):
            p[r] = [0.5 * p[3][i] - 0.5 * p[r][i] for i in range(4)]
        # w 行同步翻转，保证 w 仍为 1（正交）
        p[3] = [-v for v in p[3]]
        p[3][3] = 1.0
    return mat_mul(p, view)


def check_gpu_converted(uses_reversed_z, label):
    print("-" * 72)
    print(f"平台：{label}  —— unity_MatrixVP = GPU约定(proj)*view")
    print(f"{'视空间 z':>12} | {'写侧存入 depth':>18} | {'采样侧比较值':>18} |   差")

    near, far, texel = 1.0, 500.0, 60.0
    proj = make_ortho_cascade(near, far, texel)
    view = [[1.0 if i == j else 0.0 for j in range(4)] for i in range(4)]
    view[2][3] = -100.0

    az = atlas_matrix(proj, view, uses_reversed_z)
    vp = gpu_projection_z_row(proj, view, uses_reversed_z)

    ok = True
    for zv in [0.0, 50.0, -50.0, 100.0, -100.0, 200.0, -200.0, 399.0, -399.0]:
        p = [0.0, 0.0, zv, 1.0]
        clip = mat_vec(vp, p)
        z_ndc = clip[2] / clip[3]
        stored = z_ndc if uses_reversed_z else 0.5 * (z_ndc + 1.0)

        sts = mat_vec(az, p)
        compare = sts[2] / sts[3] if abs(sts[3]) > 1e-9 else sts[2]

        d = abs(stored - compare)
        good = d < 1e-6
        ok = ok and good
        print(f"{zv:>12} | {stored:>18.6f} | {compare:>18.6f} | {d:.2e} {'OK' if good else 'MISMATCH'}")

    print(f"\n结论：{'一致 ⇒ 直接用 unity_MatrixVP（SetViewProjectionMatrices 的输出）' if ok else '不一致'}")
    return ok


print()
a = check_gpu_converted(True, "D3D11 / Metal / Vulkan")
print()
b = check_gpu_converted(False, "OpenGL / GLES")

print("\n" + "=" * 72)
if a and b:
    print("✅ 两个平台分支都一致。")
    print("   ⇒ shader 里直接 mul(unity_MatrixVP, pos) 即可，")
    print("     绝不要自己再手推 z 行（上一轮的错误）。")
else:
    print("❌ 仍有不一致，需要进一步排查。")