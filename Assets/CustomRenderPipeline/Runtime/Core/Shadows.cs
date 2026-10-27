using UnityEngine;
using UnityEngine.Rendering;
using Cluster;

namespace CustomRenderPipline
{
    public class Shadows
    {
		const string bufferName = "Shadows";

		const int maxShadowedDirLightCount = 4, maxShadowedOtherLightCount = 16;
		const int maxCascades = 4;

		/// <summary>「自动把排除 layer 补进灯光 Culling Mask」只警告一次。</summary>
		static bool s_warnedExclLayer;

		/// <summary>「自动把 BRG 批次专用 layer 从灯光 Culling Mask 移除」只警告一次。</summary>
		static bool s_warnedBRGCullLayer;

		static string[] directionalFilterKeywords = {
			"_DIRECTIONAL_PCF3",
			"_DIRECTIONAL_PCF5",
			"_DIRECTIONAL_PCF7",
		};

		static string[] otherFilterKeywords = {
			"_OTHER_PCF3",
			"_OTHER_PCF5",
			"_OTHER_PCF7",
		};

		static string[] cascadeBlendKeywords = {
			"_CASCADE_BLEND_SOFT",
			"_CASCADE_BLEND_DITHER"
		};

		static string[] shadowMaskKeywords = {
			"_SHADOW_MASK_ALWAYS",
			"_SHADOW_MASK_DISTANCE"
		};

		static int
			dirShadowAtlasId = Shader.PropertyToID("_DirectionalShadowAtlas"),
			dirShadowMatricesId = Shader.PropertyToID("_DirectionalShadowMatrices"),
			otherShadowAtlasId = Shader.PropertyToID("_OtherShadowAtlas"),
			otherShadowMatricesId = Shader.PropertyToID("_OtherShadowMatrices"),
			otherShadowTilesId = Shader.PropertyToID("_OtherShadowTiles"),
			cascadeCountId = Shader.PropertyToID("_CascadeCount"),
			cascadeCullingSpheresId = Shader.PropertyToID("_CascadeCullingSpheres"),
			cascadeDataId = Shader.PropertyToID("_CascadeData"),
			shadowAtlastSizeId = Shader.PropertyToID("_ShadowAtlasSize"),
			shadowDistanceFadeId = Shader.PropertyToID("_ShadowDistanceFade"),
			shadowPancakingId = Shader.PropertyToID("_ShadowPancaking");

		static Vector4[]
			cascadeCullingSpheres = new Vector4[maxCascades],
			cascadeData = new Vector4[maxCascades],
			otherShadowTiles = new Vector4[maxShadowedOtherLightCount];

		static Matrix4x4[]
			dirShadowMatrices = new Matrix4x4[maxShadowedDirLightCount * maxCascades],
			otherShadowMatrices = new Matrix4x4[maxShadowedOtherLightCount];

		struct ShadowedDirectionalLight
		{
			public int visibleLightIndex;
			public float slopeScaleBias;
			public float nearPlaneOffset;
			public Vector3 forward, right, up;
		}

		ShadowedDirectionalLight[] shadowedDirectionalLights =
			new ShadowedDirectionalLight[maxShadowedDirLightCount];

		struct ShadowedOtherLight
		{
			public int visibleLightIndex;
			public float slopeScaleBias;
			public float normalBias;
			public bool isPoint;
		}

		ShadowedOtherLight[] shadowedOtherLights =
			new ShadowedOtherLight[maxShadowedOtherLightCount];

		int shadowedDirLightCount, shadowedOtherLightCount;

		CommandBuffer buffer = new CommandBuffer
		{
			name = bufferName
		};

		ScriptableRenderContext context;

		CullingResults cullingResults;

		ShadowSetting settings;

		bool useShadowMask;

		Vector4 atlasSizes;

		/// <summary>本帧的阴影路径（资产开关缓存）：SRP / Cluster / ClusterDebug。</summary>
		ClusterShadowMode m_shadowMode = ClusterShadowMode.SRP;

		/// <summary>
		/// 阴影 split（方向光的级联 / 点光的 cube 面）的剔除包围球，键 = visibleLightIndex。
		/// 供 BRG 的阴影视图做【按级联】的逐实例剔除：BRG 的
		/// `BatchDrawCommand.splitVisibilityMask` 就是"这条命令在哪些 split 里可见"，
		/// 一个 split 恰好 = 一个级联（Unity 官方文档原话）。
		/// 球取自 Unity 自己的 ShadowSplitData.cullingSphere —— 普通 MeshRenderer 的
		/// 阴影投射物剔除用的也正是它，所以语义与 Unity 一致，不会再出现「远处没阴影」。
		/// 时序：BRG 的 OnPerformCulling 跑在 context.Cull 阶段（早于本帧阴影计算），
		/// 所以 BRG 拿到的是【上一帧】的球（静态物体无差别；半径另留 25% 余量）。
		/// </summary>

