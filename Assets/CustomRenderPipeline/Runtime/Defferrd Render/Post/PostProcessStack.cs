using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline.DefferPipeline
{
    /// <summary>
    /// 后处理渲染器（对应 URP 的 PostProcessPass，但只做我们需要的部分）。
    ///
    /// 分工：
    ///   · 参数全部来自 Volume 框架（PostVolumeSettings 里的那些 VolumeComponent）；
    ///   · 本类只管「拿参数 → 排 pass → 画」。
    ///
    /// 每帧的 pass 结构（和 URP 同一个思路：能合的全塞进一次全屏）：
    ///   Bloom 预过滤 → 逐级降采样 → 逐级升采样（加法混合）
    ///   → Uber（畸变/色差/合成/曝光/tonemap/调色/暗角/颗粒）
    ///   → FXAA（可选）
    /// </summary>
    public class PostProcessStack
    {
        const int MaxBloomMips = 6;

        static readonly int SourceTexId = Shader.PropertyToID("_SourceTex");
        static readonly int BloomTexId = Shader.PropertyToID("_BloomTex");
        static readonly int SourceTexelId = Shader.PropertyToID("_SourceTex_TexelSize");
        static readonly int BloomTexelId = Shader.PropertyToID("_BloomTex_TexelSize");

        static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
        static readonly int SoftKneeId = Shader.PropertyToID("_SoftKnee");
        static readonly int BloomClampId = Shader.PropertyToID("_Clamp");
        static readonly int BloomTintId = Shader.PropertyToID("_BloomTint");
        static readonly int BloomScatterId = Shader.PropertyToID("_BloomScatter");

        static readonly int BloomIntensityId = Shader.PropertyToID("_BloomIntensity");
        static readonly int ExposureId = Shader.PropertyToID("_Exposure");
        static readonly int TonemapModeId = Shader.PropertyToID("_TonemapMode");
        static readonly int ContrastId = Shader.PropertyToID("_Contrast");
        static readonly int SaturationId = Shader.PropertyToID("_Saturation");
        static readonly int ColorFilterId = Shader.PropertyToID("_ColorFilter");
        static readonly int VignetteColorId = Shader.PropertyToID("_VignetteColor");
        static readonly int VignetteCenterId = Shader.PropertyToID("_VignetteCenter");
        static readonly int VignetteParamsId = Shader.PropertyToID("_VignetteParams");
        static readonly int FilmGrainParamsId = Shader.PropertyToID("_FilmGrainParams");
        static readonly int ChromaticAberrationId = Shader.PropertyToID("_ChromaticAberration");
        static readonly int LensDistortionParamsId = Shader.PropertyToID("_LensDistortionParams");

        // Bloom 各级 mip 的临时 RT（固定名，靠 GetTemporaryRT 分配/复用）
        static readonly int[] MipIds =
        {
            Shader.PropertyToID("_PostBloomMip0"),
            Shader.PropertyToID("_PostBloomMip1"),
            Shader.PropertyToID("_PostBloomMip2"),
            Shader.PropertyToID("_PostBloomMip3"),
            Shader.PropertyToID("_PostBloomMip4"),
            Shader.PropertyToID("_PostBloomMip5"),
        };
        static readonly int LdrId = Shader.PropertyToID("_PostLDR");
        static readonly int DummyId = Shader.PropertyToID("_PostDummy");

        readonly int[] m_mipW = new int[MaxBloomMips];
        readonly int[] m_mipH = new int[MaxBloomMips];

        Material m_bloom, m_uber, m_fxaa;
        bool m_triedCreate;
        bool m_blocked;
        bool m_warnedNoSettings;
        int m_allocatedMips;
        bool m_dummyAllocated;
        float m_grainTime;

        // ------------------------------------------------------------------
        // 资源
        // ------------------------------------------------------------------
        bool EnsureMaterials()
        {
            if (m_blocked) return false;
            if (m_triedCreate) return m_bloom != null && m_uber != null && m_fxaa != null;
            m_triedCreate = true;

            Shader bloomShader = Shader.Find("Hidden/CustomRP/PostBloom");
            Shader uberShader = Shader.Find("Hidden/CustomRP/PostUber");
            Shader fxaaShader = Shader.Find("Hidden/CustomRP/PostFXAA");

            if (bloomShader == null || uberShader == null || fxaaShader == null)
            {
                // 永久禁用，避免每帧刷屏。shader 放在 Resources/Post 下，Shader.Find 打包后也找得到；
                // 找不到通常是 .shader 编译失败，或者 Shader "..." 名字被改了。
                m_blocked = true;
                Debug.LogError(
                    "[PostProcess] 后处理 shader 找不到，已禁用后处理。检查 " +
                    "Assets/CustomRenderPipeline/Resources/Post/ 下三个 .shader 是否有编译错误、名字是否被改。" +
                    $"(bloom={bloomShader != null}, uber={uberShader != null}, fxaa={fxaaShader != null})");
                return false;
            }

            m_bloom = new Material(bloomShader) { name = "CustomRP.PostBloom", hideFlags = HideFlags.HideAndDontSave };
            m_uber = new Material(uberShader) { name = "CustomRP.PostUber", hideFlags = HideFlags.HideAndDontSave };
            m_fxaa = new Material(fxaaShader) { name = "CustomRP.PostFXAA", hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        /// <summary>取 Volume 栈并按 trigger 位置刷新。Trigger 传摄像机 transform，
        /// 于是 Local Volume 的 blendDistance 会随摄像机位置渐变（和 URP 一致）。</summary>
        static VolumeStack GetStack(Camera camera)
        {
            VolumeManager manager = VolumeManager.instance;
            if (manager == null) return null;
            VolumeStack stack = manager.stack;
            if (stack == null) return null;
            manager.Update(stack, camera.transform, ~0);
            return stack;
        }

        // ------------------------------------------------------------------
        // 主入口
        // ------------------------------------------------------------------

        /// <summary>
        /// 把 source 做完整条后处理链，写到 target。
        /// 返回 false 表示「没做，调用方走原来的直通路径」。
        ///
        /// 注意：即使所有效果都关，只要开关打开也会走一次 uber —— 因为 HDR 缓冲不做 tonemap
        /// 就是硬切，而 tonemap 是这条链的地基。
        /// </summary>
        public bool Render(
            CommandBuffer buffer, Camera camera,
            RenderTargetIdentifier source, RenderTargetIdentifier target,
            int width, int height, PostProcessSettings settings)
        {
            if (width <= 0 || height <= 0) return false;
            if (settings == null) return false;
            if (!EnsureMaterials()) return false;

            VolumeStack stack = GetStack(camera);
            if (stack == null) return false;

            var tonemap = stack.GetComponent<TonemappingSettings>();
            var bloom = stack.GetComponent<BloomSettings>();
            var grade = stack.GetComponent<ColorAdjustmentsSettings>();
            var vignette = stack.GetComponent<VignetteSettings>();
            var grain = stack.GetComponent<FilmGrainSettings>();
            var ca = stack.GetComponent<ChromaticAberrationSettings>();
            var lens = stack.GetComponent<LensDistortionSettings>();

            // ---------- 1. Bloom ----------
            m_allocatedMips = 0;
            m_dummyAllocated = false;
            bool bloomOn = bloom != null && bloom.active && bloom.intensity.value > 0f;
            RenderTargetIdentifier bloomTex = source;
            if (bloomOn)
            {
                bloomTex = RenderBloom(buffer, source, width, height, bloom);
            }
            else
            {
                // 关掉时绑一张 1x1 黑图：uber 里那次采样照跑但不影响画面（比多一套 shader 变体划算）
                buffer.GetTemporaryRT(DummyId, 1, 1, 0, FilterMode.Point, RenderTextureFormat.Default);
                buffer.SetRenderTarget(DummyId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
                buffer.ClearRenderTarget(false, true, Color.black);
                bloomTex = DummyId;
                m_dummyAllocated = true;
            }

            // ---------- 2. Uber ----------
            // 强度归一化：bloom 是「mip0 自身 + 每级粗 mip × scatter^k」累加出来的，
            // 直接拿 intensity 乘会随级数线性变亮（5 级时能到 3 倍）。
            // 除以理论权重和 ⇒ intensity=1 时总亮度≈一倍亮部，和 URP 的手感接近。
            float bloomScale = 0f;
            if (bloomOn)
            {
                float s = Mathf.Clamp(bloom.scatter.value, 0.01f, 0.99f);
                float sum = 1f, w = 1f;
                for (int i = 1; i < m_allocatedMips; i++) { w *= s; sum += w; }
                bloomScale = bloom.intensity.value / Mathf.Max(sum, 1e-4f);
            }
            m_uber.SetFloat(BloomIntensityId, bloomScale);

            // tonemap 的默认值：Volume 里【没有】这个组件时按 Neutral 输出。
            // 理由：HDR 缓冲不做映射就是硬切（超 1 的部分全白），这是错的；
            // 而"加了后处理但画面一点没变"会让人以为后处理没生效。
            // 用户想要原来的硬切观感，就在 Volume 里加 Tonemapping 并把 mode 设成 None。
            float exposure = 1f;
            float tonemapMode = (float)(int)PostTonemappingMode.Neutral;
            if (tonemap != null)
            {
                if (tonemap.active)
                {
                    exposure = Mathf.Pow(2f, tonemap.postExposure.value);
                    tonemapMode = (float)(int)tonemap.mode.value;
                }
                else
                {
                    tonemapMode = 0f;       // 组件存在但被关掉 ⇒ 明确不映射
                }
            }
            else if (!m_warnedNoSettings)
            {
                // 只在「一个 CustomRP 效果都没有」时提示，避免用户只加了 Bloom 也被念一遍
                bool anySettings = bloom != null || grade != null || vignette != null
                    || grain != null || ca != null || lens != null;
                if (!anySettings)
                {
                    m_warnedNoSettings = true;
                    Debug.Log(
                        "[PostProcess] Volume 里还没有任何 CustomRP 后处理设置，已按默认 Neutral tonemap 输出。" +
                        "想调效果：Hierarchy 加空物体 → Add Component → Volume → New Profile → " +
                        "Add Override → CustomRP → 挑 Bloom / Color Adjustments / Vignette ...");
                }
            }

            m_uber.SetFloat(ExposureId, exposure);
            m_uber.SetFloat(TonemapModeId, tonemapMode);

            bool gradeOn = grade != null && grade.active;
            m_uber.SetFloat(ContrastId, gradeOn ? 1f + grade.contrast.value / 100f : 1f);
            m_uber.SetFloat(SaturationId, gradeOn ? 1f + grade.saturation.value / 100f : 1f);
            Color filter = gradeOn ? grade.colorFilter.value : Color.white;
            filter.a = 1f;          // 染色是乘法，alpha 不参与
            m_uber.SetColor(ColorFilterId, filter);

            bool vignetteOn = vignette != null && vignette.active && vignette.intensity.value > 0f;
            if (vignetteOn)
            {
                m_uber.SetColor(VignetteColorId, vignette.color.value);
                Vector2 center = vignette.center.value;
                m_uber.SetVector(VignetteCenterId, new Vector4(center.x, center.y, 0f, 0f));
                m_uber.SetVector(VignetteParamsId, new Vector4(
                    vignette.intensity.value,
                    Mathf.Max(vignette.smoothness.value, 0.01f),   // smoothstep 的 edge0/edge1 不能相等
                    vignette.roundness.value,
                    width / (float)height));
            }
            else
            {
                m_uber.SetColor(VignetteColorId, Color.black);
                m_uber.SetVector(VignetteParamsId, new Vector4(0f, 0.01f, 1f, width / (float)height));
            }

            float grainIntensity = grain != null && grain.active ? grain.intensity.value : 0f;
            if (grainIntensity > 0f)
            {
                // 每帧递增的种子：颗粒必须"跳"，静止就变成一层脏贴图了
                m_grainTime += Time.unscaledDeltaTime * 60f;
                if (m_grainTime > 1000f) m_grainTime -= 1000f;
            }
            m_uber.SetVector(FilmGrainParamsId, new Vector4(
                grainIntensity,
                grain != null ? grain.response.value : 0f,
                m_grainTime, 0f));

            m_uber.SetFloat(ChromaticAberrationId,
                ca != null && ca.active ? ca.intensity.value : 0f);

            bool lensOn = lens != null && lens.active && Mathf.Abs(lens.intensity.value) > 1e-4f;
            m_uber.SetVector(LensDistortionParamsId, lensOn
                ? new Vector4(lens.intensity.value, Mathf.Max(lens.scale.value, 1e-4f), 0f, 0f)
                : Vector4.zero);

            bool useFxaa = settings.antiAliasing != PostAntiAliasing.None;
            // 中间 RT 的 viewport = 内部分辨率（含 renderScale）；
            // 最后一笔写屏幕必须用 camera.pixelRect —— renderScale < 1 时要铺满整个屏幕（顺便放大），
            // 用 (0,0,width,height) 会只画左下角一块。
            Rect internalViewport = new Rect(0f, 0f, width, height);
            Rect screenViewport = camera.pixelRect;

            if (useFxaa)
            {
                // Uber 先落到 LDR 图，再让 FXAA 处理 —— FXAA 必须在 tonemap 之后的显示值上做
                buffer.GetTemporaryRT(LdrId, width, height, 0, FilterMode.Bilinear, RenderTextureFormat.Default);
                Draw(buffer, m_uber, 0, LdrId, source, bloomTex, width, height,
                    internalViewport, RenderBufferLoadAction.DontCare);
                Draw(buffer, m_fxaa, 0, target, LdrId, bloomTex, width, height,
                    screenViewport, RenderBufferLoadAction.DontCare);
                buffer.ReleaseTemporaryRT(LdrId);
            }
            else
            {
                Draw(buffer, m_uber, 0, target, source, bloomTex, width, height,
                    screenViewport, RenderBufferLoadAction.DontCare);
            }

            if (m_dummyAllocated) buffer.ReleaseTemporaryRT(DummyId);
            for (int i = 0; i < m_allocatedMips; i++) buffer.ReleaseTemporaryRT(MipIds[i]);
            m_dummyAllocated = false;
            m_allocatedMips = 0;
            return true;
        }

        // ------------------------------------------------------------------
        // Bloom
        // ------------------------------------------------------------------
        RenderTargetIdentifier RenderBloom(
            CommandBuffer buffer, RenderTargetIdentifier source, int width, int height, BloomSettings bloom)
        {
            int wanted = Mathf.Clamp(bloom.mipCount.value, 1, MaxBloomMips);

            // 逐级减半，短边小于 4 就停 —— 再小就采到隔壁像素上（模糊会变成"方块"）
            int mipCount = 0;
            int mw = width, mh = height;
            for (int i = 0; i < wanted; i++)
            {
                int nw = Mathf.Max(mw / 2, 1);
                int nh = Mathf.Max(mh / 2, 1);
                if (nw < 4 || nh < 4) break;
                buffer.GetTemporaryRT(MipIds[mipCount], nw, nh, 0,
                    FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
                m_mipW[mipCount] = nw;
                m_mipH[mipCount] = nh;
                mipCount++;
                mw = nw;
                mh = nh;
            }
            m_allocatedMips = mipCount;
            if (mipCount == 0) return source;

            m_bloom.SetFloat(ThresholdId, bloom.threshold.value);
            // ⚠️ softKnee 会做分母，0 会算出 NaN ⇒ 给下限
            m_bloom.SetFloat(SoftKneeId, Mathf.Max(bloom.softKnee.value, 0.0001f));
            m_bloom.SetFloat(BloomClampId, 65472f);      // 半精度上限附近，掐 firefly
            Color tint = bloom.tint.value;
            tint.a = 1f;
            m_bloom.SetColor(BloomTintId, tint);

            Rect vp0 = new Rect(0f, 0f, m_mipW[0], m_mipH[0]);

            // 1) 预过滤：全分辨率 → mip0
            //    Draw 里会用【源】尺寸（width/height）填 _SourceTex_TexelSize，
            //    shader 里再取 0.5 个 texel 做 4 tap —— 正好对应源上的 2×2 像素。
            Draw(buffer, m_bloom, 0, MipIds[0], source, source, width, height,
                vp0, RenderBufferLoadAction.DontCare);

            // 2) 降采样：mip[i-1] → mip[i]
            for (int i = 1; i < mipCount; i++)
            {
                buffer.SetGlobalVector(BloomTexelId, new Vector4(
                    1f / m_mipW[i - 1], 1f / m_mipH[i - 1], m_mipW[i - 1], m_mipH[i - 1]));
                Draw(buffer, m_bloom, 1, MipIds[i], source, MipIds[i - 1],
                    m_mipW[i - 1], m_mipH[i - 1],
                    new Rect(0f, 0f, m_mipW[i], m_mipH[i]), RenderBufferLoadAction.DontCare);
            }

            // 3) 升采样（加法混合）：mip[i+1] 叠进 mip[i]。
            //    ⚠️ 这里必须 Load —— 目标里已经有本级的降采样结果，DontCare 会把它丢掉。
            m_bloom.SetFloat(BloomScatterId, Mathf.Clamp(bloom.scatter.value, 0.01f, 1f));
            for (int i = mipCount - 2; i >= 0; i--)
            {
                buffer.SetGlobalVector(BloomTexelId, new Vector4(
                    1f / m_mipW[i + 1], 1f / m_mipH[i + 1], m_mipW[i + 1], m_mipH[i + 1]));
                Draw(buffer, m_bloom, 2, MipIds[i], source, MipIds[i + 1],
                    m_mipW[i + 1], m_mipH[i + 1],
                    new Rect(0f, 0f, m_mipW[i], m_mipH[i]), RenderBufferLoadAction.Load);
            }

            return MipIds[0];
        }

        // ------------------------------------------------------------------
        // 画全屏
        // ------------------------------------------------------------------
        /// <summary>
        /// 全屏三角。顶点由 shader 自己用 SV_VertexID 生成（3 个顶点，不用 quad）。
        /// 刻意【不用 CommandBuffer.Blit】：Blit 走 ImageFilters::Blit，源纹理不可直接采样时
        /// 会插一次 Hidden/Internal-Loading（keyword 空间不兼容 ⇒ 项目里踩过的断言）。
        /// 自己 SetRenderTarget + DrawProcedural 就完全绕开它。
        ///
        /// srcW/srcH 必须是【源纹理】的尺寸：shader 里 `_SourceTex_TexelSize` 是拿来做
        /// 采样偏移的（比如 Bloom 预过滤的 4 个 tap），传成目标尺寸就会偏一倍。
        /// </summary>
        static void Draw(
            CommandBuffer buffer, Material mat, int pass,
            RenderTargetIdentifier dest, RenderTargetIdentifier srcTex, RenderTargetIdentifier bloomTex,
            int srcW, int srcH, Rect viewport, RenderBufferLoadAction load)
        {
            buffer.SetGlobalTexture(SourceTexId, srcTex);
            buffer.SetGlobalVector(SourceTexelId, new Vector4(1f / srcW, 1f / srcH, srcW, srcH));
            buffer.SetGlobalTexture(BloomTexId, bloomTex);

            // 最后一笔用 Store：以前这里写 DontCare，若摄像机渲染到 targetTexture（而不是后缓冲），
            // DontCare 意味着"内容未定义" ⇒ 画面可能是黑的。Store 才稳。
            buffer.SetRenderTarget(dest, load, RenderBufferStoreAction.Store);
            buffer.SetViewport(viewport);
            buffer.DrawProcedural(Matrix4x4.identity, mat, pass, MeshTopology.Triangles, 3);
        }

        public void Dispose()
        {
            Release(ref m_bloom);
            Release(ref m_uber);
            Release(ref m_fxaa);
            m_triedCreate = false;
            m_blocked = false;
        }

        static void Release(ref Material mat)
        {
            if (mat == null) return;
            if (Application.isPlaying) Object.Destroy(mat);
            else Object.DestroyImmediate(mat);
            mat = null;
        }
    }
}
