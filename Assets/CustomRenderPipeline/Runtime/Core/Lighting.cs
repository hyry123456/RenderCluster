using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using System.Runtime.InteropServices;
using CustomRenderPipline.DefferPipeline;
using System;
using System.Collections.Generic;

namespace CustomRenderPipline
{
	struct OtherLight
    {
		public Vector4 lightColor;
		public Vector4 lightPosition;
		public Vector4 lightDirectionsAndMasks;
		public Vector4 lightSpotAngles;
		public Vector4 lightShadowData;
		// ---- 面光源（Rectangle）专用：两条半轴向量（已乘半宽/半高）----
		// lightAreaRight.w = 半宽。约定：w > 0 ⇔ 这是面光源；点光 / 聚光必须清零，
		// 否则 NativeArray(UninitializedMemory) 里的残留值会让它被误判成面光源。
		public Vector4 lightAreaRight;
		public Vector4 lightAreaUp;
    }

    public class Lighting
    {
		const string bufferName = "Lighting";

		/// <summary>
		/// 支持的直接光和间接光的数量
		/// </summary>
		const int maxDirLightCount = 4;

		/// <summary>
		/// 非方向光（点 / 聚 / 面）的**同时参与剔除**的上限。
		/// 注意这不是「GPU 每轮只能处理 N 盏」——ClusterLight.compute 里是
		/// 「每线程跨步遍历」的循环，簇内灯数与该线程数解耦，所以这个值只决定
		/// 「相机内最多保留多少盏」，超出的按重要度丢掉最弱的（见 SetupLights）。
		/// 256 盏 × 112B = 28KB；提到 1024 也只有 112KB，显存可忽略。
		/// </summary>
		const int _MAX_OTHER_LIGHT_COUNT = 1024;

		static int
			dirLightCountId = Shader.PropertyToID("_DirectionalLightCount"),
			dirLightColorsId = Shader.PropertyToID("_DirectionalLightColors"),
			dirLightDirectionsAndMasksId =
				Shader.PropertyToID("_DirectionalLightDirectionsAndMasks"),
			dirLightShadowDataId =
				Shader.PropertyToID("_DirectionalLightShadowData");

		static Vector4[]
			dirLightColors = new Vector4[maxDirLightCount],
			dirLightDirectionsAndMasks = new Vector4[maxDirLightCount],
			dirLightShadowData = new Vector4[maxDirLightCount];

		static int
			otherLightCountId = Shader.PropertyToID("_OtherLightCount"),
			otherLightBufferId = Shader.PropertyToID("_OtherLightBuffer");

