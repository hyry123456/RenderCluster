Shader "CustomRP/BulkFog"
{
    SubShader
    {

        Pass
        {

            Cull Front
            Blend SrcAlpha OneMinusSrcAlpha

            // Cull Front
            // Blend One One
            // Blend One Zero
            ZWrite Off
            ZTest Off

            HLSLPROGRAM

            #include "../../ShaderLibrary/Common.hlsl"
            #include "../../ShaderLibrary/Noise.hlsl"
            #include "../../CommonInput/CommonInput.hlsl"


            float3 _Extend;
            float3 _Offset;
            TEXTURE3D(_Light3D);
            SAMPLER(sampler_Light3D);
            uint _RayCount;
            TEXTURE2D(_Noise);
            TEXTURE2D(_RenderCameraDepth);
            float _FogIntensity;
            float _BulkLightShrinkRadio;
            float _LightIntensity;

            float _FogMaxDepth;
            float _FogMinDepth;
            float _FogDepthFallOff;
            float _FogMaxHight;
            float _FogMinHight;
            float _FogPosYFallOff;
            float3 _FogColor;

            float3 _FogFrequency;
            float3 _FogSpeed;

            #pragma vertex vert
            #pragma fragment frag

            struct Attributes {
                float3 positionOS : POSITION;
                float3 normal : NORMAL;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float3 worldPos : VAR_WORLDPOS;
                float3 boxMaxPositionWS : BOX_MAX;
                float3 boxMinPositionWS : BOX_MIN;
            };


            float3 LineraLerp(float3 value, float3 minV, float3 maxV){
                return saturate((value - minV) / (maxV - minV));
            }

            float LinearLerp(float value, float minV, float maxV){
                return saturate((value - minV) / (maxV - minV));
            }
            
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

            //当点1、2和起点都在一条线上时，检查1是否离起点更近
            bool Check1Near2(float beginAxis, float firAxis, float secAxis, float direcAxis){
                float time1 = (firAxis - beginAxis) / direcAxis,
                    time2 = (secAxis - beginAxis) / direcAxis;
                return time1 < time2;
            }


            void RayTracingBox(float3 direction, float3 maxBox, float3 minBox, out float inT, out float outT, float3 origin){
                outT = 1.#INF; inT = -1000000000;
                for(int i = 0; i < 3; i++){
                    if(direction[i] > 0){
                        outT = min((maxBox[i] - origin[i]) / direction[i], outT);
                        inT = max((minBox[i] - origin[i]) / direction[i], inT);
                    }
                    else{
                        outT = min((minBox[i] - origin[i]) / direction[i], outT);
                        inT = max((maxBox[i] - origin[i]) / direction[i], inT);
                    }
                }
            }


            Varyings vert(Attributes input){
                Varyings output;
                float3 objPos = input.positionOS * _Extend + _Offset;
                output.worldPos = objPos;
                output.positionCS = TransformWorldToHClip(output.worldPos);
                output.boxMaxPositionWS = 0.5 * _Extend + _Offset;
                output.boxMinPositionWS = -0.5 * _Extend + _Offset;
                return output;
            }

            float2 randomUV(float value){

                float intV0 = floor(value);
                float ran0 = hash1d(intV0);
                float frac0 = frac(value);

                float intV1 = floor(-value);
                float ran1 = hash1d(intV1);
                float frac1 = frac(-value);

                return float2(
                    lerp(ran0, ran1, frac0),
                    0
                );
            }

            //x:01深度, y:01高度
            float GetDefferFogRadio(float2 depth01_h, float3 worldPos){
                //确定在范围中的比例
                float bufferDepth = 1.0 - saturate( (_FogMaxDepth - depth01_h.x) / ( _FogMaxDepth - _FogMinDepth ) );
                //根据平方缩减
                float depthRadio = pow(bufferDepth, _FogDepthFallOff);

                //确定高度比例
                float posX = saturate( (_FogMaxHight - depth01_h.y) / ( _FogMaxHight - _FogMinHight ) );
                    
                //根据平方缩减
                float posYRadio = pow(posX, _FogPosYFallOff);

                worldPos *= _FogFrequency;
                worldPos += _FogSpeed * _Time.x;

                // float2 uv = worldPos.xz + randomUV(worldPos.y);
                float fog = saturate(Perlin3DFBM(worldPos, 1));
                // float fog = SAMPLE_TEXTURE2D(_Fog, sampler_linear_repeat, uv).r;
                
                return saturate(depthRadio * posYRadio + fog * _FogIntensity);
            }



            float4 frag(Varyings input) : SV_TARGET{

                float outT, inT;
                float3 direction = normalize(input.worldPos - _WorldSpaceCameraPos);
                float2 screenUV = input.positionCS.xy * _CameraBufferSize.zw;

                float bufferDepth = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, sampler_point_clamp, screenUV, 0);

                float eyeDepth = IsOrthographicCamera() ? OrthographicDepthBufferToLinear(bufferDepth) 
                    : LinearEyeDepth(bufferDepth, _ZBufferParams);

                float3 bufferPos = GetWorldPos(bufferDepth, screenUV);

                RayTracingBox(direction, input.boxMaxPositionWS, input.boxMinPositionWS, inT, outT, _WorldSpaceCameraPos);

                float3 targetPos = _WorldSpaceCameraPos + direction * outT;

                float3 beginPos = _WorldSpaceCameraPos + direction * max(inT, 0);
                float3 end;

                //如果物体的起点比盒子的起点更近，就是被物体挡住，不渲染
                if(Check1Near2(_WorldSpaceCameraPos.x, bufferPos.x, beginPos.x, direction.x)) clip(-1);

                if(Check1Near2(_WorldSpaceCameraPos.x, bufferPos.x, targetPos.x, direction.x)) end = bufferPos;
                else end = targetPos;

                float4 endCS = TransformWorldToHClip(end);
                float4 beginCS = TransformWorldToHClip(beginPos);
                float beginDepth01 = (beginCS.w - _ProjectionParams.y) / (_ProjectionParams.z - _ProjectionParams.y);
                float endDepth01 = (endCS.w - _ProjectionParams.y) / (_ProjectionParams.z - _ProjectionParams.y);

                float beginH01 = LinearLerp(beginPos.y, input.boxMinPositionWS.y, input.boxMaxPositionWS.y);
                float endH01 = LinearLerp(end.y, input.boxMinPositionWS.y, input.boxMaxPositionWS.y);



                float3 color = 0;
                float fogSum = 0;
                float seed = 0;
                float2 jitter = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_linear_repeat, screenUV * (_ScreenParams.xy / 30), 0).xy;
                seed = frac(jitter.x + jitter.y);

                float3 step = (end - beginPos) / _RayCount;
                float3 current = beginPos;
                float2 currentFog = float2(beginDepth01, beginH01);
                float2 fogSetp = (float2(endDepth01, endH01) - float2(beginDepth01, beginH01)) / _RayCount;

                for(uint i = 0; i < _RayCount; i++){
                    float3 worldPos = current + step * seed;
                    float fog = GetDefferFogRadio(currentFog, worldPos);
                    fogSum += fog;
                    currentFog += fogSetp;
                    float3 uvw = LineraLerp(worldPos, input.boxMinPositionWS, input.boxMaxPositionWS);
                    color += SAMPLE_TEXTURE3D(_Light3D, sampler_Light3D, uvw).rgb;
                    current += step;
                }
                fogSum /= _RayCount;
                color /= _RayCount;

                // float fog = GetDefferFogRadio(float2(endDepth01, endH01), end);

                // return float4(_FogColor, fog);


                return float4(color * 50 + _FogColor, fogSum);
            }

            ENDHLSL
        }
    }
}
