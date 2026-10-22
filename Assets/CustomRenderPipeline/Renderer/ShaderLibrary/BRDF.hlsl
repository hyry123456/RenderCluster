#ifndef CUSTOM_BRDF_INCLUDED
#define CUSTOM_BRDF_INCLUDED

struct BRDF {
	float3 diffuse;
	float3 specular;
	float roughness;
};




#define MIN_REFLECTIVITY 0.04


float OneMinusReflectivity (float metallic) {
	float range = 1.0 - MIN_REFLECTIVITY;
	return range - metallic * range;
}

BRDF GetBRDF (Surface surface, bool applyAlphaToDiffuse = false) {
	BRDF brdf;
	float oneMinusReflectivity = OneMinusReflectivity(surface.metallic);

	brdf.diffuse = surface.color * oneMinusReflectivity;
	if (applyAlphaToDiffuse) {
		brdf.diffuse *= surface.alpha;
	}
	brdf.specular = lerp(MIN_REFLECTIVITY, surface.color, surface.metallic);

	brdf.roughness = surface.roughness;
	return brdf;
}


//计算Smith-Joint阴影遮掩函数，返回的是除以镜面反射项分母的可见性项V
inline half ComputeSmithJointGGXVisibilityTerm(half nl, half nv, half roughness)
{
	half ag = roughness * roughness;
	half lambdaV = nl * (nv * (1 - ag) + ag);
	half lambdaL = nv * (nl * (1 - ag) + ag);

	return 0.5f / (lambdaV + lambdaL + 1e-5f);
}
//计算法线分布函数
inline half ComputeGGXTerm(half nh, half roughness)
{
	half a = roughness * roughness;
	half a2 = a * a;
	half d = (a2 - 1.0f) * nh * nh + 1.0f;
	return a2 * INV_PI / (d * d + 1e-5f);
}
//计算菲涅尔
inline half3 ComputeFresnelTerm(half3 F0, half cosA)
{
	return F0 + (1 - F0) * pow(1 - cosA, 5);
}

//反射的漫反射缩放：反射颜色、计算掠射角时反射率、法线和视角方向的夹角
inline half3 ComputeFresnelLerp(half3 c0, half3 c1, half cosA)
{
	half t = pow(1 - cosA, 5);
	return lerp(c0, c1, t);
}

//计算漫反射项
inline half3 ComputeDisneyDiffuseTerm(half nv, half nl, half lh, half roughness, half3 baseColor)
{
	half Fd90 = 0.5f + 2 * roughness * lh * lh;
	return baseColor * INV_PI * (1 + (Fd90 - 1) * pow(1 - nl, 5)) * (1 + (Fd90 - 1) * pow(1 - nv, 5));
}


float3 DirectBRDF (Surface surface, Light light) {
	float3 baseCol = surface.color  * light.color;
	// float3 diff = baseCol  * (1- surface.metallic) / 3.14159265359;

	float3 lightDir = light.direction, viewDir = surface.viewDirection, normal = surface.normal;

	//计算BRDF需要用到一些项
	// 高光数据
	half3 halfDir = normalize(lightDir + viewDir);
	//视线方向与法线方向的余弦值
	half nv = saturate(dot(normal,viewDir));
	//法线方向与灯光方向的余弦值
	half nl = saturate(dot(normal,lightDir));
	//高光数据与世界法线的余弦值
	half nh = saturate(dot(normal,halfDir));
	//灯光方向与视线方向的余弦值
	half lv = saturate(dot(lightDir,viewDir));
	//灯光方向与高光方向的余弦值
	half lh = saturate(dot(lightDir,halfDir));

	//计算镜面反射率,unity_ColorSpaceDielectricSpec是一个固定值，rgb都大概在65的位置，不太确定有什么用
	//菲涅尔效应的F0值，也就是菲涅尔效应的最低值的计算
	half3 specColor = lerp(0.4, baseCol, surface.metallic);

	half V = ComputeSmithJointGGXVisibilityTerm(nl, nv, surface.roughness);//计算BRDF高光反射项，可见性V  这里把分母已经除了
	half D = ComputeGGXTerm(nh, surface.roughness);//计算BRDF高光反射项,法线分布函数D
	half3 F = ComputeFresnelTerm(specColor,lh);//计算BRDF高光反射项，菲涅尔项F
	half3 specularTerm = V * D * F * 1.3f;//计算镜面反射项

	half oneMinusReflectivity = OneMinusReflectivity(surface.metallic);
	//计算漫反射率，实际上F0时的漫反射系数，也就是确定漫反射颜色，如果金属度高，那么就说明漫反射很弱，直接就是值为0
	half3 diffColor = baseCol * oneMinusReflectivity;

	half3 diff = ComputeDisneyDiffuseTerm(nv, nl, lh, surface.roughness, diffColor);

	return PI * (specularTerm + diff) * nl * light.attenuation * surface.ambientOcclusion * light.color;
	// return PI * (specularTerm) * nl * light.attenuation * surface.ambientOcclusion * light.color;
}