		/// <summary>初始化本帧阴影渲染所需的上下文（渲染上下文 / 剔除结果 / 阴影设置）。</summary>
		public void Setup(
			ScriptableRenderContext context, CullingResults cullingResults,
			ShadowSetting settings
		)
		{
			this.context = context;
			this.cullingResults = cullingResults;
			this.settings = settings;

			shadowedDirLightCount = shadowedOtherLightCount = 0;
			useShadowMask = false;
		}

		public void Cleanup()
		{
			buffer.ReleaseTemporaryRT(dirShadowAtlasId);
            if (shadowedOtherLightCount > 0)
            {
                buffer.ReleaseTemporaryRT(otherShadowAtlasId);
            }
            ExecuteBuffer();
		}

		/// <summary>为方向光预留阴影图空间，并判断该光源是否支持阴影。</summary>
		/// <returns>阴影强度、图集瓦片索引（cascadeCount × 已登记数）、normalBias、阴影遮罩通道；不支持时返回 (0,0,0,-1)。</returns>
		public Vector4 ReserveDirectionalShadows(
			Light light, int visibleLightIndex
		)
		{
            if (
                shadowedDirLightCount < maxShadowedDirLightCount &&
                light.shadows != LightShadows.None && light.shadowStrength > 0f
            )
            {
                // 【阴影交给 MeshRenderer 的模式】灯光的 cullingMask 必须包含 BRG 用的排除 layer，
                // 否则这些物体不会进入 shadow caster 集合 ⇒ 完全没有阴影（且很难一眼看出原因）。
                // 处理方式：直接把它补进灯光的 Culling Mask（而不是"关掉该模式并回退"）。
                // 回退会让 BRG 批次重新投阴影 ⇒ BRG 与 MeshRenderer 同时投 = 阴影画两遍、
                // 成本翻倍，而画面完全正常，是比"没阴影"更难查的状态。
                // 只改运行时对象，退出 Play 场景重载即恢复，不会脏化场景。
                int exclLayer = ClusterProverty.BRGShadowExcludeLayer;
                if (exclLayer >= 0 && exclLayer < 32 &&
                    (light.cullingMask & (1 << exclLayer)) == 0)
                {
                    light.cullingMask |= 1 << exclLayer;
                    if (!s_warnedExclLayer)
                    {
                        s_warnedExclLayer = true;
                        Debug.LogWarning($"[RVT/BRG] 已把 layer {exclLayer} 自动补进方向光 \"{light.name}\" 的 " +
                            "Culling Mask（仅本次运行有效，不会写进场景）。" +
                            "要永久生效请手动打开该 layer；不想用这个模式就把 brgGBufferExcludeLayer 设为 -1。");
                    }
                }

                // ★ BRG 批次自己在 BRGCullLayer 上，把它从灯光的 cullingMask 里【去掉】——
                // 这样阴影视图的剔除阶段就会直接跳过整条 BRG 批次，是"BRG 不投阴影"最可靠的一层拦截：
                // 它不依赖 BatchFilterSettings.shadowCastingMode（2022.3 的 SRP 下不可靠），
                // 也不依赖 shader 有没有 ShadowCaster pass。
                int brgLayer = ClusterProverty.BRGCullLayer;
                if (!BatchRenderManager.BRGCastsShadows &&
                    brgLayer >= 0 && brgLayer < 32 &&
                    (light.cullingMask & (1 << brgLayer)) != 0)
                {
                    light.cullingMask &= ~(1 << brgLayer);
                    if (!s_warnedBRGCullLayer)
                    {
                        s_warnedBRGCullLayer = true;
                        Debug.LogWarning($"[RVT/BRG] 已把 layer {brgLayer}（BRG 批次专用层）从方向光 " +
                            $"\"{light.name}\" 的 Culling Mask 移除（仅本次运行有效）⇒ BRG 不再参与阴影投射。");
                    }
                }
				float maskChannel = -1;
				LightBakingOutput lightBaking = light.bakingOutput;
				if (
					lightBaking.lightmapBakeType == LightmapBakeType.Mixed &&
					lightBaking.mixedLightingMode == MixedLightingMode.Shadowmask
				)
				{
					useShadowMask = true;
					maskChannel = lightBaking.occlusionMaskChannel;
				}

                if (!cullingResults.GetShadowCasterBounds(
                    visibleLightIndex, out Bounds b
                ))
                {
                    // BRG 接管绘制后，场景物体的 MeshRenderer 被禁用，不会进入 cullingResults 的
                    // shadow caster 集合，于是 GetShadowCasterBounds 对本该投射阴影的光返回 false，
                    // 导致该方向光被“无投射物”跳过 —— 表现即“开了 BRG 后阴影全没了/出错”。
                    // 只要本工程仍有 BRG 实例在投射阴影，就不能据此跳过本光。
                    if (BatchRenderManager.Instance == null ||
                        !BatchRenderManager.Instance.HasBatches)
                    {
                        return new Vector4(-light.shadowStrength, 0f, 0f, maskChannel);
                    }
                }

                shadowedDirectionalLights[shadowedDirLightCount] =
					new ShadowedDirectionalLight
					{
						visibleLightIndex = visibleLightIndex,
						slopeScaleBias = light.shadowBias,
						nearPlaneOffset = light.shadowNearPlane,
						forward = light.transform.forward,
						right = light.transform.right,
						up = light.transform.up,
                    };
				return new Vector4(
					light.shadowStrength,
					settings.directional.cascadeCount * shadowedDirLightCount++,
					light.shadowNormalBias, maskChannel
				);
			}
			return new Vector4(0f, 0f, 0f, -1f);
		}

