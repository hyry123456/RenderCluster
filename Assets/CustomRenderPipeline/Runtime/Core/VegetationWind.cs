using UnityEngine;

/// <summary>
/// 把场景里 <see cref="WindZone"/> 的参数推到全局 shader 属性，供植被 shader（CustomRP/Defferer/Vegetation）
/// 在顶点里做风吹位移。
///
/// 为什么自己推：Unity 内置的 WindZone 只有 Terrain 的草/树会自动吃到（走 TerrainEngine 内部通路），
/// 普通 MeshRenderer 拿不到。而 WindZone 的参数本身是「场景里美术调的」那一份，
/// 自己读出来再推给 shader，既能复用美术已经在编辑器里调好的数值，也让风吹强度有唯一来源。
///
/// 推两个全局量（与 Vegetation.shader 顶部的声明逐字对应）：
///   _VegetationWind      = (dirX, dirY, dirZ, strength 0~1.5)
///   _VegetationWindParams= (turbulence 0~1, pulse 0~1, time 秒, phaseScatter)
/// </summary>
public static class VegetationWind
{
    static readonly int WindId = Shader.PropertyToID("_VegetationWind");
    static readonly int ParamsId = Shader.PropertyToID("_VegetationWindParams");

    /// <summary>场景里当前生效的 WindZone。缓存在这里，避免每帧 FindObjectsByType。</summary>
    static WindZone s_zone;
    static int s_lastScanFrame = -9999;
    /// <summary>重扫间隔（帧）。风区基本是静态的，偶尔扫一次足够应付运行时创建/销毁。</summary>
    const int ScanIntervalFrames = 120;

    /// <summary>相位的散列尺度：世界 xz 上每米相差多少相位。太大整片同步，太小会碎成噪点。</summary>
    const float PhaseScatter = 1.6f;

    public static void Update()
    {
        RefreshZoneIfNeeded();

        Vector3 dir;
        float windMain, turbulence, pulseMag, pulseFreq;

        if (s_zone != null)
        {
            // Directional 模式下 Unity 的风向就是 transform.forward（Spherical 模式方向无关，这里也用它）
            dir = s_zone.transform.forward;
            windMain = s_zone.windMain;
            turbulence = s_zone.windTurbulence;
            pulseMag = s_zone.windPulseMagnitude;
            pulseFreq = s_zone.windPulseFrequency;
        }
        else
        {
            // 场景里没有 WindZone：给一个很轻的默认风，免得草完全静止像纸片（视觉验收用，不影响正确性）
            dir = new Vector3(1f, 0f, 0.35f);
            windMain = 0.015f;
            turbulence = 0.8f;
            pulseMag = 0.4f;
            pulseFreq = 0.01f;
        }

        // pulse：WindZone 用它做「一阵一阵」的包络。Frequency 单位是次/秒，转成角频率。
        float pulse = 1f;
        if (pulseMag > 0f && pulseFreq > 0f)
            pulse = 1f + Mathf.Sin(Time.time * pulseFreq * 6.2831853f) * pulseMag;

        // windMain 的绝对值很小（本场景 0.025），归一成 0~1.5 的强度再交给 shader，
        // 这样 shader 里的 _WindScale / _WindBendMax 是「相对倍数 vs 绝对米数」的分工。
        float strength = Mathf.Clamp01(windMain * 8f + turbulence * 0.15f) * Mathf.Max(0f, pulse);
        strength = Mathf.Min(strength, 1.5f);

        dir.y = 0f;                                  // 草只做水平摆动，纵向位移会陷进/浮出地面
        if (dir.sqrMagnitude < 1e-6f) dir = Vector3.right;
        dir.Normalize();

        Shader.SetGlobalVector(WindId, new Vector4(dir.x, dir.y, dir.z, strength));
        Shader.SetGlobalVector(ParamsId, new Vector4(
            Mathf.Clamp01(turbulence),
            Mathf.Clamp01(pulseMag),
            Time.time,
            PhaseScatter));
    }

    static void RefreshZoneIfNeeded()
    {
        if (s_lastScanFrame >= 0 && Time.frameCount - s_lastScanFrame < ScanIntervalFrames) return;
        s_lastScanFrame = Time.frameCount;

        s_zone = null;
        var zones = Object.FindObjectsByType<WindZone>(FindObjectsSortMode.None);
        for (int i = 0; i < zones.Length; i++)
        {
            // WindZone 继承 Component（不是 Behaviour），没有 isActiveAndEnabled，只能看 GameObject
            if (zones[i] != null && zones[i].gameObject.activeInHierarchy) { s_zone = zones[i]; break; }
        }
    }
}
