#ifndef CUSTOMRP_POSTFX_INPUT
#define CUSTOMRP_POSTFX_INPUT

UNITY_INSTANCING_BUFFER_START(PostFX)

    //----------------Bloom----------------------
    bool _BloomBicubicUpsampling;
    float _BloomIntensity;
    float4 _BloomThreshold;
    //----------------Bloom----------------------


    //----------------ColorGrading----------------------
    float4 _ColorAdjustments;
    float4 _ColorFilter;
    float4 _WhiteBalance;
    float4 _SplitToningShadows, _SplitToningHighlights;
    float4 _SMHShadows, _SMHMidtones, _SMHHighlights, _SMHRange;

    float4 _ColorGradingLUTParameters;
    bool _ColorGradingLUTInLogC;
    TEXTURE2D(_ColorGradingLUT);    

    //----------------ColorGrading----------------------

    //---------------------FXAA---------------------
    float _BilaterFilterFactor;

    //---------------------FXAA---------------------


    //---------------Depth Of Field-------------------

    float _FocusDistance;       //聚焦位置
    float _FocusRange;          //Fade范围
    float _BokehRadius;         //角度调整

    //---------------Depth Of Field-------------------

    float4x4 _PreviousViewProjectionMatrix;
    float _BlurRadio;


UNITY_INSTANCING_BUFFER_END(PostFX)

TEXTURE2D(_SourceTexture2);      //临时根据贴图存放位置
TEXTURE2D(_SourceTexture3);      //临时根据贴图存放位置
TEXTURE2D(_RenderCameraDepth);   //摄像机深度图
TEXTURE2D(_AutoExpourseTex);   //自动曝光贴图

float4 GetSourceBicubic (float2 screenUV) {
	return SampleTexture2DBicubic(
		TEXTURE2D_ARGS(_SourceTexture, sampler_linear_clamp), screenUV,
		_SourceTexture_TexelSize.zwxy, 1.0, 0.0
	);
}

float4 GetSource2(float2 screenUV){
    return SAMPLE_TEXTURE2D_LOD(_SourceTexture2, sampler_linear_repeat, screenUV, 0);
}

float4 GetSource3(float2 screenUV){
    return SAMPLE_TEXTURE2D_LOD(_SourceTexture3, sampler_linear_repeat, screenUV, 0);
}

float3 ApplyBloomThreshold (float3 color) {


	float brightness = Max3(color.r, color.g, color.b);
	float soft = brightness + _BloomThreshold.y;
	soft = clamp(soft, 0.0, _BloomThreshold.z);
	soft = soft * soft * _BloomThreshold.w;
	float contribution = max(soft, brightness - _BloomThreshold.x);
	contribution /= max(brightness, 0.00001);



	return color * contribution;
}

//------------------------ColorGrading---------------------------------



float Luminance (float3 color, bool useACES) {
	return useACES ? AcesLuminance(color) : Luminance(color);
}

float3 ColorGradePostExposure (float3 color) {
    float expose = _ColorAdjustments.x;

	return color * expose;
}

float3 ColorGradeWhiteBalance (float3 color) {
	color = LinearToLMS(color);
	color *= _WhiteBalance.rgb;
	return LMSToLinear(color);
}

float3 ColorGradingContrast (float3 color, bool useACES) {
	color = useACES ? ACES_to_ACEScc(unity_to_ACES(color)) : LinearToLogC(color);
	color = (color - ACEScc_MIDGRAY) * _ColorAdjustments.y + ACEScc_MIDGRAY;
	return useACES ? ACES_to_ACEScg(ACEScc_to_ACES(color)) : LogCToLinear(color);
}

float3 ColorGradeColorFilter (float3 color) {
	return color * _ColorFilter.rgb;
}

float3 ColorGradingHueShift (float3 color) {
	color = RgbToHsv(color);
	float hue = color.x + _ColorAdjustments.z;
	color.x = RotateHue(hue, 0.0, 1.0);
	return HsvToRgb(color);
}

float3 ColorGradingSaturation (float3 color, bool useACES) {
	float luminance = Luminance(color, useACES);
	return (color - luminance) * _ColorAdjustments.w + luminance;
}

float3 ColorGradeSplitToning (float3 color, bool useACES) {
	color = PositivePow(color, 1.0 / 2.2);
	float t = saturate(Luminance(saturate(color), useACES) + _SplitToningShadows.w);
	float3 shadows = lerp(0.5, _SplitToningShadows.rgb, 1.0 - t);
	float3 highlights = lerp(0.5, _SplitToningHighlights.rgb, t);
	color = SoftLight(color, shadows);
	color = SoftLight(color, highlights);
	return PositivePow(color, 2.2);
}