		/// <summary>为其他光源（点光 / 聚光）预留阴影图空间，返回其阴影参数。</summary>
		public Vector4 ReserveOtherShadows(Light light, int visibleLightIndex)
		{
			if (light.shadows == LightShadows.None || light.shadowStrength <= 0f)
			{
				return new Vector4(0f, 0f, 0f, -1f);
			}
			float maskChannel = -1f;
			LightBakingOutput lightBaking = light.bakingOutput;
			if (
				lightBaking.lightmapBakeType == LightmapBakeType.Mixed &&
				lightBaking.mixedLightingMode == MixedLightingMode.Shadowmask
			)
			{
				useShadowMask = true;
				maskChannel = lightBaking.occlusionMaskChannel;
			}

			bool isPoint = light.type == LightType.Point;
			int newLightCount = shadowedOtherLightCount + (isPoint ? 6 : 1);
			if (
				newLightCount > maxShadowedOtherLightCount ||
				(!cullingResults.GetShadowCasterBounds(visibleLightIndex, out Bounds b)
					&& (BatchRenderManager.Instance == null ||
						!BatchRenderManager.Instance.HasBatches))
			)
			{
				return new Vector4(-light.shadowStrength, 0f, 0f, maskChannel);
			}

			shadowedOtherLights[shadowedOtherLightCount] = new ShadowedOtherLight
			{
				visibleLightIndex = visibleLightIndex,
				slopeScaleBias = light.shadowBias,
				normalBias = light.shadowNormalBias,
				isPoint = isPoint
			};

			Vector4 data = new Vector4(
				light.shadowStrength, shadowedOtherLightCount,
				isPoint ? 1f : 0f, maskChannel
			);
			shadowedOtherLightCount = newLightCount;
			return data;
		}

		public void Render()
		{
			// 本帧阴影路径（资产开关）：SRP / Cluster / ClusterDebug。
			// ClusterDebug 走与 Cluster 完全相同的 GPU 路径，只是每级联追加 CPU/GPU 对账日志。
			m_shadowMode = ClusterControl.Instance != null
				? ClusterControl.Instance.ClusterAssembleAsset.clusterShadowMode
				: ClusterShadowMode.SRP;

			// 【真正的 cluster 管线】首次（或物体有变动时）把所有簇摊平到世界空间。
			// 只在 cluster 两条路径下触发，SRP 模式零开销。
			if (m_shadowMode != ClusterShadowMode.SRP)
			{
				ClusterShadowRenderer.Instance.Rebuild();
			}

			if (shadowedDirLightCount > 0)
			{
				RenderDirectionalShadows();
			}
			else
			{
				buffer.GetTemporaryRT(
					dirShadowAtlasId, 1, 1,
					32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap
				);
			}
			if (shadowedOtherLightCount > 0)
			{
				RenderOtherShadows();
			}
			else
			{
				buffer.SetGlobalTexture(otherShadowAtlasId, dirShadowAtlasId);
			}

			buffer.BeginSample(bufferName);
			SetKeywords(shadowMaskKeywords, useShadowMask ?
				QualitySettings.shadowmaskMode == ShadowmaskMode.Shadowmask ? 0 : 1 :
				-1
			);
			buffer.SetGlobalInt(
				cascadeCountId,
				shadowedDirLightCount > 0 ? settings.directional.cascadeCount : 0
			);
			float f = 1f - settings.directional.cascadeFade;
			buffer.SetGlobalVector(
				shadowDistanceFadeId, new Vector4(
					1f / settings.maxDistance, 1f / settings.distanceFade,
					1f / (1f - f * f)
				)
			);
			buffer.SetGlobalVector(shadowAtlastSizeId, atlasSizes);
			buffer.EndSample(bufferName);
			ExecuteBuffer();

		}


