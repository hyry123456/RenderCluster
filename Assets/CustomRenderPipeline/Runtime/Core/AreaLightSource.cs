using UnityEngine;

namespace CustomRenderPipline
{
    /// <summary>
    /// 把一盏 **Spot 光**升级成「矩形面光源」（MRP 代表点近似）。
    ///
    /// 为什么不用 Unity 自带的 LightType.Rectangle：
    ///   Unity 的 Light Inspector 里 Rectangle 显示为 "Area (baked only)"，
    ///   Mode 会被强制成 Baked（docs: "It affects only baked lightmaps and lightprobes"），
    ///   自建管线需要的实时数据在 visibleLights 里拿不到。
    ///
    /// 为什么挂 **Spot** 而不是 Point：
    ///   · 面光源只往发光面正前方照，本质是半空间的 ⇒ Spot 的 1 个 perspective 阴影面正好覆盖，
    ///     而点光要画 6 个 cube 面（其中 3 面永远照不到，纯浪费 3 倍阴影绘制 + 3 个 tile 预算）；
    ///   · 光照本身仍走面光源 MRP（半球、**不受 spotAngle 限制**），锥角只决定阴影覆盖范围。
    ///
    /// 用法：Light 类型设 Spot → 挂本组件 → 调 Width / Height → 调 Range（= 照亮半径，
    ///       同时就是阴影的 far 面）→ 阴影锥由 shadowConeAngle 自动算，默认 90° 正好贴合半球。
    /// 不挂本组件时，Spot 光的行为完全不变。
    /// </summary>
    [RequireComponent(typeof(Light))]
    [DisallowMultipleComponent]
    public class AreaLightSource : MonoBehaviour
    {
        [Header("面片尺寸（米，与世界缩放无关）")]
        [Tooltip("矩形宽度。")]
        [Min(0.001f)] public float width = 2f;

        [Tooltip("矩形高度。")]
        [Min(0.001f)] public float height = 1f;

        [Header("阴影锥（把 Spot 阴影的 near 面对齐到面片）")]
        [Tooltip("开启后自动改本灯的两个属性：\n" +
                 "  spotAngle      = shadowConeAngle\n" +
                 "  shadowNearPlane = 半对角线 / tan(shadowConeAngle/2)\n" +
                 "第二个式子的含义就是「让阴影锥在 near 平面处的截面 ≈ 发光面本身」，\n" +
                 "于是阴影不再从中心一点往外扇，而是从整个面往外延（far 平面仍是 light.range，Unity 本来就如此）。\n" +
                 "面光源的着色不看 spotAngle（恒为半球），所以这两个值只影响阴影。\n" +
                 "关掉则完全由你手动控制 Spot Angle / Shadow Near Plane。")]
        public bool autoMatchShadowCone = true;

        [Tooltip("阴影锥的张角。**90° 是最优值**，不是随便挑的：\n" +
                 "  · 面光源只照半径 light.range 的半球；\n" +
                 "  · 透视锥在 z=range 处的半径 = range·tan(θ/2)；\n" +
                 "  ⇒ θ>90° 时远平面比半球还大，多出来那一圈 texel 完全浪费（影子变糊）；\n" +
                 "  ⇒ θ<90° 时半球外侧那圈「有光」的地方没有影子（锥外有光无影）。\n" +
                 "θ=90° 时远平面半径正好 = range = 半球半径，near 面正好 = 面片大小，一点不浪费。\n" +
                 "只有面片特别大、你更在乎近处精度时，才值得往上调。")]
        [Range(20f, 170f)] public float shadowConeAngle = 90f;

        [Header("Gizmos")]
        [Tooltip("选中本物体时，不画我们自己的蓝色阴影视锥。\n" +
                 "原因：Unity 的 Light 组件在【选中时】会自己画一个聚光锥，用的就是我们设置的 spotAngle，\n" +
                 "和这里的蓝锥几乎完全重合 —— 两套线叠在一起很难看。\n" +
                 "选中时让位给 Unity（它从灯的顶点画起），未选中时（Unity 什么都不画）才画我们的淡锥补体积感。\n" +
                 "想看「near 面是否贴到面片」时把这里关掉，两个锥就能对比着看。")]
        public bool hideOwnConeWhenSelected = true;

        [Header("说明")]
        [Tooltip("发光方向 = +forward（和 Spot 锥同向）：旋转本物体即可改变发光面朝向。\n" +
                 "尺寸是世界空间的米数，不受 GameObject 缩放影响（与 shader 侧一致）。\n" +
                 "光照是半球，不受 Spot Angle 限制；Spot Angle 只决定【阴影覆盖范围】。\n" +
                 "挂在 Point 上也能用，但不投影。")]
        [TextArea(3, 5)] public string note =
            "发光方向 = +forward；尺寸与世界缩放无关。\n" +
            "光照 = 半球（不受 Spot Angle 限制）；Spot Angle 只决定阴影覆盖范围。\n" +
            "挂在 Point 上也能用，但不投影。";