float3 ColorGradingShadowsMidtonesHighlights (float3 color, bool useACES) {
	float luminance = Luminance(color, useACES);
	float shadowsWeight = 1.0 - smoothstep(_SMHRange.x, _SMHRange.y, luminance);
	float highlightsWeight = smoothstep(_SMHRange.z, _SMHRange.w, luminance);
	float midtonesWeight = 1.0 - shadowsWeight - highlightsWeight;
	return
		color * _SMHShadows.rgb * shadowsWeight +
		color * _SMHMidtones.rgb * midtonesWeight +
		color * _SMHHighlights.rgb * highlightsWeight;
}

float3 ColorGrade (float3 color, bool useACES = false) {
	color = ColorGradePostExposure(color);
	color = ColorGradeWhiteBalance(color);
	color = ColorGradingContrast(color, useACES);
	color = ColorGradeColorFilter(color);
	color = max(color, 0.0);
	color =	ColorGradeSplitToning(color, useACES);
	color = max(color, 0.0);
	color = ColorGradingShadowsMidtonesHighlights(color, useACES);
	color = ColorGradingHueShift(color);
	color = ColorGradingSaturation(color, useACES);
	return max(useACES ? ACEScg_to_ACES(color) : color, 0.0);
}



float3 GetColorGradedLUT (float2 uv, bool useACES = false) {
	float3 color = GetLutStripValue(uv, _ColorGradingLUTParameters);
	return ColorGrade(_ColorGradingLUTInLogC ? LogCToLinear(color) : color, useACES);
}

float3 ApplyColorGradingLUT (float3 color) {
	return ApplyLut2D(
		TEXTURE2D_ARGS(_ColorGradingLUT, sampler_linear_clamp),
		saturate(_ColorGradingLUTInLogC ? LinearToLogC(color) : color),
		_ColorGradingLUTParameters.xyz
	);
}

//------------------------ColorGrading---------------------------------


//----------------FFX----------------------


float CompareColor(float4 col1, float4 col2)
{
	float l1 = Luminance(col1.rgb);
	float l2 = Luminance(col2.rgb);
	return smoothstep(_BilaterFilterFactor, 1.0, 1.0 - abs(l1 - l2));
}

float SampleLuminance (float2 uv) {
    #if defined(LUMINANCE_GREEN)
        return GetSource(uv).g;
    #else
        return GetSource(uv).a;
    #endif
}

float SampleLuminance (float2 uv, float uOffset, float vOffset) {
    uv += GetSourceTexelSize().xy * float2(uOffset, vOffset);
    return SampleLuminance(uv);
}

struct LuminanceData {
    float m, n, e, s, w;
    float ne, nw, se, sw;
    float highest, lowest, contrast;
};
float _ContrastThreshold, _RelativeThreshold;
float _SubpixelBlending;

LuminanceData SampleLuminanceNeighborhood (float2 uv) {
    LuminanceData l;
    l.m = SampleLuminance(uv);			//中间
    //上下左右
    l.n = SampleLuminance(uv, 0,  1);
    l.e = SampleLuminance(uv, 1,  0);
    l.s = SampleLuminance(uv, 0, -1);
    l.w = SampleLuminance(uv,-1,  0);

    //东南、东北方向等四个方向
    l.ne = SampleLuminance(uv,  1,  1);
    l.nw = SampleLuminance(uv, -1,  1);
    l.se = SampleLuminance(uv,  1, -1);
    l.sw = SampleLuminance(uv, -1, -1);

    l.highest = max(max(max(max(l.n, l.e), l.s), l.w), l.m);	//最高亮度
    l.lowest = min(min(min(min(l.n, l.e), l.s), l.w), l.m);		//最低亮度
    l.contrast = l.highest - l.lowest;		//获得最高亮度与最低亮度之间的差
    return l;
}
		
//根据传入的阈值以及高度影响值，判断是否应该跳过该像素，不进行FXAA
bool ShouldSkipPixel (LuminanceData l) {
    float threshold =
        max(_ContrastThreshold, _RelativeThreshold * l.highest);
    return l.contrast < threshold;
}

//确定混合因子, 这个只控制边界的直接混合模式, 与边框的混合是独立的
float DeterminePixelBlendFactor (LuminanceData l) {
    float filter = 2 * (l.n + l.e + l.s + l.w);		//上下左右占比
    filter += l.ne + l.nw + l.se + l.sw;			//四边占比小一点
    filter *= 1.0 / 12;
    filter = abs(filter - l.m);			//混合因子由自身以及四周的对比度差决定
    filter = saturate(filter / l.contrast);		//根据对比度差进行归一化,不然进过上面变太小了
    float blendFactor = smoothstep(0, 1, filter);
    return blendFactor * blendFactor * _SubpixelBlending;
}