		void RenderDirectionalShadows()
		{
			int atlasSize = (int)settings.directional.atlasSize;
			atlasSizes.x = atlasSize;
			atlasSizes.y = 1f / atlasSize;
			buffer.GetTemporaryRT(
				dirShadowAtlasId, atlasSize, atlasSize,
				32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap
			);
			buffer.SetRenderTarget(
				dirShadowAtlasId,
				RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store
			);
			buffer.ClearRenderTarget(true, false, Color.clear);
			buffer.SetGlobalFloat(shadowPancakingId, 1f);
			buffer.BeginSample(bufferName);
			ExecuteBuffer();

			int tiles = shadowedDirLightCount * settings.directional.cascadeCount;
			int split = tiles <= 1 ? 1 : tiles <= 4 ? 2 : 4;
			int tileSize = atlasSize / split;

			for (int i = 0; i < shadowedDirLightCount; i++)
			{
				RenderDirectionalShadows(i, split, tileSize);
			}

			buffer.SetGlobalVectorArray(
				cascadeCullingSpheresId, cascadeCullingSpheres
			);
			buffer.SetGlobalVectorArray(cascadeDataId, cascadeData);
			buffer.SetGlobalMatrixArray(dirShadowMatricesId, dirShadowMatrices);
			SetKeywords(
				directionalFilterKeywords, (int)settings.directional.filter - 1
			);
			SetKeywords(
				cascadeBlendKeywords, (int)settings.directional.cascadeBlend - 1
			);
			buffer.EndSample(bufferName);
			ExecuteBuffer();
		}

		// 级联数据缓存（避免每帧分配；方向光级联数上限 4，这里留到 8）
		readonly Matrix4x4[] m_cascadeView = new Matrix4x4[8];
		readonly Matrix4x4[] m_cascadeProj = new Matrix4x4[8];
		readonly ShadowSplitData[] m_cascadeSplit = new ShadowSplitData[8];