        /// <summary>半宽（米），保证非 0 以免 shader 里除以 0。</summary>
        public float HalfWidth => Mathf.Max(width, 0.001f) * 0.5f;

        /// <summary>半高（米）。</summary>
        public float HalfHeight => Mathf.Max(height, 0.001f) * 0.5f;

        /// <summary>包围球半径（半对角线），tile 剔除拿它兜底。</summary>
        public float BoundingRadius
        {
            get
            {
                float w = HalfWidth, h = HalfHeight;
                return Mathf.Sqrt(w * w + h * h);
            }
        }

        Light m_light;
        bool m_warnedType;
        bool m_warnedPointShadow;
        bool m_warnedConeClamp;

        Light SourceLight
        {
            get
            {
                if (m_light == null) m_light = GetComponent<Light>();
                return m_light;
            }
        }

        void OnEnable()
        {
#if UNITY_EDITOR
            RequestSceneRepaint();
#endif
            Light l = SourceLight;
            if (l == null) return;

            ApplyShadowCone();

            // 挂 Point 上不会投影：管线在 SetupPointLight 的面光分支直接 return，
            // 不调 ReserveOtherShadows ⇒ 既不占 6 个 cube tile，也不画任何阴影。
            if (l.type == LightType.Point && l.shadows != LightShadows.None && !m_warnedPointShadow)
            {
                m_warnedPointShadow = true;
                Debug.LogWarning(
                    $"[AreaLightSource] \"{name}\" 挂在 Point 上：面光源在 Point 上不投影，" +
                    $"Light.shadows = {l.shadows} 会被忽略（不会画 6 个 cube 面）。" +
                    "改挂 Spot 即可获得 1 个 tile 的阴影，成本只有点光的 1/6。", this);
            }
        }

