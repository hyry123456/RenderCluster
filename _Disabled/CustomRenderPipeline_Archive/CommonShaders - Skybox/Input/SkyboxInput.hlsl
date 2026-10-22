#ifndef CUSTOM_SKYBOX_INPUT
#define CUSTOM_SKYBOX_INPUT


uniform half _Exposure;     // HDR exposure
uniform half3 _GroundColor;
uniform half _SunSize;
uniform half _SunSizeConvergence;
uniform half3 _SkyTint;
uniform half _AtmosphereThickness;


// static const float3 kDefaultScatteringWavelength = float3(.65, .57, .475);
uniform float3 kDefaultScatteringWavelength;
static const float3 kVariableRangeForScatteringWavelength = float3(.15, .15, .15);


#define OUTER_RADIUS 1.025
static const float kOuterRadius = OUTER_RADIUS;
static const float kOuterRadius2 = OUTER_RADIUS*OUTER_RADIUS;
static const float kInnerRadius = 1.0;
static const float kInnerRadius2 = 1.0;

static const float kCameraHeight = 0.0001;


#define kRAYLEIGH (lerp(0.0, 0.0025, pow(_AtmosphereThickness, 2.5)))      // Rayleigh constant
#define kMIE 0.0010             // Mie constant
#define kSUN_BRIGHTNESS 20.0    // Sun brightness

#define kMAX_SCATTER 50.0 // Maximum scattering value, to prevent math overflows on Adrenos

static const half kHDSundiskIntensityFactor = 15.0;
static const half kSimpleSundiskIntensityFactor = 27.0;

static const half kSunScale = 400.0 * kSUN_BRIGHTNESS;
static const float kKmESun = kMIE * kSUN_BRIGHTNESS;
static const float kKm4PI = kMIE * 4.0 * 3.14159265;
static const float kScale = 1.0 / (OUTER_RADIUS - 1.0);
static const float kScaleDepth = 0.25;
static const float kScaleOverScaleDepth = (1.0 / (OUTER_RADIUS - 1.0)) / 0.25;
static const float kSamples = 2.0; // THIS IS UNROLLED MANUALLY, DON'T TOUCH

#define MIE_G (-0.990)
#define MIE_G2 0.9801


#define SKY_GROUND_THRESHOLD 0.02

// fine tuning of performance. You can override defines here if you want some specific setup
// or keep as is and allow later code to set it according to target api

// if set vprog will output color in final color space (instead of linear always)
// in case of rendering in gamma mode that means that we will do lerps in gamma mode too, so there will be tiny difference around horizon
// #define SKYBOX_COLOR_IN_TARGET_COLOR_SPACE 0

// sun disk rendering:
// no sun disk - the fastest option
#define SKYBOX_SUNDISK_NONE 0
// simplistic sun disk - without mie phase function
#define SKYBOX_SUNDISK_SIMPLE 1
// full calculation - uses mie phase function
#define SKYBOX_SUNDISK_HQ 2



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

#endif