		void RenderDirectionalShadows(int index, int split, int tileSize)
		{
			ShadowedDirectionalLight light = shadowedDirectionalLights[index];
			var shadowSettings =
				new ShadowDrawingSettings(cullingResults, light.visibleLightIndex, BatchCullingProjectionType.Orthographic)
				{
					useRenderingLayerMaskTest = true
				};
			int cascadeCount = settings.directional.cascadeCount;
			int tileOffset = index * cascadeCount;
			Vector3 ratios = settings.directional.CascadeRatios;
			//不要缩小该级联的“阴影投射物裁剪球”。
			//shadowCascadeBlendCullingFactor 是作用在 splitData.cullingSphere 半径上的乘数；
			//取 <1 会把落在 [factor*R, R] 这一圈环带里的投射物整批裁掉，
			//结果就是：物体在主画面里看得见，却没有被画进该级联的阴影图 ——
			//表现为“被房子遮住的区域只有一部分有阴影”，且不同级联之间投射物集合不一致、阴影剧变。
			//取 1 = 不收缩，保证每个级联都渲染其真实阴影视锥内的全部投射物（只增不减）。
			float cullingFactor = 1f;
			float tileScale = 1f / split;

			// ===== 两条阴影路径的分岔（资产上切换，几何/灯光/级联设置都不用动）=====
			//   SRP         ：逐物体剔除 + SRP Batcher + context.DrawShadows
			//   Cluster     ：逐簇 GPU 剔除 + 每级联一次 DrawProceduralIndirect
			//   ClusterDebug：同 Cluster，额外打印统计信息
			bool useCluster = m_shadowMode == ClusterShadowMode.Cluster ||
							  m_shadowMode == ClusterShadowMode.ClusterDebug;
			ClusterShadowRenderer cluster = ClusterShadowRenderer.Instance;
			if (useCluster)
			{
				cluster.targetTexels = ClusterControl.Instance.ClusterAssembleAsset.clusterTargetTexels;
				cluster.debugForceAll = ClusterControl.Instance.ClusterAssembleAsset.clusterShadowForceAll
					? 1 : 0;   // 1 = 强制全可见（隔离测试：区分"剔除错" vs "绘制链坏"）；0 = 标准 compute 剔除
				cluster.BeginLight();
			}

			// ===== pass 1：只做 CPU 计算（级联矩阵 / 世界平面 / 图集矩阵），不提交任何命令 =====
			// 为什么必须拆两遍：cluster 的剔除要【在所有级联登记完之后、任何 draw 之前】
			// 一次性提交，否则又回到「每级联 Reset/Cull/BuildArgs」= 4×3 个 dispatch。
			for (int i = 0; i < cascadeCount; i++)
			{
				cullingResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(
					light.visibleLightIndex, i, cascadeCount, ratios, tileSize,
					light.nearPlaneOffset, out Matrix4x4 viewMatrix,
					out Matrix4x4 projectionMatrix, out ShadowSplitData splitData
				);

				splitData.shadowCascadeBlendCullingFactor = cullingFactor;
				m_cascadeView[i] = viewMatrix;
				m_cascadeProj[i] = projectionMatrix;
				m_cascadeSplit[i] = splitData;

				if (index == 0)
				{
					SetCascadeData(i, splitData.cullingSphere, tileSize);

				}

				int tileIndex = tileOffset + i;
				Vector2 offset = new Vector2(tileIndex % split, tileIndex / split);
				dirShadowMatrices[tileIndex] = ConvertToAtlasMatrix(
					projectionMatrix * viewMatrix, offset, tileScale
				);

				// cluster：登记该级联的视锥（worldToClip 与下方 Draw 用的逐位一致）+ LOD 参数（纯 CPU，不提交）。
				// worldToClip = GL.GetGPUProjectionMatrix(proj,true) * view：正是绘制侧实际用的矩阵，
				// 用它提的 6 个平面口径与渲染完全相同 ⇒ 不漏不重、不出现过剔除。
				if (useCluster)
				{
					Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(projectionMatrix, true);
					Matrix4x4 worldToClip = gpuProj * viewMatrix;
					cluster.AddCascade(worldToClip, projectionMatrix, splitData.cullingSphere, tileSize);
				}
			}

		// ===== 一次性 GPU 剔除：整帧只有 Reset / CullAll / BuildArgs 三个 dispatch =====
		if (useCluster)
		{
			// compute 剔除（Reset / CullAll / BuildArgs）统一录进独立 CommandBuffer 并立即执行：
			//   · CullAll 把可见簇 ID 直接原子追加进 _Visible（RWStructuredBuffer，UAV 可写，
			//     无"Index 缓冲不能当 UAV"的限制，也【不需要任何 Raw→Index 跨类型拷贝】⇒ 不再慢、不再错），
			//   · BuildArgs 写好 indirect args。
			// 绘制时 shader 在 VS 里查 _Visible + _Indices + _Positions 还原几何（拼索引从 compute 移到 VS）。
			// 本方案已彻底删除 CopyForDraw，也不再需要 GetData 强制同步 stall：
			//   _Args 由 compute 写出、被 DrawProceduralIndirect 的固定功能单元消费；SRP 上下文内
			//   ExecuteCommandBuffer 已让 compute 先于后续绘制落地，资源屏障由 Unity 处理。
			var cullCmd = new CommandBuffer { name = "ClusterCull" };
			cluster.Cull(cullCmd);
			context.ExecuteCommandBuffer(cullCmd);
			cullCmd.Release();
		}

		if (useCluster)
		{
				// ===== Cluster / ClusterDebug 模式：只画 cluster，彻底不跑 GPUDravinSystem / context.DrawShadows =====
				// 4 个级联的 viewport + 矩阵 + 间接绘制全部累积进【同一个 buffer】，
				// 循环外只 ExecuteBuffer 一次，把材质 PrepareKeywords 从 4× 降到 1×（~2.5ms → ~0.6ms）。
				buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
				for (int i = 0; i < cascadeCount; i++)
				{
					int tileIndex = tileOffset + i;
					// viewport 决定画到 atlas 的哪个 tile（4 级联共用同一张图）
					SetTileViewport(tileIndex, split, tileSize);
				// SetViewProjectionMatrices 供下方 cutout 的 context.DrawShadows 使用（SRP 路径同源）。
				buffer.SetViewProjectionMatrices(m_cascadeView[i], m_cascadeProj[i]);
				// cluster 阴影矩阵：worldToClip = GetGPUProjectionMatrix(proj) * view。
				// ⚠️【关键修复 2026-10-26】绝不能用裸 proj*view：自定义 HLSLPROGRAM 在
				// DrawProceduralIndirect 下不走 SetViewProjectionMatrices，而后者内部会对投影矩阵
				// 套 GL.GetGPUProjectionMatrix，把 OpenGL 风格 [-1,1] 的 z 换成平台期望的 [0,1]/reversed-Z
				// 深度约定。少了这一步 ⇒ D3D 上 NDC z 大量落在 [0,1] 之外被裁光 ⇒ 整张阴影图几乎全空
				// （与"位置探针全绿、全画旁路也画不出阴影、近处偶尔一个影子 + 移动波动"完全吻合）。
				// 采样侧 ConvertToAtlasMatrix(proj*view) 已用 usesReversedZBuffer 做等价 z 取反，
				// 两侧深度约定一致，阴影才能对上。true = 渲染进 RenderTexture，与 SetViewProjectionMatrices 一致。
				Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(m_cascadeProj[i], true);
				Matrix4x4 worldToClip = gpuProj * m_cascadeView[i];
					cluster.Draw(buffer, i, worldToClip);   // 只"添加"到 buffer，不执行
				}
                buffer.SetGlobalDepthBias(0f, 0f);
                ExecuteBuffer();               // 一次执行：compute 剔除 + 4 级联间接绘制

                // ===== cutout 物体（草/蕨/灌木等需 alpha 镂空的材质）改走 SRP 阴影路径 =====
                // Cluster 阴影 shader 是纯几何、不采样贴图 ⇒ 这类物体若进 Cluster 会画成
                // 实心方块 + 生出巨量簇（浪费且形状不对）。故把它们排除出 Cluster，
                // 这里用 rendering layer mask 只挑出「标记了 cutoff」的物体，
                // 交给 context.DrawShadows：用物体自身的 ShadowCaster 按原几何投阴影。
                // 植被/卡片模型本身即外壳几何（只有最外一层），阴影就是卡片形状，无需额外镂空处理。
                // 非 cutout 物体的 renderingLayerMask 不含该位 ⇒ 被本 pass 自动跳过，不会与 Cluster 重复绘制。
                // 【2022.3 的过滤口径】ShadowDrawingSettings 根本没有 renderingLayerMask 属性（所有 Unity 版本都没有），
                // 掩码只能来自"投射阴影的灯光自身"：useRenderingLayerMaskTest=true 时 Unity 按
                //   (Renderer.renderingLayerMask & Light.renderingLayerMask) != 0
                // 过滤投射物。所以这里临时把该灯光的掩码切成"只含 cutoff 位"，画完立刻还原。
                if (ClusterShadowRenderer.Instance.HasCutoffCasters())
                {
                    var cutoffLight = cullingResults.visibleLights[light.visibleLightIndex].light;
                    // Light.renderingLayerMask 在 2022.3 是 int（Renderer 侧那个才是 uint），别混用
                    int savedLightRLM = cutoffLight != null ? cutoffLight.renderingLayerMask : 0;
                    if (cutoffLight != null)
                        cutoffLight.renderingLayerMask = 1 << ClusterProverty.CutoffRenderingLayerBit;
                    shadowSettings.useRenderingLayerMaskTest = true;
                    for (int i = 0; i < cascadeCount; i++)
                    {
                        int tileIndex = tileOffset + i;
                        SetTileViewport(tileIndex, split, tileSize);
                        buffer.SetViewProjectionMatrices(m_cascadeView[i], m_cascadeProj[i]);
                        buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
                        ExecuteBuffer();                 // 应用本級联的 viewport + 矩阵
                        shadowSettings.splitData = m_cascadeSplit[i];
                        context.DrawShadows(ref shadowSettings);
                        buffer.SetGlobalDepthBias(0f, 0f);
                    }
                    if (cutoffLight != null)
                        cutoffLight.renderingLayerMask = savedLightRLM;   // 还原，避免污染后续 pass
                }
            }
            else
			{
				// ===== SRP 模式：GPUDravinSystem 画 BRG/RVT + context.DrawShadows 画 MeshRenderer =====
				for (int i = 0; i < cascadeCount; i++)
				{
					int tileIndex = tileOffset + i;
					SetTileViewport(tileIndex, split, tileSize);
					buffer.SetViewProjectionMatrices(m_cascadeView[i], m_cascadeProj[i]);
					buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
					GPUDravinSystem.Instance.SetDirectionCullingPlane(m_cascadeSplit[i].cullingSphere,
						light.forward, light.up, light.right);
					GPUDravinSystem.Instance.DrawShadows(context, buffer);
					ExecuteBuffer();
					shadowSettings.splitData = m_cascadeSplit[i];
					context.DrawShadows(ref shadowSettings);
					buffer.SetGlobalDepthBias(0f, 0f);
				}
			}

		}