        /// <summary>
        /// 把 Spot 的阴影视锥对齐到发光面。
        ///
        /// Unity 的 Spot 阴影视锥是【透视】，由三个量决定：
        ///   near = light.shadowNearPlane
        ///   far  = light.range                 ← 「far = 灯的射程」本来就是这样
        ///   半张角 = spotAngle/2
        /// 它在离灯 d 处的截面半径是 d·tan(spotAngle/2)。
        ///
        /// 想让「near 平面」正好等于发光面，就要求：
        ///   shadowNearPlane · tan(spotAngle/2) = 半对角线
        ///   ⇒ shadowNearPlane = 半对角线 / tan(spotAngle/2)
        ///
        /// 效果：near 之前那段「从中心一点往外扇」的锥被剪掉了，阴影从整个面开始往外延；
        /// 深度精度也更好（near/far 比值变小）。
        ///
        /// ⚠️ 代价：near 之内的物体不投影。灯贴着的天花板/墙面正好落在这里 —— 通常反而是想要的
        ///（避免自遮挡 acne），但如果灯离其他物体很近，会看到它们不投影。
        /// </summary>
        void ApplyShadowCone()
        {
            Light l = SourceLight;
            if (l == null) return;
            if (!autoMatchShadowCone) return;
            // 透视视锥的 near 面必须是有限的 ⇒ 有中心点。只有 Spot 能这么玩。
            if (l.type != LightType.Spot) return;

            float angle = Mathf.Clamp(shadowConeAngle, 5f, 175f);
            float halfDiag = Mathf.Max(BoundingRadius, 0.001f);
            float near = halfDiag / Mathf.Tan(angle * 0.5f * Mathf.Deg2Rad);

            // near 必须有限且远小于 far(=light.range)，否则视锥退化。
            // 窄锥要「在 near 处达到面片大小」就必须把 near 推得很远 —— 这是透视锥的固有约束，
            // 此时阴影只剩 range-near 这一段可用，会明显变短。
            float nearCap = Mathf.Max(0.01f, l.range * 0.9f);
            if (near > nearCap)
            {
                if (!m_warnedConeClamp)
                {
                    m_warnedConeClamp = true;
                    Debug.LogWarning(
                        $"[AreaLightSource] \"{name}\"：面片半对角线 {halfDiag:F2} 相对 Range {l.range:F2} 太大，" +
                        $"阴影锥的 near 被夹到 {nearCap:F2}（原需 {near:F2}）。" +
                        "阴影会明显变短/失真。把 Range 调大、把面片调小，或关掉 autoMatchShadowCone。", this);
                }
                near = nearCap;
            }

            l.spotAngle = angle;
            l.shadowNearPlane = near;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 主动刷新 Scene 视图。
        /// 管线的 SetupLights 是**每帧**读组件的（已不做缓存），但编辑器不会因为
        /// 「GameObject 上多了一个它不认识的组件」就自动重绘 Scene 视图 ——
        /// 表现为「挂上组件没反应，要拖一下相机才生效」。这里把这一步补上。
        /// </summary>
        static void RequestSceneRepaint()
        {
            UnityEditor.SceneView.RepaintAll();
        }
#endif

        void OnValidate()
        {
            Light l = SourceLight;
            if (l == null) return;

            // 改 Width / Height / shadowConeAngle 时立刻重算阴影锥，然后才重绘
            //（添加组件 / 改 Light 属性同样会走到这里 ⇒ 立刻重绘，不用拖相机才生效）
            ApplyShadowCone();
#if UNITY_EDITOR
            RequestSceneRepaint();
#endif

            if (l.type == LightType.Spot) return;      // 推荐配置

            if (l.type == LightType.Point)
            {
                if (!m_warnedPointShadow)
                {
                    m_warnedPointShadow = true;
                    Debug.Log(
                        $"[AreaLightSource] \"{name}\" 使用 Point：光照正常，但【不投影】。" +
                        "想要阴影请把 Light 类型改成 Spot（1 个 tile，成本是点光的 1/6）。", this);
                }
            }
            else if (!m_warnedType)
            {
                m_warnedType = true;
                Debug.LogWarning(
                    $"[AreaLightSource] \"{name}\" 的 Light 类型是 {l.type}，" +
                    "面光源只支持 Spot / Point（其余类型会忽略本组件）。", this);
            }
        }

#if UNITY_EDITOR
        // ---------------- Gizmos ----------------
        // 这里画的是【两个不同的体积】，也是这套面光源最容易搞混的地方：
        //
        //   ① 照亮体积 = 半球（朝 +Z、半径 light.range）
        //        照明模型真正的支撑集：sideAttenuation 在发光面背后恒为 0，range 之外恒为 0。
        //        ClusterLight 的 tile 剔除用的就是它（球 ∩ 前向半空间 = 半球，精确、不漏灯）。
        //   ② 阴影视锥 = 透视锥（near 面 ≈ 面片，far 面 = light.range）
        //        只有阴影 caster 剔除用它。它是①的子集 —— 锥外那圈就是「照亮但没影子」。
        //
        // 两个画在一起才能一眼看出 shadowConeAngle 该不该调：
        // 锥把半球包完了 = 没有无影区（此时 θ≈90°）；锥明显比半球细 = 边上那圈没影子。
        //
        // 矩阵用 TRS(pos, rot, scale=1)：尺寸是世界米数，和 shader 完全一致
        //（shader 侧用归一化的 t.right/t.up + 本组件的 width/height，同样不吃缩放）。

        static readonly Color kEdge = new Color(1f, 0.82f, 0.35f, 1f);
        static readonly Color kFill = new Color(1f, 0.82f, 0.35f, 0.16f);
        static readonly Color kEmitDir = new Color(0.35f, 0.85f, 1f, 1f);
        static readonly Color kLitVol = new Color(0.40f, 1f, 0.55f, 0.30f);
        static readonly Color kShadowCone = new Color(0.35f, 0.85f, 1f, 0.32f);
        static readonly Color kShadowConeDim = new Color(0.35f, 0.85f, 1f, 0.12f);

        // 【关键】Unity 在选中物体时，OnDrawGizmos 和 OnDrawGizmosSelected 都会调用。
        // 如果不在这里拦一下，「未选中也要画」的部分（面片轮廓、淡锥）会被画两遍，
        // 线条叠在一起会显得又粗又糊 —— 这也是「两个都在看着难受」的一部分。
        void OnDrawGizmos()
        {
            if (IsSelectedInEditor()) return;      // 选中时交给 OnDrawGizmosSelected
            DrawAreaGizmo(false);
        }

        void OnDrawGizmosSelected()
        {
            DrawAreaGizmo(true);
        }

        bool IsSelectedInEditor()
        {
            return UnityEditor.Selection.Contains(gameObject);
        }

        void DrawAreaGizmo(bool selected)
        {
            float w = Mathf.Max(width, 0.001f);
            float h = Mathf.Max(height, 0.001f);
            Light l = SourceLight;
            float range = l != null ? l.range : 0f;

            Matrix4x4 saved = Gizmos.matrix;
            // 局部空间：面片躺在 XY 平面，发光方向 = +Z（= transform.forward，与 Unity Spot 锥同向）
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

            // 面片轮廓（未选中也画：一眼看出场景里哪些是面光源、朝哪、多大）
            Gizmos.color = kEdge;
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(w, h, 0f));

            // 阴影视锥：未选中时画淡的（Unity 此时什么都不画，靠它补「体积感」）；
            // 选中时 Unity 会画自己的聚光锥（同 spotAngle、同轴），默认让位，避免两套线重叠。
            if (l != null && l.type == LightType.Spot && range > 0f)
            {
                if (!(selected && hideOwnConeWhenSelected))
                {
                    Gizmos.color = selected ? kShadowCone : kShadowConeDim;
                    DrawShadowFrustum(l, range);
                }
            }

            if (selected)
            {
                // 发光面本体
                Gizmos.color = kFill;
                Gizmos.DrawCube(Vector3.zero, new Vector3(w, h, 0f));

                // 发光方向 = 局部 +Z（= transform.forward，和 Unity 的 Spot 锥同向）
                float len = Mathf.Max(Mathf.Min(w, h) * 1.2f, 0.4f);
                Vector3 tip = new Vector3(0f, 0f, len);
                Gizmos.color = kEmitDir;
                Gizmos.DrawLine(Vector3.zero, tip);
                float a = len * 0.2f;
                Gizmos.DrawLine(tip, new Vector3(a, 0f, len - a));
                Gizmos.DrawLine(tip, new Vector3(-a, 0f, len - a));
                Gizmos.DrawLine(tip, new Vector3(0f, a, len - a));
                Gizmos.DrawLine(tip, new Vector3(0f, -a, len - a));

                // ① 照亮体积（半球）= tile 剔除用的那个体积
                if (range > 0f)
                {
                    Gizmos.color = kLitVol;
                    DrawHemisphere(range);
                }
            }

            Gizmos.matrix = saved;
        }

