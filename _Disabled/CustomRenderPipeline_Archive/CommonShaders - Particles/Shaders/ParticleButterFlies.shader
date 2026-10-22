Shader "CustomRP/ParticleButterFlies"
{
    Properties
    {
    }
    SubShader
    {

		HLSLINCLUDE
            #include "../../../ShaderLibrary/Common.hlsl"
            #include "../../../CommonInput/CommonInput.hlsl"
		ENDHLSL
        Pass
        {
            // Blend SrcAlpha OneMinusSrcAlpha
            Blend One Zero
            ZWrite On
            Cull Off
            HLSLPROGRAM
            #include "../Input/ParticleSystemInput.hlsl"

            uint _Row, _Column;     //uv动画需要的行和列
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            #pragma target 4.6

            #pragma vertex vert
            // #pragma fragment frag
            #pragma fragment WriteGBufferParticle
            #pragma require geometry
            #pragma geometry geom
            
            struct ParticleIndex{
                uint index : INDEX;
            };

            struct ButterFlies{
                float4 color : VAR_COLOR;
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : VAR_POSITION;
                float3 normal : NORMAL;
            };

            ParticleIndex vert(uint idIns : SV_VertexID)
            {
                ParticleIndex particle;

                particle.index = idIns;
                return particle;
            }

            //计算uv，包含uv动画
            float2 GetButterUV(float2 uv, float4 uvTransData) {
                float2 reUV;
                reUV.xy = uv + uvTransData.xy;

                reUV.x /= _Row;
                reUV.y /= _Column;
                return reUV;
            }

            void CaculateButterflies(ParticleData data, inout ButterFlies particles[6]){

                float3 worldVer = data.worldPos;    //中心
                float paritcleLen = data.size;      //大小

                float3 upDir = normalize(data.nowSpeed), 
                    particleNormal = normalize(cross(float3(0, 1, 0), upDir));

                particles[0].positionWS = -upDir * paritcleLen + -particleNormal * paritcleLen;
                particles[1].positionWS = upDir * paritcleLen + -particleNormal * paritcleLen;

                particles[2].positionWS = -upDir * paritcleLen;
                particles[3].positionWS = upDir * paritcleLen;

                particles[4].positionWS = -upDir * paritcleLen + particleNormal * paritcleLen;
                particles[5].positionWS = upDir * paritcleLen + particleNormal * paritcleLen;

                float hight = paritcleLen * sin(data.random.w * 10) ;
                particles[0].positionWS.y += hight;
                particles[1].positionWS.y += hight;
                particles[4].positionWS.y += hight;
                particles[5].positionWS.y += hight;

                particles[0].positionWS += worldVer;
                particles[1].positionWS += worldVer;
                particles[2].positionWS += worldVer;
                particles[3].positionWS += worldVer;
                particles[4].positionWS += worldVer;
                particles[5].positionWS += worldVer;

                int transData = data.random.x * _Row * _Column;

                particles[0].pos = mul(UNITY_MATRIX_VP, float4(particles[0].positionWS, 1));
                particles[0].color = data.color;
                particles[0].uv = GetButterUV(float2(0, 0), transData);

                particles[1].pos = mul(UNITY_MATRIX_VP, float4(particles[1].positionWS, 1));
                particles[1].color = data.color;
                particles[1].uv = GetButterUV(float2(0, 1), transData);

                particles[2].pos = mul(UNITY_MATRIX_VP, float4(particles[2].positionWS, 1));
                particles[2].color = data.color;
                particles[2].uv = GetButterUV(float2(1, 0), transData);

                particles[3].pos = mul(UNITY_MATRIX_VP, float4(particles[3].positionWS, 1));
                particles[3].color = data.color;
                particles[3].uv = GetButterUV(float2(1, 1), transData);

                particles[4].pos = mul(UNITY_MATRIX_VP, float4(particles[4].positionWS, 1));
                particles[4].color = data.color;
                particles[4].uv = GetButterUV(float2(0, 0), transData);

                particles[5].pos = mul(UNITY_MATRIX_VP, float4(particles[5].positionWS, 1));
                particles[5].color = data.color;
                particles[5].uv = GetButterUV(float2(0, 1), transData);
            }

            //2个面形成蝴蝶的两个翅膀，故总共花12个点
            [maxvertexcount(12)]
            void geom(point ParticleIndex IN[1], inout TriangleStream<ButterFlies> tristream)
            {
                ParticleData particle = _ParticlesBuffer[IN[0].index];

                if(particle.index.x == 0) return;

                ButterFlies particles[6] = (ButterFlies[6])0;
                CaculateButterflies(particle, particles);

                float3 normal = normalize(cross(particles[2].positionWS - particles[0].positionWS, particles[1].positionWS - particles[0].positionWS));
                particles[0].normal = normal;
                particles[1].normal = normal;
                particles[2].normal = normal;
                particles[3].normal = normal;

                tristream.Append(particles[2]);
                tristream.Append(particles[1]);
                tristream.Append(particles[0]);
                tristream.RestartStrip();

                tristream.Append(particles[2]);
                tristream.Append(particles[3]);
                tristream.Append(particles[1]);
                tristream.RestartStrip();

                normal = -normalize(cross(particles[4].positionWS - particles[2].positionWS, particles[3].positionWS - particles[2].positionWS));
                particles[2].normal = normal;
                particles[3].normal = normal;
                particles[4].normal = normal;
                particles[5].normal = normal;

                tristream.Append(particles[4]);
                tristream.Append(particles[5]);
                tristream.Append(particles[2]);
                tristream.RestartStrip();

                tristream.Append(particles[5]);
                tristream.Append(particles[2]);
                tristream.Append(particles[3]);
                tristream.RestartStrip();
            }

            float4 GetColor(ButterFlies i){
                float4 color1 = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv.xy);
                return color1 * i.color;
            }


            void WriteGBufferParticle (ButterFlies i,
                out float4 _GBufferRT0 : SV_Target0,	//rgb:abledo,w:metalness
                out float4 _GBufferRT1 : SV_Target1,	//normal
                out float4 _GBufferRT2 : SV_Target2,	//rgb:emissive,w:roughness
                out float4 _GBufferRT3 : SV_Target3	//rgb:reflect,w:AO
            ){
                float4 color = GetColor(i);
                clip(color.a - 0.1);

	            float2 normalPack = PackNormal(i.normal);

                _GBufferRT0 = float4(color.xyz, 0);
                _GBufferRT1 = float4(normalPack, 0, 1);
                _GBufferRT2 = float4(color.xyz * 0.5, 0.5);
                _GBufferRT3 = float4(0, 0, 0, 1);
            }


            ENDHLSL
        }

        Pass
        {
            // Blend SrcAlpha OneMinusSrcAlpha
            Blend One Zero
            ZWrite On
            Cull Off
            HLSLPROGRAM
            #include "../Input/ParticleSystemInput.hlsl"

            uint _Row, _Column;     //uv动画需要的行和列
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            #pragma target 4.6

            #pragma vertex vert
            // #pragma fragment frag
            #pragma fragment WriteShadowParticle
            #pragma require geometry
            #pragma geometry geom
            
            struct ParticleIndex{
                uint index : INDEX;
            };

            struct ButterFlies{
                float4 color : VAR_COLOR;
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : VAR_POSITION;
            };

            ParticleIndex vert(uint idIns : SV_VertexID)
            {
                ParticleIndex particle;

                particle.index = idIns;
                return particle;
            }

            //计算uv，包含uv动画
            float2 GetButterUV(float2 uv, float4 uvTransData) {
                float2 reUV;
                reUV.xy = uv + uvTransData.xy;

                reUV.x /= _Row;
                reUV.y /= _Column;
                return reUV;
            }

            void CaculateButterflies(ParticleData data, inout ButterFlies particles[6]){

                float3 worldVer = data.worldPos;    //中心
                float paritcleLen = data.size;      //大小

                float3 upDir = normalize(data.nowSpeed), 
                    particleNormal = normalize(cross(float3(0, 1, 0), upDir));

                particles[0].positionWS = -upDir * paritcleLen + -particleNormal * paritcleLen;
                particles[1].positionWS = upDir * paritcleLen + -particleNormal * paritcleLen;

                particles[2].positionWS = -upDir * paritcleLen;
                particles[3].positionWS = upDir * paritcleLen;

                particles[4].positionWS = -upDir * paritcleLen + particleNormal * paritcleLen;
                particles[5].positionWS = upDir * paritcleLen + particleNormal * paritcleLen;

                float hight = paritcleLen * sin(data.random.w * 10) ;
                particles[0].positionWS.y += hight;
                particles[1].positionWS.y += hight;
                particles[4].positionWS.y += hight;
                particles[5].positionWS.y += hight;

                particles[0].positionWS += worldVer;
                particles[1].positionWS += worldVer;
                particles[2].positionWS += worldVer;
                particles[3].positionWS += worldVer;
                particles[4].positionWS += worldVer;
                particles[5].positionWS += worldVer;

                int transData = data.random.x * _Row * _Column;

                particles[0].pos = mul(UNITY_MATRIX_VP, float4(particles[0].positionWS, 1));
                particles[0].color = data.color;
                particles[0].uv = GetButterUV(float2(0, 0), transData);

                particles[1].pos = mul(UNITY_MATRIX_VP, float4(particles[1].positionWS, 1));
                particles[1].color = data.color;
                particles[1].uv = GetButterUV(float2(0, 1), transData);

                particles[2].pos = mul(UNITY_MATRIX_VP, float4(particles[2].positionWS, 1));
                particles[2].color = data.color;
                particles[2].uv = GetButterUV(float2(1, 0), transData);

                particles[3].pos = mul(UNITY_MATRIX_VP, float4(particles[3].positionWS, 1));
                particles[3].color = data.color;
                particles[3].uv = GetButterUV(float2(1, 1), transData);

                particles[4].pos = mul(UNITY_MATRIX_VP, float4(particles[4].positionWS, 1));
                particles[4].color = data.color;
                particles[4].uv = GetButterUV(float2(0, 0), transData);

                particles[5].pos = mul(UNITY_MATRIX_VP, float4(particles[5].positionWS, 1));
                particles[5].color = data.color;
                particles[5].uv = GetButterUV(float2(0, 1), transData);
            }

            //2个面形成蝴蝶的两个翅膀，故总共花12个点
            [maxvertexcount(12)]
            void geom(point ParticleIndex IN[1], inout TriangleStream<ButterFlies> tristream)
            {
                ParticleData particle = _ParticlesBuffer[IN[0].index];

                if(particle.index.x == 0) return;

                ButterFlies particles[6] = (ButterFlies[6])0;
                CaculateButterflies(particle, particles);

                tristream.Append(particles[2]);
                tristream.Append(particles[1]);
                tristream.Append(particles[0]);
                tristream.RestartStrip();

                tristream.Append(particles[2]);
                tristream.Append(particles[3]);
                tristream.Append(particles[1]);
                tristream.RestartStrip();

                tristream.Append(particles[4]);
                tristream.Append(particles[5]);
                tristream.Append(particles[2]);
                tristream.RestartStrip();

                tristream.Append(particles[5]);
                tristream.Append(particles[2]);
                tristream.Append(particles[3]);
                tristream.RestartStrip();
            }

            float4 GetColor(ButterFlies i){
                float4 color1 = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv.xy);
                return color1 * i.color;
            }


            void WriteShadowParticle (ButterFlies i){
                float4 color = GetColor(i);
                clip(color.a - 0.1);
            }


            ENDHLSL
        }
    }
}