		private NativeArray<OtherLight> _OtherLightArray = 
			new NativeArray<OtherLight>(_MAX_OTHER_LIGHT_COUNT, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

		private ComputeBuffer _OtherLightBuffer = new 
			ComputeBuffer(_MAX_OTHER_LIGHT_COUNT, Marshal.SizeOf<OtherLight>());

		CommandBuffer buffer = new CommandBuffer
		{
			name = bufferName
		};

		CullingResults cullingResults;
		Shadows shadows = new Shadows();
		ClusterLight clusterLight;

		public Lighting()
        {
			Shader.SetGlobalBuffer(otherLightBufferId, _OtherLightBuffer);
			GC.SuppressFinalize(_OtherLightBuffer);
        }

		/// <summary>
		/// 准备以及复制灯光数据，在灯光中会一同处理阴影数据
		/// </summary>
		/// <param name="cullingResults">裁剪数据</param>
		/// <param name="shadowSettings">阴影设置数据</param>
		/// <param name="renderingLayerMask">渲染遮罩层</param>
		public void Setup(
			ScriptableRenderContext context, int renderingLayerMask,
			CullingResults cullingResults, ShadowSetting shadowSettings
		)
		{
			this.cullingResults = cullingResults;
			buffer.BeginSample(bufferName);
			shadows.Setup(context, cullingResults, shadowSettings); //注册阴影数据
			SetupLights(renderingLayerMask);  //注册灯光，传递灯光数据
			shadows.Render();                                       //渲染阴影
			buffer.EndSample(bufferName);
			context.ExecuteCommandBuffer(buffer);
			buffer.Clear();
		}

		/// <summary>		/// 清除阴影纹理		/// </summary>
		public void Cleanup()
		{
			shadows.Cleanup();
		}

		public void Dispose()
        {
			_OtherLightArray.Dispose();
			_OtherLightBuffer.Dispose();
			clusterLight?.Dispose();
		}

		/// <summary>
		/// 非方向光的候选（先收集、超上限再裁剪）。
		/// </summary>
		struct LightCandidate
		{
			public int visibleIndex;
			public float importance;
		}

		readonly List<LightCandidate> m_candidates = new List<LightCandidate>(256);

		// 静态比较器：避免每次 Sort 都分配一个 lambda 委托（List.Sort 每帧都跑会 GC）
		static readonly Comparison<LightCandidate> kCompareByImportance =
			(a, b) => b.importance.CompareTo(a.importance);

		// 上一帧因超上限被丢掉的数量，用来「只在数量变化时」打一次日志，避免刷屏
		int m_lastDroppedCount = -1;

#if UNITY_EDITOR
		// 「面光源分支已走到」只打一次，避免每帧刷屏
		static bool s_loggedAreaLight;
#endif

		/// <summary>
		/// 取这盏灯上的 AreaLightSource（没有则 null）。
		/// **故意不做缓存**：缓存「这盏灯没有组件」在【运行时添加/移除组件】后会永久失效
		///（问题表现：Play 中挂上面光源组件却完全没效果，必须重进 Play 才行）。
		/// 场景里的非方向光通常只有几十盏，每帧一次 GetComponent 的开销可以忽略；
		/// 用正确的行为换这点性能是划算的。
		/// </summary>
		static AreaLightSource GetAreaSource(Light light)
		{
			return light.GetComponent<AreaLightSource>();
		}

		/// <summary>
		/// 灯的重要性：影响体积 × 亮度。
		/// 和「屏幕覆盖面积」正相关的粗糙近似（不依赖相机参数，也不需要逐灯投影）。
		/// 用途只有一个：可见灯数超上限时，先丢最弱的，而不是按 cullingResults 的顺序瞎丢。
		/// </summary>
		static float LightImportance(VisibleLight visibleLight)
		{
			Color c = visibleLight.finalColor;
			float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
			float r = Mathf.Max(visibleLight.range, 0.001f);
			return lum * r * r;
		}

		/// <summary>	/// 准备以及传递灯光数据	/// </summary>
		void SetupLights(int renderingLayerMask)
		{
			//所有可见的灯光数据
			NativeArray<VisibleLight> visibleLights = cullingResults.visibleLights;
			int dirLightCount = 0, otherLightCount = 0;

			// 非方向光先只收集「可见索引 + 重要度」，不立刻写进 buffer。
			// 原因：一旦可见灯数超过上限，必须按重要度裁剪——以前是
			// "if (count < 256) Setup(...)"，也就是按 cullingResults 的遍历顺序
			// 直接截断，丢掉的可能恰恰是最亮的那几盏（顺序来自场景层级，和亮度无关）。
			m_candidates.Clear();

			//枚举每一个灯光数据进行注册
			for (int i = 0; i < visibleLights.Length; i++)
			{
				VisibleLight visibleLight = visibleLights[i];   //目前灯光数据
				Light light = visibleLight.light;               //该灯光的灯光内部数据
				if ((light.renderingLayerMask & renderingLayerMask) == 0) continue;

				switch (visibleLight.lightType)
				{           //判断灯光类型，进行对应的初始化
					case LightType.Directional:
						if (dirLightCount < maxDirLightCount)
						{
							SetupDirectionalLight(
								dirLightCount++, i, ref visibleLight, light
							);
						}
						break;
					case LightType.Point:
					case LightType.Spot:
					case LightType.Rectangle:   // == LightType.Area (3)
					case LightType.Disc:
						m_candidates.Add(new LightCandidate
						{
							visibleIndex = i,
							importance = LightImportance(visibleLight),
						});
						break;
				}
			}

			// 超上限：按重要度降序保留最强的 _MAX_OTHER_LIGHT_COUNT 盏。
			// 排序只在「真的超了」的时候跑，平时（灯少）零成本。
			if (m_candidates.Count > _MAX_OTHER_LIGHT_COUNT)
			{
				m_candidates.Sort(kCompareByImportance);

				int dropped = m_candidates.Count - _MAX_OTHER_LIGHT_COUNT;
				if (dropped != m_lastDroppedCount)
				{
					m_lastDroppedCount = dropped;
					Debug.LogWarning(
						$"[Lighting] 相机内非方向光 {m_candidates.Count} 盏，超过上限 " +
						$"{_MAX_OTHER_LIGHT_COUNT}，已按重要度丢弃最弱的 {dropped} 盏。" +
						"如需全部参与，请调高 Lighting._MAX_OTHER_LIGHT_COUNT。");
				}
			}
			else if (m_lastDroppedCount > 0)
			{
				m_lastDroppedCount = 0;
			}

			int keep = Mathf.Min(m_candidates.Count, _MAX_OTHER_LIGHT_COUNT);
			for (int k = 0; k < keep; k++)
			{
				int i = m_candidates[k].visibleIndex;
				VisibleLight visibleLight = visibleLights[i];
				Light light = visibleLight.light;
				switch (visibleLight.lightType)
				{
					case LightType.Point:
						SetupPointLight(otherLightCount++, i, ref visibleLight, light);
						break;
					case LightType.Spot:
						SetupSpotLight(otherLightCount++, i, ref visibleLight, light);
						break;
					default:    // Rectangle / Disc
						SetupRectangleLight(otherLightCount++, i, ref visibleLight, light);
						break;
				}
			}

			buffer.SetGlobalInt(dirLightCountId, dirLightCount);
			if (dirLightCount > 0)
			{
				buffer.SetGlobalVectorArray(dirLightColorsId, dirLightColors);
				buffer.SetGlobalVectorArray(
					dirLightDirectionsAndMasksId, dirLightDirectionsAndMasks
				);
				buffer.SetGlobalVectorArray(dirLightShadowDataId, dirLightShadowData);
			}

			buffer.SetGlobalInt(otherLightCountId, otherLightCount);

			_OtherLightBuffer.SetData(_OtherLightArray, 0, 0, otherLightCount);
			//buffer.SetGlobalBuffer(otherLightBufferId, _OtherLightBuffer);


			//buffer.SetGlobalVectorArray(otherLightColorsId, otherLightColors);
			//buffer.SetGlobalVectorArray(
			//	otherLightPositionsId, otherLightPositions
			//);
			//buffer.SetGlobalVectorArray(
			//	otherLightDirectionsAndMasksId, otherLightDirectionsAndMasks
			//);
			//buffer.SetGlobalVectorArray(
			//	otherLightSpotAnglesId, otherLightSpotAngles
			//);
			//buffer.SetGlobalVectorArray(
			//	otherLightShadowDataId, otherLightShadowData
			//);
            //ReadyClusterLight(camera, clusterSetting);
        }

		/// <summary>
		/// 注册直接光数据，存储到数组中，之后传递给GPU
		/// </summary>
		/// <param name="index">数组编号</param>
		/// <param name="visibleIndex">灯光中的编号</param>
		/// <param name="visibleLight">可视灯光数据，存储了矩阵数据</param>
		/// <param name="light">灯光数据</param>
		void SetupDirectionalLight(
			int index, int visibleIndex, ref VisibleLight visibleLight, Light light
		)
		{
			dirLightColors[index] = visibleLight.finalColor;                    //存颜色
			Vector4 dirAndMask = -visibleLight.localToWorldMatrix.GetColumn(2); //方向
			dirAndMask.w = light.renderingLayerMask.ReinterpretAsFloat();       //遮罩层
			dirLightDirectionsAndMasks[index] = dirAndMask;                     //存储方向和遮罩层
			dirLightShadowData[index] =
				shadows.ReserveDirectionalShadows(light, visibleIndex);         //存储阴影数据
		}

		/// <summary>	/// 注册点光数据	/// </summary>
		void SetupPointLight(
			int index, int visibleIndex, ref VisibleLight visibleLight, Light light
		)
		{
			// 挂了 AreaLightSource 的灯按【矩形面光源】处理。
			// 推荐挂 **Spot**（见 SetupSpotLight）：面光源是半球发光，Spot 的 1 个阴影 tile 就够，
			// 不像点光那样要画 6 个 cube 面。
			//
			// 挂在 Point 上也能用（光照仍是面光源），但【不投影】：
			// 这里直接 return、不调 shadows.ReserveOtherShadows ⇒ 不占 16 个 tile 里的 6 个 cube 面，
			// RenderPointShadows 的 6 次 DrawShadows 一次都不会跑。
			// 保留这条分支纯粹是容错 —— 免得用户误挂 Point 时整盏灯直接失效。
			AreaLightSource area = GetAreaSource(light);
			if (area != null)
			{
				Transform t = light.transform;
				FillAreaLight(index, visibleLight.finalColor,
					t.position, t.right, t.up, t.forward,
					area.HalfWidth, area.HalfHeight, visibleLight.range,
					light.renderingLayerMask, Vector4.zero);   // ← 无阴影
				return;
			}

			OtherLight otherLight = _OtherLightArray[index];
			otherLight.lightColor = visibleLight.finalColor;
			otherLight.lightColor.w = visibleLight.range;
			Vector4 position = visibleLight.localToWorldMatrix.GetColumn(3);        //世界坐标
			position.w =
				1f / Mathf.Max(visibleLight.range * visibleLight.range, 0.00001f);  //点光范围
			otherLight.lightPosition = position;                                  //存储点光位置以及范围
			otherLight.lightSpotAngles = new Vector4(0f, 1f, 10f);                      //与射灯进行区分的赋值
			Vector4 dirAndmask = Vector4.one;
			dirAndmask.w = light.renderingLayerMask.ReinterpretAsFloat();
			otherLight.lightDirectionsAndMasks = dirAndmask;
			otherLight.lightShadowData = shadows.ReserveOtherShadows(light, visibleIndex);
			// NativeArray 是 UninitializedMemory：面光源字段必须显式清零，
			// 否则会残留上一帧某盏面光源的轴向量，被 shader 误判成面光源。
			otherLight.lightAreaRight = Vector4.zero;
			otherLight.lightAreaUp = Vector4.zero;

			_OtherLightArray[index] = otherLight;
		}

		/// <summary>	/// 注册射灯数据	/// </summary>
		void SetupSpotLight(
			int index, int visibleIndex, ref VisibleLight visibleLight, Light light
		)
		{
			// ===== 面光源主路径：Spot + AreaLightSource =====
			// 光照走【面光源 MRP】（半球、不受锥角限制），阴影走【Spot 的 1 个 tile】。
			// 为什么是 Spot 而不是 Point：面光源只往发光面正前方照，本来就是半空间的，
			// 点光的 6 面 cube 有 3 面永远照不到（纯浪费 3 倍阴影绘制 + 3 个 tile 预算）；
			// 而 Spot 的 1 个 perspective 面正好覆盖这个半空间。
			// 采样侧无需任何特殊处理：GetOtherShadow 只用 tileIndex / _OtherShadowMatrices
			// / surfaceWS.position，完全不看 spotAngles，所以面光源复用 Spot 阴影是零成本的。
			//
			// ⚠️ 取舍：阴影只覆盖 Spot 锥（默认可能只有 60°），面片照亮范围（半球）比它大，
			//    锥外是「有光无影」。想贴合就调大 spotAngle（120~160°），锥越大阴影越糊。
			AreaLightSource area = GetAreaSource(light);
			if (area != null)
			{
#if UNITY_EDITOR
				// 一次性诊断：确认「面光源分支真的被走到」。
				// 走不到时最典型的症状就是「光照看起来还是射灯」，所以这条日志能一锤定音。
				if (!s_loggedAreaLight)
				{
					s_loggedAreaLight = true;
					Debug.Log($"[Lighting] 面光源已接管：\"{light.name}\" " +
						$"（Spot + AreaLightSource，尺寸 {area.width}×{area.height}）", light);
				}
#endif
				Transform t = light.transform;
				// 先 reserve 阴影（必须是 Spot 才会只占 1 个 tile；isPoint 由 light.type 判定）
				Vector4 shadowData = shadows.ReserveOtherShadows(light, visibleIndex);
				FillAreaLight(index, visibleLight.finalColor,
					t.position, t.right, t.up, t.forward,
					area.HalfWidth, area.HalfHeight, visibleLight.range,
					light.renderingLayerMask, shadowData);
				return;
			}

			OtherLight otherLight = _OtherLightArray[index];

			otherLight.lightColor = visibleLight.finalColor;
			otherLight.lightColor.w = visibleLight.range;
			Vector4 position = visibleLight.localToWorldMatrix.GetColumn(3);
			position.w =
				1f / Mathf.Max(visibleLight.range * visibleLight.range, 0.00001f);
			otherLight.lightPosition = position;
			Vector4 dirAndMask = -visibleLight.localToWorldMatrix.GetColumn(2);
			dirAndMask.w = light.renderingLayerMask.ReinterpretAsFloat();
			otherLight.lightDirectionsAndMasks = dirAndMask;

			float innerCos = Mathf.Cos(Mathf.Deg2Rad * 0.5f * light.innerSpotAngle);
			float outerCos = Mathf.Cos(Mathf.Deg2Rad * 0.5f * visibleLight.spotAngle);
			float angleRangeInv = 1f / Mathf.Max(innerCos - outerCos, 0.001f);
			otherLight.lightSpotAngles = new Vector4(
				angleRangeInv, -outerCos * angleRangeInv, outerCos
			);
			otherLight.lightShadowData = shadows.ReserveOtherShadows(light, visibleIndex);
			otherLight.lightAreaRight = Vector4.zero;
			otherLight.lightAreaUp = Vector4.zero;

			_OtherLightArray[index] = otherLight;
		}

		/// <summary>
		/// 面光源的公共填充：C# 只负责「中心 + 两条正交轴 + 半宽半高 + range」，
		/// shader 侧的 MRP 求值与它完全解耦，两条入口（AreaLightSource / Rectangle 类型）共用。
		/// 采用「代表点近似（MRP）」：shader 会把矩形上离着色点最近的点当点光源来算，
		/// 所以这里只需要把几何信息传过去，剔除直接复用点光的球测试（球心 = 中心，半径 = range）。
		/// </summary>
		void FillAreaLight(
			int index, Vector4 finalColor,
			Vector3 center, Vector3 right, Vector3 up, Vector3 fwd,
			float halfW, float halfH, float range, int renderingLayerMask,
			Vector4 shadowData
		)
		{
			OtherLight otherLight = _OtherLightArray[index];

			// 球半径至少覆盖半对角线，否则 tile 剔除会把「照亮了 tile、但球心在 tile 外」的灯漏掉
			float boundRadius = Mathf.Sqrt(halfW * halfW + halfH * halfH);
			range = Mathf.Max(Mathf.Max(range, boundRadius + 0.001f), 0.001f);

			otherLight.lightColor = finalColor;
			otherLight.lightColor.w = range;
			otherLight.lightPosition = new Vector4(
				center.x, center.y, center.z,
				1f / Mathf.Max(range * range, 0.00001f));
			// 与 Spot 完全同口径：lightDirectionsAndMasks.xyz 存的是【指向光源的方向】= -发射方向
			//（SetupSpotLight 里同样用 `-column(2)`，为的是和 shader 的 light.direction
			//  「表面→光源」点乘能算出锥形衰减）。
			// Unity 的 Spot 锥沿 +forward ⇒ 发光面外法线 = +fwd，这里取反存；
			// shader 侧再用 `-normalize(xyz)` 还原成外法线给 MRP 用。
			otherLight.lightDirectionsAndMasks = new Vector4(
				-fwd.x, -fwd.y, -fwd.z,
				renderingLayerMask.ReinterpretAsFloat());
			// 与点光同款 (0,1) ⇒ 锥形衰减恒为 1。
			// 面光源的光照是【半球】而不是锥：锥角只用来决定阴影覆盖范围，
			// 若这里带上 spot 参数，面片照亮的范围会被 Spot 锥硬切一刀。
			otherLight.lightSpotAngles = new Vector4(0f, 1f, 10f);
			otherLight.lightAreaRight = new Vector4(
				right.x * halfW, right.y * halfW, right.z * halfW, halfW);
			otherLight.lightAreaUp = new Vector4(
				up.x * halfH, up.y * halfH, up.z * halfH, halfH);
			// 阴影：挂 Spot 时由调用方传入（1 个 tile）；挂 Point / Rectangle 时给 0（不投影）
			otherLight.lightShadowData = shadowData;

			_OtherLightArray[index] = otherLight;
		}

		/// <summary>
		/// 注册 Unity 原生 Rectangle / Disc 类型的光源。
		/// ⚠️ 实际很少走到这里：Unity 的 Light Inspector 把 Rectangle 锁成 Baked，
		/// 实时管线基本拿不到它的 visibleLights。面光源的主路径是
		/// 「Point 光 + <see cref="AreaLightSource"/>」（见 SetupPointLight）。
		/// 保留这条分支是为了「预设/代码强行设成 Realtime 的 Rectangle 灯」也能正确渲染。
		/// </summary>
		void SetupRectangleLight(
			int index, int visibleIndex, ref VisibleLight visibleLight, Light light
		)
		{
			// 用 transform 的轴而不是 localToWorld 的列：Transform.right/up/forward 已归一化，
			// 不会被 light 上的缩放污染（localToWorld 的列带缩放，直接乘半宽会算错尺寸）。
			Transform t = light.transform;
			Vector3 right = t.right;
			Vector3 up = t.up;
			Vector3 fwd = t.forward;

			// areaSize 只对 Rectangle 有意义；Disc / 其他类型退化成一个点（半轴极小，
			// 但 lightAreaRight.w 仍 > 0 ⇒ 走面光分支，视觉上等价于点光）
			Vector2 size = light.type == LightType.Rectangle ? light.areaSize : Vector2.zero;
			float halfW = Mathf.Max(size.x * 0.5f, 0.001f);
			float halfH = Mathf.Max(size.y * 0.5f, 0.001f);

			FillAreaLight(index, visibleLight.finalColor,
				t.position, right, up, fwd,
				halfW, halfH, visibleLight.range, light.renderingLayerMask, Vector4.zero);
		}

		public void ReadyClusterLight(Camera camera, ClusterLightSetting setting,
			CommandBuffer buffer, int width, int height)
		{
			if (clusterLight == null)
				clusterLight = new ClusterLight();

            if (setting.isUse)
            {
                buffer.EnableShaderKeyword("_USE_CLUSTER");
            }
            else
            {
                buffer.DisableShaderKeyword("_USE_CLUSTER");
                return;
            }

			clusterLight.ComputeLightCluster(buffer, setting.clusterLightCS, 
				camera, width, height, setting.renderVisableLight);
		}
	}
}