struct EdgeData {
    bool isHorizontal;
    float pixelStep;
    float oppositeLuminance, gradient;	//沿着边
};

//确定边缘，因为不是直接周围模糊，因此需要根据四周的灰度值判断可能的模糊朝向
//这里为了更好的效果，使用了四周采用，而不是直接十字
EdgeData DetermineEdge (LuminanceData l) {
    EdgeData e;
    float horizontal =
        abs(l.n + l.s - 2 * l.m) * 2 +
        abs(l.ne + l.se - 2 * l.e) +
        abs(l.nw + l.sw - 2 * l.w);
    float vertical =
        abs(l.e + l.w - 2 * l.m) * 2 +
        abs(l.ne + l.nw - 2 * l.n) +
        abs(l.se + l.sw - 2 * l.s);
    //检查水平还是垂直的亮度差大，大的就是要模糊的方向
    e.isHorizontal = horizontal >= vertical;

    //左右选一边
    float pLuminance = e.isHorizontal ? l.n : l.e;
    //上下选一边
    float nLuminance = e.isHorizontal ? l.s : l.w;
    
    //判断与中间的差
    float pGradient = abs(pLuminance - l.m);
    float nGradient = abs(nLuminance - l.m);

    //获得像素尺寸，比较等会是单边模糊，而上下的每一个像素数量是不一致的
    e.pixelStep =
        e.isHorizontal ? GetSourceTexelSize().y : GetSourceTexelSize().x;

    //最后确定偏移的方向，是正方向还是负方向，比较上面只判断了水平还是垂直
    if (pGradient < nGradient) {
        e.pixelStep = -e.pixelStep;
        e.oppositeLuminance = nLuminance;	//偏移值就是差值
        e.gradient = nGradient;		//存储上下的差，之后需要朝这边偏移
    }
    else {
        e.oppositeLuminance = pLuminance;
        e.gradient = pGradient;
    }
    return e;
}

#if defined(LOW_QUALITY)
    #define EDGE_STEP_COUNT 4
    #define EDGE_STEPS 1, 1.5, 2, 4
    #define EDGE_GUESS 12
#else
    #define EDGE_STEP_COUNT 10
    #define EDGE_STEPS 1, 1.5, 2, 2, 2, 2, 2, 2, 2, 4
    #define EDGE_GUESS 8
#endif

static const float edgeSteps[EDGE_STEP_COUNT] = { EDGE_STEPS };

float DetermineEdgeBlendFactor (LuminanceData l, EdgeData e, float2 uv) {
    float2 uvEdge = uv;
    float2 edgeStep;
    //目标是沿着边走，但是并没有必要每一个都这么做，因此对移动方向的另一边偏移，而且只移动一步，采集中间的平均值
    if (e.isHorizontal) {
        uvEdge.y += e.pixelStep * 0.5;
        edgeStep = float2(GetSourceTexelSize().x, 0);
    }
    else {
        uvEdge.x += e.pixelStep * 0.5;
        edgeStep = float2(0, GetSourceTexelSize().y);
    }

    float edgeLuminance = (l.m + e.oppositeLuminance) * 0.5;	//平均值
    float gradientThreshold = e.gradient * 0.25;	
    
    float2 puv = uvEdge + edgeStep * edgeSteps[0];	//进行偏移
    float pLuminanceDelta = SampleLuminance(puv) - edgeLuminance;
    bool pAtEnd = abs(pLuminanceDelta) >= gradientThreshold;		//如果大于，就是到达边缘了

    //持续循环，查找边缘，且使用展开循环指令，优化性能
    int i;
    UNITY_UNROLL
    for (i = 1; i < EDGE_STEP_COUNT && !pAtEnd; i++) {
        puv += edgeStep * edgeSteps[i];
        pLuminanceDelta = SampleLuminance(puv) - edgeLuminance;
        pAtEnd = abs(pLuminanceDelta) >= gradientThreshold;
    }
    if (!pAtEnd) {
        puv += edgeStep * EDGE_GUESS;
    }

    float2 nuv = uvEdge - edgeStep * edgeSteps[0];
    float nLuminanceDelta = SampleLuminance(nuv) - edgeLuminance;
    bool nAtEnd = abs(nLuminanceDelta) >= gradientThreshold;

    // UNITY_UNROLL
    for (i = 1; i < EDGE_STEP_COUNT && !nAtEnd; i++) {
        nuv -= edgeStep * edgeSteps[i];
        nLuminanceDelta = SampleLuminance(nuv) - edgeLuminance;
        nAtEnd = abs(nLuminanceDelta) >= gradientThreshold;
    }
    if (!nAtEnd) {
        nuv -= edgeStep * EDGE_GUESS;
    }

    float pDistance, nDistance;
    if (e.isHorizontal) {
        pDistance = puv.x - uv.x;
        nDistance = uv.x - nuv.x;
    }
    else {
        pDistance = puv.y - uv.y;
        nDistance = uv.y - nuv.y;
    }
    
    float shortestDistance;
    bool deltaSign;
    if (pDistance <= nDistance) {
        shortestDistance = pDistance;
        deltaSign = pLuminanceDelta >= 0;
    }
    else {
        shortestDistance = nDistance;
        deltaSign = nLuminanceDelta >= 0;
    }

    if (deltaSign == (l.m - edgeLuminance >= 0)) {
        return 0;
    }
    return 0.5 - shortestDistance / (pDistance + nDistance);
}