		void SetCascadeData(int index, Vector4 cullingSphere, float tileSize)
		{
			float texelSize = 2f * cullingSphere.w / tileSize;
			float filterSize = texelSize * ((float)settings.directional.filter + 1f);
			cullingSphere.w -= filterSize;
			cullingSphere.w *= cullingSphere.w;
			cascadeCullingSpheres[index] = cullingSphere;
			cascadeData[index] = new Vector4(
				1f / cullingSphere.w,
				filterSize * 1.4142136f
			);
		}

		void RenderOtherShadows()
		{
			int atlasSize = (int)settings.other.atlasSize;
			atlasSizes.z = atlasSize;
			atlasSizes.w = 1f / atlasSize;
			buffer.GetTemporaryRT(
				otherShadowAtlasId, atlasSize, atlasSize,
				32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap
			);
			buffer.SetRenderTarget(
				otherShadowAtlasId,
				RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store
			);
			buffer.ClearRenderTarget(true, false, Color.clear);
			buffer.SetGlobalFloat(shadowPancakingId, 0f);
			buffer.BeginSample(bufferName);
			ExecuteBuffer();

			int tiles = shadowedOtherLightCount;
			int split = tiles <= 1 ? 1 : tiles <= 4 ? 2 : 4;
			int tileSize = atlasSize / split;

			for (int i = 0; i < shadowedOtherLightCount;)
			{
				if (shadowedOtherLights[i].isPoint)
				{
					RenderPointShadows(i, split, tileSize);
					i += 6;
				}
				else
				{
					RenderSpotShadows(i, split, tileSize);
					i += 1;
				}
			}

			buffer.SetGlobalMatrixArray(otherShadowMatricesId, otherShadowMatrices);
			buffer.SetGlobalVectorArray(otherShadowTilesId, otherShadowTiles);
			SetKeywords(
				otherFilterKeywords, (int)settings.other.filter - 1
			);
			buffer.EndSample(bufferName);
			ExecuteBuffer();
		}

