Shader "CustomRP/Skybox/Skybox"
{
    Properties
    {
        [Main(Base_Sky_Setting, _, off, off)]
        _group ("Base Sky Setting", float) = 0
        [SubToggle(Base_Sky_Setting, _DRAW_SUN_MOON)] _DrawSunMoon("Draw sun and moon", Float) = 0
        [Space()]
		[Sub(Base_Sky_Setting)] _DayColor("Day Base Color", Color) = (.02, .03, .05, 1.0)
		[Sub(Base_Sky_Setting)] _NightColor("Night Base Color", Color) = (.0, .0, .0, 1.0)
        [Sub(Base_Sky_Setting)] _GroundColor("Ground Color", Color) = (.17, .16, .15, 1)
        [Sub(Base_Sky_Setting)] _AtmosphereThickness ("Atmosphere Thickness", Range(0,5)) = 1.0
        [Sub(Base_Sky_Setting)] [HDR]_SkyTint ("Sky Tint", Color) = (2.1, 4.5, 6, 1)
        [Sub(Base_Sky_Setting)] _Exposure("Exposure", Range(0, 8)) = 1.3

        [Main(Sun_Moon, _, off, off)]
        _group1 ("Sun And Moon Setting", float) = 0
        [Space()]
        [Sub(Sun_Moon)] _SunSize ("Sun Size", Range(0,1)) = 0.04
        [Sub(Sun_Moon)] _SunSizeConvergence("Sun Size Convergence", Range(1,10)) = 3
        [Sub(Sun_Moon)] [HDR]_MoonColor("Moon Color", Color) = (.17, .16, .15, 1)
        [Sub(Sun_Moon)] _MoonOffset("Moon Offset", Range(-0.5, 0.5)) = -0.17
        [Sub(Sun_Moon)] _MoonMaskRadius("Moon Mask Radius", Range(0, 0.1)) = 0.03


        [Main(Star_Setting, _, off, off)]
        _group2 ("Stars Setting", float) = 0
        [Space()]
        [Sub(Star_Setting)] _BlendStep ("Blend Step", Range(0, 1.0)) = 0.5
		[Sub(Star_Setting)] _Stars("Stars Texture", 2D) = "black" {}
		[Sub(Star_Setting)] _StarsCutoff("Stars Cutoff",  Range(0.01, 1)) = 0.1
		[Sub(Star_Setting)] _StarsSpeed("Stars Move Speed",  Range(-10, 10)) = 0.3
		[Sub(Star_Setting)] _StarsFrequency("Star Frequency",  Range(0, 5)) = 1.8
		[Sub(Star_Setting)] [HDR]_StarsColor("Stars Color", Color) = (0.0,1,0.5,1)

        [Main(Cloud_Setting, _, off, off)]
        _group3 ("Cloud Setting", float) = 0
        [Space()]
		[Sub(Cloud_Setting)] _Cloud("Cloud Texture", 2D) = "black" {}
		[Sub(Cloud_Setting)] _CloudCutoff("Cloud Cutoff",  Range(0.01, 1)) = 0.3
		[Sub(Cloud_Setting)] _CloudSpeed("Cloud Move Speed",  Range(-10, 10)) = -1
		[Sub(Cloud_Setting)] _CloudFrequency("Cloud Frequency",  Range(0, 3)) = 0.4
		[Sub(Cloud_Setting)] _CloudDayColor("Cloud Day Color", Color) = (0.26, 0.6, 0.5, 1)
		[Sub(Cloud_Setting)] _CloudNightColor("Cloud Night Color", Color) = (0.01, 0.2, 0.22, 1)
		[Sub(Cloud_Setting)] _DistortScale("Distort Noise Scale",  Range(0, 1)) = 0.4
		[Sub(Cloud_Setting)] _DistortionSpeed("Distortion Speed",  Range(-1, 1)) = 0.5
		[Sub(Cloud_Setting)] _CloudNoiseScale("Cloud Noise Scale",  Range(0, 1)) = 0.2
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox"}
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma shader_feature _DRAW_SUN_MOON

            #include "../../../ShaderLibrary/Common.hlsl"
            #include "../../../ShaderLibrary/Surface.hlsl"
            #include "../../../ShaderLibrary/Shadows.hlsl"
            #include "../../../ShaderLibrary/Light.hlsl"
            // #include "../Input/SkyboxInput.hlsl"

            float4 _DayColor;
            float4 _NightColor;
            float4 _GroundColor;
            float4 _SkyTint;
            float _AtmosphereThickness;
            float _Exposure;
            float _SunSize;
            float _SunSizeConvergence;

            float _MoonOffset;
            float _MoonMaskRadius;
            float4 _MoonColor;

            #define OUTER_RADIUS 1.025
            static const float _OuterRadius = OUTER_RADIUS;
            static const float _OuterRadius2 = OUTER_RADIUS*OUTER_RADIUS;
            static const float _InnerRadius = 1.0;
            static const float _InnerRadius2 = 1.0;

            static const float _CameraHeight = 0.0001;

            static const float _ScaleOverScaleDepth = (1.0 / (OUTER_RADIUS - 1.0)) / 0.25;

            static const float _Scale = 1.0 / (OUTER_RADIUS - 1.0);
            static const float _Samples = 2.0; // THIS IS UNROLLED MANUALLY, DON'T TOUCH

            #define RAYLEIGH (lerp(0.0, 0.0025, pow(_AtmosphereThickness, 2.5)))      // Rayleigh constant
            #define MIE 0.0010             // Mie constant
            #define SUN_BRIGHTNESS 20.0    // Sun brightness
            #define SUN_BRIGHTNESS 20.0    // Sun brightness

            static const half _HDSundiskIntensityFactor = 15.0;


            #define MAX_SCATTER 50.0 // Maximum scattering value, to prevent math overflows on Adrenos
            static const float _Km4PI = MIE * 4.0 * 3.14159265;
            static const float _KmESun = MIE * SUN_BRIGHTNESS;

            static const float _ScaleDepth = 0.25;

            #define SKY_GROUND_THRESHOLD 0.02

            #define MIE_G (-0.990)
            #define MIE_G2 0.9801




            float scale(float inCos)
            {
                float x = 1.0 - inCos;
                return 0.25 * exp(-0.00287 + x*(0.459 + x*(3.83 + x*(-6.80 + x*5.25))));
            }

            // Calculates the Rayleigh phase function
            half getRayleighPhase(half eyeCos2)
            {
                return 0.75 + 0.75*eyeCos2;
            }

            half getRayleighPhase(half3 light, half3 ray)
            {
                half eyeCos = dot(light, ray);
                return getRayleighPhase(eyeCos * eyeCos);
            }

            // Calculates the Mie phase function
            half getMiePhase(half eyeCos, half eyeCos2)
            {
                half temp = 1.0 + MIE_G2 - 2.0 * MIE_G * eyeCos;
                temp = pow(temp, pow(_SunSize,0.65) * 10);
                temp = max(temp,1.0e-4); // prevent division by zero, esp. in half precision
                temp = 1.5 * ((1.0 - MIE_G2) / (2.0 + MIE_G2)) * (1.0 + eyeCos2) / temp;
                return temp;
            }


            half calcSunAttenuation(half3 lightPos, half3 ray)
            {
                half focusedEyeCos = pow(saturate(dot(lightPos, ray)), _SunSizeConvergence);
                return getMiePhase(-focusedEyeCos, focusedEyeCos * focusedEyeCos);
            }

            half calcMoonAttenuation(half3 invLightpos, half3 ray){
                half focusedEyeCos = pow(saturate(dot(invLightpos, ray)), _SunSizeConvergence);
                float3 rayOffset = normalize(float3(invLightpos.x + _MoonOffset, invLightpos.yz));

                float crescentMoon = pow(saturate(dot(ray, rayOffset) - (1.0 - _MoonMaskRadius)), 0.08);

                return smoothstep(0.98, 1, dot(invLightpos, ray)) * saturate (0.6 - crescentMoon);
            }

            struct appdata_t
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4  pos             : SV_POSITION;

                float3  vertex          : TEXCOORD0;

                // calculate sky colors in vprog
                half3   groundColor     : TEXCOORD1;
                half3   skyColor        : TEXCOORD2;
                half3   sunColor        : TEXCOORD3;
            };

            v2f vert (appdata_t v)
            {
                v2f OUT;
                OUT.pos = TransformObjectToHClip(v.vertex.xyz);
                float3 lightDirection = _DirectionalLightDirectionsAndMasks[0].xyz;
	            float3 lightColor = _DirectionalLightColors[0].rgb;

                float3 baseCol = lerp(_NightColor.xyz, _DayColor.xyz, lightDirection.y * 0.5 + 0.5);

                float3 skyMainCol = _SkyTint.xyz;

                // OUT.skyColor = baseCol;

                float3 cameraPos = float3(0,_InnerRadius + _CameraHeight,0);    // The camera's current position
                float3 eyeRay = normalize(mul((float3x3)unity_ObjectToWorld, v.vertex.xyz));

                float kKrESun = RAYLEIGH * SUN_BRIGHTNESS;
                float kKr4PI = RAYLEIGH * 4.0 * 3.14159265;

                float far = 0.0;
                half3 cIn, cOut;
                if(eyeRay.y >= 0.0)
                {
                    far = sqrt(_OuterRadius2 + _InnerRadius2 * eyeRay.y * eyeRay.y - _InnerRadius2) - _InnerRadius * eyeRay.y;
                    float3 pos = cameraPos + far * eyeRay;
                    float height = _InnerRadius + _CameraHeight;
                    float depth = exp(_ScaleOverScaleDepth * (-_CameraHeight));
                    float startAngle = dot(eyeRay, cameraPos) / height;
                    float startOffset = depth*scale(startAngle);

                    // Initialize the scattering loop variables
                    float sampleLength = far / _Samples;
                    float scaledLength = sampleLength * _Scale;
                    float3 sampleRay = eyeRay * sampleLength;
                    float3 samplePoint = cameraPos + sampleRay * 0.5;

                    float3 frontColor = float3(0.0, 0.0, 0.0);

                    {
                        float height = length(samplePoint);
                        float depth = exp(_ScaleOverScaleDepth * (_InnerRadius - height));
                        float lightAngle = dot(lightDirection.xyz, samplePoint) / height;
                        float cameraAngle = dot(eyeRay, samplePoint) / height;
                        float scatter = (startOffset + depth*(scale(lightAngle) - scale(cameraAngle)));
                        float3 attenuate = exp(-clamp(scatter, 0.0, MAX_SCATTER) * (skyMainCol * kKr4PI + _Km4PI));

                        frontColor += attenuate * (depth * scaledLength);
                        samplePoint += sampleRay;
                    }
                    {
                        float height = length(samplePoint);
                        float depth = exp(_ScaleOverScaleDepth * (_InnerRadius - height));
                        float lightAngle = dot(lightDirection.xyz, samplePoint) / height;
                        float cameraAngle = dot(eyeRay, samplePoint) / height;
                        float scatter = (startOffset + depth*(scale(lightAngle) - scale(cameraAngle)));
                        float3 attenuate = exp(-clamp(scatter, 0.0, MAX_SCATTER) * (skyMainCol * kKr4PI + _Km4PI));

                        frontColor += attenuate * (depth * scaledLength);
                        samplePoint += sampleRay;
                    }

                    cIn = frontColor * (skyMainCol * kKrESun);
                    // cIn = frontColor;
                    cOut = frontColor * _KmESun;
                }
                else
                {
                    // Ground
                    far = (-_CameraHeight) / (min(-0.001, eyeRay.y));

                    float3 pos = cameraPos + far * eyeRay;

                    // Calculate the ray's starting position, then calculate its scattering offset
                    float depth = exp((-_CameraHeight) * (1.0/_ScaleDepth));
                    float cameraAngle = dot(-eyeRay, pos);
                    float lightAngle = dot(lightDirection, pos);
                    float cameraScale = scale(cameraAngle);
                    float lightScale = scale(lightAngle);
                    float cameraOffset = depth*cameraScale;
                    float temp = (lightScale + cameraScale);

                    // Initialize the scattering loop variables
                    float sampleLength = far / _Samples;
                    float scaledLength = sampleLength * _Scale;
                    float3 sampleRay = eyeRay * sampleLength;
                    float3 samplePoint = cameraPos + sampleRay * 0.5;

                    // Now loop through the sample rays
                    float3 frontColor = float3(0.0, 0.0, 0.0);
                    float3 attenuate;
                    {
                        float height = length(samplePoint);
                        float depth = exp(_ScaleOverScaleDepth * (_InnerRadius - height));
                        float scatter = depth*temp - cameraOffset;
                        attenuate = exp(-clamp(scatter, 0.0, MAX_SCATTER) * (skyMainCol * kKr4PI + _Km4PI));
                        frontColor += attenuate * (depth * scaledLength);
                        samplePoint += sampleRay;
                    }
                    cIn = frontColor * (skyMainCol * kKrESun + _KmESun);
                    cOut = clamp(attenuate, 0.0, 1.0);
                }

                OUT.vertex = -eyeRay;

                OUT.groundColor = _Exposure * (cIn + _GroundColor * cOut);
                OUT.skyColor    = _Exposure * (cIn * getRayleighPhase(lightDirection, -eyeRay));

                // half lightColorIntensity = clamp(length(lightColor.xyz), 0.25, 1);
                OUT.sunColor    = _HDSundiskIntensityFactor * saturate(cOut) * lightColor.xyz;

                OUT.groundColor = sqrt(OUT.groundColor);
                OUT.skyColor    = sqrt(OUT.skyColor) + baseCol;

                return OUT;
            }

            TEXTURE2D(_Stars);
            float _BlendStep;
            float4 _StarsColor;
            float _StarsFrequency;
            float _StarsSpeed;
            float _StarsCutoff;

            TEXTURE2D(_Cloud);
            float _CloudSpeed;
            float _CloudFrequency;
            float _CloudCutoff;
            float _CloudNoiseScale;
            float _DistortionSpeed;
            float _DistortScale;
            float4 _CloudNightColor;
            float4 _CloudDayColor;


            float4 frag (v2f IN) : SV_Target
            {

                half3 col = half3(0.0, 0.0, 0.0);
                
                half3 ray = normalize(IN.vertex.xyz);
                half y = ray.y / SKY_GROUND_THRESHOLD;

                col = lerp(IN.skyColor, IN.groundColor, saturate(y));
                float3 lightDirection = _DirectionalLightDirectionsAndMasks[0].xyz;

                if(y < 0.0)
                {
                    #ifdef _DRAW_SUN_MOON
                        col += IN.sunColor * calcSunAttenuation(lightDirection, -ray);
                        col += _MoonColor * calcMoonAttenuation(-lightDirection, -ray);
                    #endif
                    
                    //云朵uv
                    float2 skyuv = ray.xz / (-ray.y);
                    float blendValue = smoothstep(0, _BlendStep, -ray.y);

                    //星星
                    float stars = SAMPLE_TEXTURE2D(_Stars, sampler_linear_repeat, (skyuv + float2(_StarsSpeed, _StarsSpeed) * _Time.x) * _StarsFrequency).r;
                    float3 starsCol = step(_StarsCutoff, stars) * _StarsColor * smoothstep(0.2, 0.5, -lightDirection.y);

                    //云朵
                    float cloud = SAMPLE_TEXTURE2D(_Cloud, sampler_linear_repeat, (skyuv + float2(_CloudSpeed, _CloudSpeed) * _Time.x) * _CloudFrequency).r;
                    float distort = SAMPLE_TEXTURE2D(_Cloud, sampler_linear_repeat, (skyuv + (_Time.x * _DistortionSpeed)) * _DistortScale).g;
                    float noise = SAMPLE_TEXTURE2D(_Cloud, sampler_linear_repeat, ((skyuv + distort) - (_Time.x * _CloudSpeed)) * _CloudNoiseScale).b;

                    float3 cloudCol = saturate(smoothstep(saturate(_CloudCutoff - 0.2), _CloudCutoff, cloud * noise)) * 
                        lerp(_CloudDayColor, _CloudNightColor, saturate(-lightDirection.y * 0.5 + 0.5));
                    cloudCol += saturate(smoothstep( _CloudCutoff, saturate(_CloudCutoff + 0.2), cloud * distort)) * 
                        lerp(_CloudDayColor * 0.3, _CloudNightColor * 0.3, saturate(-lightDirection.y * 0.5 + 0.5));
                    starsCol *= (1 - cloudCol);

                    float3 starAndCloudCol = (starsCol + cloudCol) * blendValue;
                    // float3 starAndCloudCol = starsCol +  * blendValue;
                    col += starAndCloudCol;
                    // col = starsCol;
                }



                return half4(col,1.0);

            }

            ENDHLSL
        }
    }
    CustomEditor "LWGUI.LWGUI"
}