float4 ApplyFXAA (in float2 uv) {
    LuminanceData l = SampleLuminanceNeighborhood(uv);
    if (ShouldSkipPixel(l))		//跳过不需要的像素
        return GetSource(uv);

    float pixelBlend = DeterminePixelBlendFactor(l);
    EdgeData e = DetermineEdge(l);

    float edgeBlend = DetermineEdgeBlendFactor(l, e, uv);
    float finalBlend = max(pixelBlend, edgeBlend);

    // return finalBlend;

    if (e.isHorizontal) {
        uv.y += e.pixelStep * finalBlend;
    }
    else {
        uv.x += e.pixelStep * finalBlend;
    }
    return float4(GetSource(uv).rgb, l.m);
}
//----------------FFX----------------------


//---------------Depth Of Field-------------------

half Weigh (half3 c) {
    return 1 / (1 + max(max(c.r, c.g), c.b));
}

half Weigh (half coc, half radius) {
    return saturate((coc - radius + 2) / 2);
}

#define BOKEH_KERNEL_MEDIUM

#if defined(BOKEH_KERNEL_SMALL)
    static const int kernelSampleCount = 16;
    static const float2 kernel[kernelSampleCount] = {
        float2(0, 0),
        float2(0.54545456, 0),
        float2(0.16855472, 0.5187581),
        float2(-0.44128203, 0.3206101),
        float2(-0.44128197, -0.3206102),
        float2(0.1685548, -0.5187581),
        float2(1, 0),
        float2(0.809017, 0.58778524),
        float2(0.30901697, 0.95105654),
        float2(-0.30901703, 0.9510565),
        float2(-0.80901706, 0.5877852),
        float2(-1, 0),
        float2(-0.80901694, -0.58778536),
        float2(-0.30901664, -0.9510566),
        float2(0.30901712, -0.9510565),
        float2(0.80901694, -0.5877853),
    };
#elif defined (BOKEH_KERNEL_MEDIUM)
    static const int kernelSampleCount = 22;
    static const float2 kernel[kernelSampleCount] = {
        float2(0, 0),
        float2(0.53333336, 0),
        float2(0.3325279, 0.4169768),
        float2(-0.11867785, 0.5199616),
        float2(-0.48051673, 0.2314047),
        float2(-0.48051673, -0.23140468),
        float2(-0.11867763, -0.51996166),
        float2(0.33252785, -0.4169769),
        float2(1, 0),
        float2(0.90096885, 0.43388376),
        float2(0.6234898, 0.7818315),
        float2(0.22252098, 0.9749279),
        float2(-0.22252095, 0.9749279),
        float2(-0.62349, 0.7818314),
        float2(-0.90096885, 0.43388382),
        float2(-1, 0),
        float2(-0.90096885, -0.43388376),
        float2(-0.6234896, -0.7818316),
        float2(-0.22252055, -0.974928),
        float2(0.2225215, -0.9749278),
        float2(0.6234897, -0.7818316),
        float2(0.90096885, -0.43388376),
    };
#endif

//---------------Depth Of Field-------------------

//depth是深度图深度
float3 GetWorldPos(float depth, float2 uv){
    #if defined(UNITY_REVERSED_Z)
        depth = 1 - depth;
    #endif
	float4 ndc = float4(uv.x * 2 - 1, uv.y * 2 - 1, depth * 2 - 1, 1);

	float4 worldPos = mul(_InverseVPMatrix, ndc);
	worldPos /= worldPos.w;
	return worldPos.xyz;
}

#endif