		void RenderSpotShadows(int index, int split, int tileSize)
		{
			ShadowedOtherLight light = shadowedOtherLights[index];
			var shadowSettings =
				new ShadowDrawingSettings(cullingResults, light.visibleLightIndex, BatchCullingProjectionType.Perspective)
				{
					useRenderingLayerMaskTest = true
				};
			cullingResults.ComputeSpotShadowMatricesAndCullingPrimitives(
				light.visibleLightIndex, out Matrix4x4 viewMatrix,
				out Matrix4x4 projectionMatrix, out ShadowSplitData splitData
			);
			shadowSettings.splitData = splitData;
			// 聚光灯：只有 1 个 split（不是级联）
			float texelSize = 2f / (tileSize * projectionMatrix.m00);
			float filterSize = texelSize * ((float)settings.other.filter + 1f);
			float bias = light.normalBias * filterSize * 1.4142136f;
			Vector2 offset = SetTileViewport(index, split, tileSize);
			float tileScale = 1f / split;
			SetOtherTileData(index, offset, tileScale, bias);
			otherShadowMatrices[index] = ConvertToAtlasMatrix(
				projectionMatrix * viewMatrix, offset, tileScale
			);

			buffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
			buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
			//GPUDravinSystem.Instance.DrawShadows(context, buffer,
			//	ClustDrawType.Shadow, projectionMatrix * viewMatrix);
            GPUDravinSystem.Instance.DrawShadows(context, buffer);
			ExecuteBuffer();
			context.DrawShadows(ref shadowSettings);
			buffer.SetGlobalDepthBias(0f, 0f);
		}

