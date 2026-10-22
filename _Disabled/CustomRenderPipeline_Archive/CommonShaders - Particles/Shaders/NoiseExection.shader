Shader "CustomRP/NoiseExection"
{

    Properties
    {
        // _MainTex("Base Tex", 2D) = "white" {}

        [Header(Base Setting)]
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend("Dst Blend", Float) = 2
        [Enum(Off, 0, On, 1)] _ZWrite("Z Write", Float) = 0

        [Header(Particles Setting)]
        [Space(10)]
        [Toggle(_NEAR_ALPHA)] _UseNearAlpha("Use Near Alpha", float) = 0
        _NearFadeDistance("Near Fade Distance", Range(0, 5)) = 1
        _NearFadeRange ("Near Fade Range", Range(0, 5)) = 1

        [Toggle(_SOFT_PARTICLE)] _UseSoftParticle("Use Soft Particle", float) = 0
        _SoftParticlesDistance("Soft Particle Distance", Range(0.0, 10.0)) = 0.01
        _SoftParticlesRange ("Soft Particle Range", Range(0.01, 10.0)) = 1

        [Header(Distortion Setting)]
        [Space(10)]
        [Toggle(_DISTORTION)] _UseDistorion("Use Distorion", float) = 0
        [NoScaleOffset] _DistortionTex("Distortion Texture", 2D) = "Bump"{}
        _DistortionSize ("Distortion Size", Range(0, 1)) = 1
        _DistortionBlend ("Distortion Blend", Range(0, 1)) = 1


        [Toggle(_FELLOW_SPEED)] _FellowSpeed("Particle Fellow Speed", float) = 0
    }
    SubShader
    {
		HLSLINCLUDE
            #include "../../../ShaderLibrary/Common.hlsl"
            #include "../../../CommonInput/CommonInput.hlsl"
            #include "../../../ShaderLibrary/Fragment.hlsl"
            #include "../Input/ParticleSystemInput.hlsl"
		ENDHLSL
        Pass
        {

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest On
            Cull Off
            HLSLPROGRAM

            #pragma target 4.6

            #pragma vertex vert
            #pragma fragment frag
            #pragma require geometry
            #pragma geometry geom

            #pragma shader_feature _FELLOW_SPEED
            #pragma shader_feature _NEAR_ALPHA
            #pragma shader_feature _SOFT_PARTICLE
            #pragma shader_feature _DISTORTION

            #pragma shader_feature _SORT_PARTICLE
            
            TEXTURE2D(_MainTex);
            TEXTURE2D(_DistortionTex);
            SAMPLER(sampler_MainTex);

            float _TexAspectRatio;
            float _NearFadeDistance;
            float _NearFadeRange;
            float _SoftParticlesDistance;
            float _SoftParticlesRange;

            float _AspectRatio;

            uint _Row, _Column;     //uv动画需要的行和列

            float _DistortionSize;
            float _DistortionBlend;


            struct ParticleIndex{
                uint index : INDEX;
            };
            
            struct Varyings {
                float4 color : VAR_COLOR;
                float4 positionCS_SS : SV_POSITION;
                float3 positionWS : VAR_POSITION;
                float4 baseUV : VAR_BASE_UV;
                float interpolation : VAR_INTERPOLATION;
            };

            ParticleIndex vert(uint idIns : SV_VertexID)
            {
                ParticleIndex particle;
                int index;
                #ifdef _SORT_PARTICLE
                    index = _ParticlesIndexsBuffer[idIns];
                #else
                    index = idIns;
                #endif

                particle.index = index;
                return particle;
            }

            [maxvertexcount(6)]
            void geom(point ParticleIndex input[1], inout TriangleStream<Varyings> tristream)
            {
                Varyings output[4] = (Varyings[4])0;
                ParticleData particle = _ParticlesBuffer[input[0].index];

                if(particle.index.x == 0) return;
                PlaneData planeData;
#ifdef _FELLOW_SPEED
                planeData = PointToPlane_FollowSpeed(particle, _AspectRatio);
#else
                //得到朝向摄像机的面
                planeData = PointToPlane(particle, _AspectRatio);
#endif

                output[0].positionCS_SS = planeData.positionCS[0];
                output[1].positionCS_SS = planeData.positionCS[1];
                output[2].positionCS_SS = planeData.positionCS[2];
                output[3].positionCS_SS = planeData.positionCS[3];

                output[0].baseUV = GetUV(float2(0, 0), particle.uvTransData, _Row, _Column);
                output[1].baseUV = GetUV(float2(1, 0), particle.uvTransData, _Row, _Column);
                output[2].baseUV = GetUV(float2(0, 1), particle.uvTransData, _Row, _Column);
                output[3].baseUV = GetUV(float2(1, 1), particle.uvTransData, _Row, _Column);

                output[0].interpolation = particle.interpolation;
                output[1].interpolation = particle.interpolation;
                output[2].interpolation = particle.interpolation;
                output[3].interpolation = particle.interpolation;

                output[0].positionWS = planeData.positionWS[0];
                output[1].positionWS = planeData.positionWS[1];
                output[2].positionWS = planeData.positionWS[2];
                output[3].positionWS = planeData.positionWS[3];

                output[0].color = particle.color;
                output[1].color = particle.color;
                output[2].color = particle.color;
                output[3].color = particle.color;

                tristream.Append(output[1]);
                tristream.Append(output[2]);
                tristream.Append(output[0]);
                tristream.RestartStrip();

                tristream.Append(output[1]);
                tristream.Append(output[3]);
                tristream.Append(output[2]);
                tristream.RestartStrip();
            }

            float4 GetBaseColor(float4 uv, float interpolation){
                float4 color1 = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv.xy);
                float4 color2 = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv.zw);
                return lerp(color1, color2, interpolation);
            }

            float ChangeAlpha(Fragment fragment){
                float reAlpha = 1;

                #ifdef _NEAR_ALPHA
                    reAlpha = saturate( (fragment.depth - _NearFadeDistance) / _NearFadeRange );
                #endif

                #ifdef _SOFT_PARTICLE
                    float depthDelta = fragment.bufferDepth - fragment.depth;	//获得深度差
                    reAlpha *= (depthDelta - _SoftParticlesDistance) / _SoftParticlesRange;	//进行透明控制
                #endif
                return saturate( reAlpha );
            }

            float2 GetDistortion(Varyings i, float baseAlpha){
                float2 color1 = SAMPLE_TEXTURE2D(_DistortionTex, sampler_MainTex, i.baseUV.xy).xy;
                float2 color2 = SAMPLE_TEXTURE2D(_DistortionTex, sampler_MainTex, i.baseUV.zw).xy;
                float2 color = lerp(color1, color2, i.interpolation) * i.color.xy;
                return color.xy * baseAlpha * _DistortionSize;
            }

            float GetDistortionBlend(){
                return _DistortionBlend;
            }

            float4 frag(Varyings input) : SV_Target{
                Fragment fragment = GetFragment(input.positionCS_SS);

                float4 color = GetBaseColor(input.baseUV, input.interpolation) * input.color;
                color.a *= ChangeAlpha(fragment);

                #ifdef _DISTORTION
                    float4 bufferColor = GetBufferColor(fragment, GetDistortion(input, color.a));
                    color.rgb = lerp( bufferColor.rgb, 
                        color.rgb, saturate(color.a - GetDistortionBlend()));
                #endif


                return color;
            }



            ENDHLSL
        }
    }
}
