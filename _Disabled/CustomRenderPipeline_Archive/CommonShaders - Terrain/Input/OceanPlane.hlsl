#ifndef CUSTOMRP_OCREANPLANE
#define CUSTOMRP_OCREANPLANE

float2 _WaveMainDirection;
float _MaxSteepness;
float _MinSteepness;
float _MaxWaveLength;
float _MinWaveLength;
float _RandomIntensity;
int _WaveCount;

struct Gerstner
{
     float3 positionWS;
     float3 binormal;
     float3 tangent;
};

float Random(int seed)
{         
    return frac(sin(dot(float2(seed,2), float2(12.9898, 78.233))) ) * 2 - 1;
}

Gerstner GerstnerWave(float2 direction,float3 positionWS,int waveCount,float wavelengthMax,float wavelengthMin,
    float steepnessMax,float steepnessMin,float randomdirection)
{
    Gerstner gerstner;

    float3 P;
    float3 B;
    float3 T;

    for (int i = 0; i < waveCount; i++)
    {
        float step = (float) i / (float) waveCount;

        float2 d = float2(Random(i),Random(2*i));
        d = normalize(lerp(normalize(direction.xy), d, randomdirection));

        //step = pow(step, 0.75f);可以使用线性插值或其他插值曲线
        float wavelength = lerp(wavelengthMax, wavelengthMin, step);
        float steepness = lerp(steepnessMax, steepnessMin, step)/waveCount;

        float k = 2 * PI / wavelength;
        float g = 9.81f;
        float w = sqrt(g * k);
        float a = steepness / k;
        float2 wavevector = k * d;
        float value = dot(wavevector, positionWS.xz) - w * _Time.y;

        P.x += d.x * a * cos(value);
        P.z += d.y * a * cos(value);
        P.y += a * sin(value);

        T.x += d.x * d.x * k * a * -sin(value);
        T.y += d.x * k * a * cos(value);
        T.z += d.x * d.y * k * a * -sin(value);

        B.x += d.x * d.y * k * a * -sin(value);
        B.y += d.y * k * a * cos(value);
        B.z += d.y * d.y * k * a * -sin(value);
    }
    
    gerstner.positionWS.x = positionWS.x + P.x;
    gerstner.positionWS.y = P.y;
    gerstner.positionWS.z = positionWS.z + P.z;
    gerstner.tangent = float3(1 + T.x, T.y, T.z);
    gerstner.binormal = float3(B.x,B.y,1 + B.z);

    return gerstner;               
}

static float3x3 causticMat = float3x3( -2,-1,2, 3,-2,1, 1,2,2);

float Caustic(float va, float3 k){
    return length(.5-frac(mul(causticMat * va, k)));
}


float CausticCreate(float2 p, float s)
{
    float4 k;
    float t = sin(s * 0.3);

    p = abs(fmod(abs(p), 30) - 15);
    
    // k.xz = p*(sin(k = t *.01).w+2.) / abs(p * 0.01);
    k.xz = p*(sin(k = t * 0.5).w+2.) / 3;
    k.y = sin(s);
    
    // float radio = (sin(s * 0.05) * 0.5 + 0.5);
    float col0 = Caustic(0.5, k.xyz);
    float col1 = Caustic(0.42, k.xyz);
    float col2 = Caustic(0.37, k.xyz);

    float col = pow(min(min(col0, col1), col2), 7) * 20.0;
    // float col = col2;
    // float col = pow(min(min(Caustic(lerp(0.45, 0.44, radio), k.xyz), Caustic(lerp(0.44, 0.43, radio), k.xyz)), 
    //     Caustic(lerp(0.43, 0.42, radio), k.xyz)), 7.)*40.;
    return saturate(col);
}

#endif