		void RenderPointShadows(int index, int split, int tileSize)
		{
			ShadowedOtherLight light = shadowedOtherLights[index];
			var shadowSettings =
				new ShadowDrawingSettings(cullingResults, light.visibleLightIndex, BatchCullingProjectionType.Orthographic)
				{
					useRenderingLayerMaskTest = true
				};
			float texelSize = 2f / tileSize;
			float filterSize = texelSize * ((float)settings.other.filter + 1f);
			float bias = light.normalBias * filterSize * 1.4142136f;
			float tileScale = 1f / split;
			float fovBias =
				Mathf.Atan(1f + bias + filterSize) * Mathf.Rad2Deg * 2f - 90f;
			for (int i = 0; i < 6; i++)
			{
				cullingResults.ComputePointShadowMatricesAndCullingPrimitives(
					light.visibleLightIndex, (CubemapFace)i, fovBias,
					out Matrix4x4 viewMatrix, out Matrix4x4 projectionMatrix,
					out ShadowSplitData splitData
				);
				viewMatrix.m11 = -viewMatrix.m11;
				viewMatrix.m12 = -viewMatrix.m12;
				viewMatrix.m13 = -viewMatrix.m13;

				shadowSettings.splitData = splitData;
				// 点光源：6 个 cube 面 = 6 个 split（每个面一个）
				int tileIndex = index + i;
				Vector2 offset = SetTileViewport(tileIndex, split, tileSize);
				SetOtherTileData(tileIndex, offset, tileScale, bias);
				otherShadowMatrices[tileIndex] = ConvertToAtlasMatrix(
					projectionMatrix * viewMatrix, offset, tileScale
				);

				buffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
				buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
				//GPUDravinSystem.Instance.DrawShadows(context, buffer,
				//	ClustDrawType.Shadow, projectionMatrix * viewMatrix);
				GPUDravinSystem.Instance.DrawShadows(context, buffer);

				ExecuteBuffer();
                context.DrawShadows(ref shadowSettings);
                buffer.SetGlobalDepthBias(0f, 0f);
			}
		}

		void SetOtherTileData(int index, Vector2 offset, float scale, float bias)
		{
			float border = atlasSizes.w * 0.5f;
			Vector4 data;
			data.x = offset.x * scale + border;
			data.y = offset.y * scale + border;
			data.z = scale - border - border;
			data.w = bias;
			otherShadowTiles[index] = data;
		}

		/// <summary>
		/// 把投影矩阵（clip 空间）转换成【阴影图集（UV [0,1]）空间】的矩阵，使阴影能正确映射到图集瓦片。
		/// 即把 NDC 的 [-1,1] 映射到 [0,1]（z 做 0.5*xyz + 0.5 的偏移），并按 reversed-Z 做等价 z 取反，
		/// 这样投影矩阵就能直接把几何画进阴影图。
		/// </summary>
		/// <param name="m">原始 VP 矩阵</param>
		/// <param name="offset">瓦片在图集中的偏移（UV）</param>
		/// <param name="scale">瓦片占图集的比例</param>
		/// <returns>转换后的图集空间矩阵</returns>
		Matrix4x4 ConvertToAtlasMatrix(Matrix4x4 m, Vector2 offset, float scale)
		{
			if (SystemInfo.usesReversedZBuffer)
			{
				m.m20 = -m.m20;
				m.m21 = -m.m21;
				m.m22 = -m.m22;
				m.m23 = -m.m23;
			}
			m.m00 = (0.5f * (m.m00 + m.m30) + offset.x * m.m30) * scale;
			m.m01 = (0.5f * (m.m01 + m.m31) + offset.x * m.m31) * scale;
			m.m02 = (0.5f * (m.m02 + m.m32) + offset.x * m.m32) * scale;
			m.m03 = (0.5f * (m.m03 + m.m33) + offset.x * m.m33) * scale;
			m.m10 = (0.5f * (m.m10 + m.m30) + offset.y * m.m30) * scale;
			m.m11 = (0.5f * (m.m11 + m.m31) + offset.y * m.m31) * scale;
			m.m12 = (0.5f * (m.m12 + m.m32) + offset.y * m.m32) * scale;
			m.m13 = (0.5f * (m.m13 + m.m33) + offset.y * m.m33) * scale;
			m.m20 = 0.5f * (m.m20 + m.m30);
			m.m21 = 0.5f * (m.m21 + m.m31);
			m.m22 = 0.5f * (m.m22 + m.m32);
			m.m23 = 0.5f * (m.m23 + m.m33);
			return m;
		}

		Vector2 SetTileViewport(int index, int split, float tileSize)
		{
			Vector2 offset = new Vector2(index % split, index / split);
			buffer.SetViewport(new Rect(
				offset.x * tileSize, offset.y * tileSize, tileSize, tileSize
			));
			return offset;
		}

		void SetKeywords(string[] keywords, int enabledIndex)
		{
			for (int i = 0; i < keywords.Length; i++)
			{
				if (i == enabledIndex)
				{
					buffer.EnableShaderKeyword(keywords[i]);
				}
				else
				{
					buffer.DisableShaderKeyword(keywords[i]);
				}
			}
		}

		void ExecuteBuffer()
		{
			context.ExecuteCommandBuffer(buffer);
			buffer.Clear();
		}
	}
}