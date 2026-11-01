using System.Collections.Generic;
using Cluster;
using UnityEngine;

/// <summary>
/// 把场景里的 Unity <see cref="Terrain"/> 接到本工程的延迟 GBuffer 上，并可选地让它走 RVT 图集。
///
/// 为什么需要它：本管线只认 `OutGBuffer` / `CustomLit` / `CustomLitAdd` 三种 LightMode。
/// Terrain 组件默认用 Unity 内建地形材质（没有这些 pass）⇒ 地形根本不会进 GBuffer，
/// 表现就是「地形不画 / 只有一处奇怪的边缘」。给它换一个带 `OutGBuffer` 的材质模板即可，
/// 之后地形与其它不透明物体走完全相同的延迟光照（标准 BRDF）。
///
/// 地形走的是【SRP 路径】：它在 `DefferPipeline.DrawGBuffer` 的 `context.DrawRenderers`
/// 里被画出来，而这一次调用【在簇绘制之前】—— 地形正好是最先写深度的那批几何，
/// 后面簇的 DrawProceduralIndirect 能吃 early-Z。不需要为它调整任何流程。
///
/// ===== RVT =====
/// 资产里 `UseRVT` 开启时，把每层的 diffuse/normal 注册成图集里的 texId（_TerrainMapId0..3），
/// 并给地形材质开 `_TERRAIN_RVT` 关键字 ⇒ shader 从图集采样，页由 4 个 TerrainFeedbackN
/// pass 上报（见 TerrainLit.shader）。
/// 运行时每个 Terrain 单独一份材质：不同地形有不同 TerrainLayer，_TerrainMapIdN 不能共用。
///
/// ⚠️ 会顺手关掉 `Terrain.drawInstanced`：开着它 Unity 会用 instanced terrain 变体
/// （需要 shader 里 `_TERRAIN_INSTANCED_PERPIXEL_NORMAL` 那套宏），本 shader 没有那条通路，
/// 开着会画错/不画。
///
/// ⚠️ 地形只在【SRP 路径】画。簇路径只吃 MeshRenderer + ClusterObject，Terrain 不是它的输入；
/// 所以 Terrain 走 SRP、其余静态物体走簇，是本工程的预期形态（互不冲突，也不重复绘制）。
/// 唯一副作用：`clusterMode = Cluster` 时地形【不投影】（簇阴影只画簇，SRP 阴影那段按
/// renderingLayerMask 只挑镂空物体，而 Terrain 没有 renderingLayerMask 字段）。
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class TerrainRenderSetup : MonoBehaviour
{
    [Tooltip("留空则用 Shader.Find 自动建 CustomRP/Defferer/TerrainLit 材质。")]
    public Material materialTemplate;

    [Tooltip("关掉后不接管任何 Terrain（用 Unity 自己的地形材质）。")]
    public bool applyOnEnable = true;

    const string TerrainShaderName = "CustomRP/Defferer/TerrainLit";
    const int MaxLayers = 4;                 // 一张 _Control 的 RGBA

    class TerrainSlot
    {
        public Terrain terrain;
        public Material material;
        public TerrainLayer[] layers;        // 分配 texId 时的图层快照（用于检测变化）
        public int[] texIds;                 // -1 = 未分配
    }

    readonly List<TerrainSlot> m_slots = new List<TerrainSlot>();
    readonly HashSet<Material> m_owned = new HashSet<Material>();   // 只销毁自己 new 出来的
    Material m_mat;

    // 场景里是否存在「已接管的运行时地形」。ClusterCore 用它决定要不要每帧多发
    // 4 次 DrawRenderers（地形 RVT 反馈）。没有地形的场景不该付这份钱。
    static int s_active;
    public static int ActiveCount => s_active;
    bool m_reportedActive;

    void SetActiveFlag(bool on)
    {
        if (on == m_reportedActive) return;
        m_reportedActive = on;
        s_active += on ? 1 : -1;
    }

    // 分配 texId 时的页表实例。管线重建（重新 Play / Dispose）后实例会变，
    // 旧的 texId 全部失效 ⇒ 必须重新分配。
    TextureNodesMap m_allocMap;

    static readonly int IdUseRVT = Shader.PropertyToID("_UseRVT");
    static readonly int IdVirtualAlbedo = Shader.PropertyToID("_VirtualAlbedo");
    static readonly int IdVirtualNormal = Shader.PropertyToID("_VirtualNormal");
    static readonly int IdVirtualMask = Shader.PropertyToID("_VirtualMask");
    static readonly int[] IdMapId = {
        Shader.PropertyToID("_TerrainMapId0"), Shader.PropertyToID("_TerrainMapId1"),
        Shader.PropertyToID("_TerrainMapId2"), Shader.PropertyToID("_TerrainMapId3"),
    };

    void OnEnable()
    {
        if (applyOnEnable) Apply();
    }

    /// <summary>手动重新扫一遍场景（运行时新建了 Terrain 时用）。</summary>
    [ContextMenu("Apply To All Terrains")]
    public void Apply()
    {
        var template = ResolveMaterial();
        if (template == null) return;

        // 只用启用中的 Terrain（被关掉的不该被接管，与 ClusterShadowRenderer 的扫描同一口径）
        var terrains = FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        if (!Application.isPlaying)
        {
            // 编辑期共用资产材质：运行时 new 出来的材质不能被写进场景的序列化字段，
            // 否则存盘后变成一个空引用（域重载就丢）。编辑期也没有 RVT 页表。
            for (int i = 0; i < terrains.Length; i++)
            {
                if (terrains[i] == null) continue;
                if (terrains[i].materialTemplate != template) terrains[i].materialTemplate = template;
                if (terrains[i].drawInstanced) terrains[i].drawInstanced = false;
            }
            return;
        }

        // 回收已不存在的
        for (int i = m_slots.Count - 1; i >= 0; i--)
        {
            var s = m_slots[i];
            if (s.terrain == null || System.Array.IndexOf(terrains, s.terrain) < 0)
            {
                DestroyOwned(s.material);
                m_slots.RemoveAt(i);
            }
        }

        for (int i = 0; i < terrains.Length; i++)
        {
            var t = terrains[i];
            if (t == null) continue;

            var slot = m_slots.Find(x => x.terrain == t);
            if (slot == null)
            {
                slot = new TerrainSlot
                {
                    terrain = t,
                    material = NewOwnedMaterial(template, t.name),
                    texIds = NewTexIds(),
                };
                m_slots.Add(slot);
            }
            else if (slot.material == null)
            {
                slot.material = NewOwnedMaterial(template, t.name);
            }

            if (t.materialTemplate != slot.material) t.materialTemplate = slot.material;
            if (t.drawInstanced) t.drawInstanced = false;
        }

        SetActiveFlag(m_slots.Count > 0);
    }

    void Update()
    {
        // RVT 的页表 / 图集只在运行时存在（ClusterCore 在渲染管线里创建）
        if (Application.isPlaying)
        {
            SyncRVT();
            // 每帧兜底：防止「某个地形在 Apply 之后才启用 / 刚实例化 / 被别的脚本把
            // materialTemplate 重置回默认」而漏网。漏网的表现正是：GBuffer 不画它
            // （默认地形材质没有 OutGBuffer pass），但默认材质自带 ShadowCaster ⇒ 阴影照常画。
            // 一旦检测到就立刻接管，并用 LogWarning 报出地形名字，方便定位「到底哪个没挂上 SRP」。
            EnsureAllTerrains();
        }
    }

    /// <summary>每帧保险：确保所有启用中的地形都拿到了 TerrainLit 材质。
    /// 设计上 Apply() 是全局一次性替换，但地形可能在 Apply 之后才启用 / 被实例化 / 被重置，
    /// 任何一条都会让这块地形「阴影在、GBuffer 不在」。这里兜底 + 点名。</summary>
    void EnsureAllTerrains()
    {
        var template = ResolveMaterial();
        if (template == null) return;
        var terrains = FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var t in terrains)
        {
            if (t == null) continue;
            bool wrong = t.materialTemplate == null
                || t.materialTemplate.shader == null
                || t.materialTemplate.shader.name != TerrainShaderName;
            if (!wrong) continue;
            Debug.LogWarning($"[Terrain] 地形 “{t.name}” 的 materialTemplate 不是 {TerrainShaderName}（当前={(t.materialTemplate ? t.materialTemplate.shader?.name : "null")}）→ GBuffer 不会画它但阴影仍在。已自动接管。");
            if (t.materialTemplate != template) t.materialTemplate = template;
            if (t.drawInstanced) t.drawInstanced = false;
        }
    }

    /// <summary>把每层的 diffuse/normal 注册进 RVT，并把图集/texId 灌到材质上。</summary>
    void SyncRVT()
    {
        if (m_slots.Count == 0) return;

        var asset = ClusterControl.TryGetAssembleAsset();
        if (asset == null) return;

        bool useRVT = asset.UseRVT;
        var map = useRVT ? (TextureNodesMap.Instance ?? TextureNodesMap.CreateInstance()) : null;

        // 页表实例换了（重新 Play / 管线重建）⇒ 之前分配的 texId 全部作废
        if (map != m_allocMap)
        {
            m_allocMap = map;
            for (int i = 0; i < m_slots.Count; i++)
            {
                m_slots[i].texIds = NewTexIds();
                m_slots[i].layers = null;
            }
        }

        bool atlasReady = ClusterObject.TryGetAtlas(out var atlasAlbedo, out var atlasNormal, out var atlasMask);

        for (int i = 0; i < m_slots.Count; i++)
        {
            var slot = m_slots[i];
            var mat = slot.material;
            if (mat == null) continue;

            if (!useRVT || map == null)
            {
                mat.SetFloat(IdUseRVT, 0f);
                mat.DisableKeyword("_TERRAIN_RVT");
                continue;
            }

            var data = slot.terrain != null ? slot.terrain.terrainData : null;
            var layers = data != null ? data.terrainLayers : null;
            if (!SameLayers(slot.layers, layers)) AllocateLayers(map, slot, layers);

            // 图集纹理必须绑成【材质属性】才能让 sampler_Virtual* 吃到 anisoLevel=8，
            // 否则地形这种大平面在掠射角会明显糊。
            if (atlasReady)
            {
                mat.SetTexture(IdVirtualAlbedo, atlasAlbedo);
                mat.SetTexture(IdVirtualNormal, atlasNormal);
                mat.SetTexture(IdVirtualMask, atlasMask);
            }

            // 图集或 texId 还没就绪时【不要】开 RVT 关键字：那时采到的是默认白图，
            // 地形会闪一帧全白。等一切就绪再切。
            bool ready = atlasReady && HasAnyTexId(slot.texIds);
            mat.SetFloat(IdUseRVT, ready ? 1f : 0f);
            if (ready) mat.EnableKeyword("_TERRAIN_RVT");
            else mat.DisableKeyword("_TERRAIN_RVT");
        }
    }

    static bool HasAnyTexId(int[] ids)
    {
        if (ids == null) return false;
        for (int i = 0; i < ids.Length; i++) if (ids[i] >= 0) return true;
        return false;
    }

    void AllocateLayers(TextureNodesMap map, TerrainSlot slot, TerrainLayer[] layers)
    {
        // 旧的 texId 不回收：TextureNodesMap.RemoveTerrainId 不清理 textureMap 字典，
        // 之后同一组贴图再申请会命中已被置 null 的槽位 ⇒ NullReferenceException。
        // 图层改动属于编辑期行为（重新 Play 就会重建页表），这里选择泄漏几个槽位换取安全。
        slot.layers = layers;
        slot.texIds = NewTexIds();

        int n = layers == null ? 0 : Mathf.Min(layers.Length, MaxLayers);
        for (int i = 0; i < n; i++)
        {
            var layer = layers[i];
            if (layer == null) continue;
            // mask/emission 传 null：图集里对应通道填白/黑。地形不用它们 ——
            // 金属度/光滑度来自 TerrainLayer 的标量（_MetallicN / _SmoothnessN）。
            var node = new TextureMapNode(layer.diffuseTexture, layer.normalMapTexture, null, null);
            slot.texIds[i] = map.AllocateTextureId(node);
        }

        for (int i = 0; i < MaxLayers; i++)
        {
            if (slot.material != null && slot.texIds[i] >= 0)
                slot.material.SetFloat(IdMapId[i], slot.texIds[i]);
        }
    }

    static bool SameLayers(TerrainLayer[] a, TerrainLayer[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static int[] NewTexIds()
    {
        var ids = new int[MaxLayers];
        for (int i = 0; i < ids.Length; i++) ids[i] = -1;
        return ids;
    }

    Material ResolveMaterial()
    {
        if (materialTemplate != null) return materialTemplate;
        if (m_mat != null) return m_mat;

        var sh = Shader.Find(TerrainShaderName);
        if (sh == null)
        {
            Debug.LogError($"[Terrain] 找不到 shader：{TerrainShaderName}（地形会退回内建材质，不进 GBuffer）");
            return null;
        }
        m_mat = new Material(sh) { name = "TerrainLit (runtime)" };
        m_owned.Add(m_mat);
        return m_mat;
    }

    Material NewOwnedMaterial(Material template, string terrainName)
    {
        var m = new Material(template) { name = template.name + " (" + terrainName + ")" };
        m_owned.Add(m);
        return m;
    }

    void DestroyOwned(Material m)
    {
        if (m == null) return;
        if (!m_owned.Remove(m)) return;      // 资产材质（用户手动指定的）不动
        if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
    }

    void OnDisable()
    {
        SetActiveFlag(false);
        for (int i = 0; i < m_slots.Count; i++) DestroyOwned(m_slots[i].material);
        m_slots.Clear();
        m_allocMap = null;

        foreach (var m in new List<Material>(m_owned)) DestroyOwned(m);
        m_mat = null;
    }
}