float3 DirectBSDF(Surface surface, Light light, float normalDistorion, float power, float width){
	float3 backLightDir = normalize(surface.normal * normalDistorion + light.direction);
	float fLTDot = pow(saturate( dot(surface.viewDirection, -backLightDir)), power ) 
		* light.attenuation * 3;
	// float fLTDot = pow(saturate( dot(surface.normal, -backLightDir)), power ) * light.attenuation * 5;
	float3 transferColor = surface.color * light.color;
	float3 transferCol = fLTDot * transferColor * light.color * light.attenuation * (1.0 - width);

	transferCol = saturate(transferCol);

	float3 baseCol = surface.color  * light.color;	
	float3 diff = baseCol  * (1- surface.metallic) / 3.14159265359;

	float3 lightDir = light.direction, viewDir = surface.viewDirection, normal = surface.normal;

	//计算BRDF需要用到一些项
	// 高光数据
	half3 halfDir = normalize(lightDir + viewDir);
	//视线方向与法线方向的余弦值
	half nv = saturate(dot(normal,viewDir));
	//法线方向与灯光方向的余弦值
	half nl = saturate(dot(normal,lightDir));
	//高光数据与世界法线的余弦值
	half nh = saturate(dot(normal,halfDir));
	//灯光方向与视线方向的余弦值
	half lv = saturate(dot(lightDir,viewDir));
	//灯光方向与高光方向的余弦值
	half lh = saturate(dot(lightDir,halfDir));

	//计算镜面反射率,unity_ColorSpaceDielectricSpec是一个固定值，rgb都大概在65的位置，不太确定有什么用
	//菲涅尔效应的F0值，也就是菲涅尔效应的最低值的计算
	half3 specColor = lerp(0.4, baseCol, surface.metallic);

	half V = ComputeSmithJointGGXVisibilityTerm(nl, nv, surface.roughness);//计算BRDF高光反射项，可见性V  这里把分母已经除了
	half D = ComputeGGXTerm(nh, surface.roughness);//计算BRDF高光反射项,法线分布函数D
	half3 F = ComputeFresnelTerm(specColor,lh);//计算BRDF高光反射项，菲涅尔项F
	half3 specularTerm = V * D * F;//计算镜面反射项	

	return ((specularTerm + diff) * nl ) * light.attenuation + transferCol;
}

float3 DirectBRDF_Spec (Surface surface, Light light) {
	float3 baseCol = surface.color  * light.color;
	// float3 diff = baseCol  * (1- surface.metallic) / 3.14159265359;

	float3 lightDir = light.direction, viewDir = surface.viewDirection, normal = surface.normal;

	//计算BRDF需要用到一些项
	// 高光数据
	half3 halfDir = normalize(lightDir + viewDir);
	//视线方向与法线方向的余弦值
	half nv = saturate(dot(normal,viewDir));
	//法线方向与灯光方向的余弦值
	half nl = saturate(dot(normal,lightDir));
	//高光数据与世界法线的余弦值
	half nh = saturate(dot(normal,halfDir));
	//灯光方向与视线方向的余弦值
	half lv = saturate(dot(lightDir,viewDir));
	//灯光方向与高光方向的余弦值
	half lh = saturate(dot(lightDir,halfDir));

	//计算镜面反射率,unity_ColorSpaceDielectricSpec是一个固定值，rgb都大概在65的位置，不太确定有什么用
	//菲涅尔效应的F0值，也就是菲涅尔效应的最低值的计算
	half3 specColor = lerp(0.4, baseCol, surface.metallic);

	half V = ComputeSmithJointGGXVisibilityTerm(nl, nv, surface.roughness);//计算BRDF高光反射项，可见性V  这里把分母已经除了
	half D = ComputeGGXTerm(nh, surface.roughness);//计算BRDF高光反射项,法线分布函数D
	half3 F = ComputeFresnelTerm(specColor,lh);//计算BRDF高光反射项，菲涅尔项F
	half3 specularTerm = V * D * F * 1.3f;//计算镜面反射项

	return PI * (specularTerm) * nl * light.attenuation * surface.ambientOcclusion * light.color;
	// return PI * (specularTerm) * nl * light.attenuation * surface.ambientOcclusion * light.color;
}

#endif