        /// <summary>局部空间里画一个半径为 radius、位于 z 平面的圆。</summary>
        static void DrawRing(float radius, float z, int seg)
        {
            float step = Mathf.PI * 2f / seg;
            Vector3 prev = new Vector3(radius, 0f, z);
            for (int i = 1; i <= seg; i++)
            {
                float ang = step * i;
                Vector3 p = new Vector3(Mathf.Cos(ang) * radius, Mathf.Sin(ang) * radius, z);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }

        /// <summary>
        /// ① 照亮体积：半径 range 的半球（rim 在发光面所在平面，顶点在 +Z 远处）。
        /// 这就是 ClusterLight.compute 里「球测试 ∩ 前向半空间」那次剔除的几何形状。
        /// </summary>
        static void DrawHemisphere(float radius)
        {
            const int seg = 32, meridians = 12, latitudes = 3;

            DrawRing(radius, 0f, seg);                       // 赤道 = 发光面平面上的圆

            for (int m = 0; m < meridians; m++)              // 经线：赤道 → 顶点
            {
                float phi = Mathf.PI * 2f * m / meridians;
                Vector3 dir = new Vector3(Mathf.Cos(phi), Mathf.Sin(phi), 0f);
                Vector3 prev = dir * radius;
                const int steps = 8;
                for (int i = 1; i <= steps; i++)
                {
                    float t = (i / (float)steps) * Mathf.PI * 0.5f;
                    Vector3 p = dir * (Mathf.Cos(t) * radius)
                        + new Vector3(0f, 0f, Mathf.Sin(t) * radius);
                    Gizmos.DrawLine(prev, p);
                    prev = p;
                }
            }

            for (int k = 1; k <= latitudes; k++)             // 纬线：出立体感
            {
                float t = (k / (float)(latitudes + 1)) * Mathf.PI * 0.5f;
                DrawRing(Mathf.Cos(t) * radius, Mathf.Sin(t) * radius, seg);
            }
        }

        /// <summary>
        /// ② 阴影视锥：near 面（autoMatchShadowCone 生效时 ≈ 面片大小）→ far 面（= light.range）。
        /// 远平面半径 = range·tan(θ/2)：θ=90° 时正好等于 range = 半球半径，不浪费 texel。
        /// </summary>
        static void DrawShadowFrustum(Light l, float range)
        {
            float tanH = Mathf.Tan(Mathf.Clamp(l.spotAngle, 1f, 179f) * 0.5f * Mathf.Deg2Rad);
            float zNear = Mathf.Clamp(l.shadowNearPlane, 0.001f, range * 0.99f);
            float rNear = zNear * tanH;
            float rFar = range * tanH;
            const int seg = 32;

            DrawRing(rNear, zNear, seg);
            DrawRing(rFar, range, seg);

            int step = Mathf.Max(seg / 4, 1);
            for (int i = 0; i < seg; i += step)
            {
                float ang = Mathf.PI * 2f * i / seg;
                Vector3 dir = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                Gizmos.DrawLine(
                    dir * rNear + new Vector3(0f, 0f, zNear),
                    dir * rFar + new Vector3(0f, 0f, range));
            }
        }
#endif
    }
}
