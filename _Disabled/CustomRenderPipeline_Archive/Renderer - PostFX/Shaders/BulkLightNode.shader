Shader "CustomRP/BulkLightNode"
{
    SubShader
    {

        Pass
        {

            Cull Front
            Blend One One
            // Blend One Zero
            ZWrite Off
            ZTest Always

            HLSLPROGRAM

            #include "../../ShaderLibrary/Common.hlsl"


            float3 _Extend;
            float3 _Offset;
            TEXTURE3D(_Light3D);
            SAMPLER(sampler_Light3D);
            uint _RayCount;
            TEXTURE2D(_Noise);
            TEXTURE2D(_RenderCameraDepth);
            float _BulkLightShrinkRadio;
            float _LightIntensity;

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
            bool Check1Near2(float3 beginPos, float3 firPos, float3 secPos) {
                float3 time1 = firPos - beginPos,
                    time2 = secPos - beginPos;
                return dot(time1, time1) < dot(time2, time2);
            }

            void RayTracingBox(float3 direction, float3 maxBox, float3 minBox, out float inT, out float outT, float3 origin){
                outT = 1.#INF; inT = 0;
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



            float4 frag(Varyings input) : SV_TARGET{

                float outT, inT;
                float3 direction = normalize(input.worldPos - _WorldSpaceCameraPos);
                //direction = max(0.0001f, abs(direction)) * sign(direction);
                float2 screenUV = input.positionCS.xy * _CameraBufferSize.zw * 2;	//体积光渲染在一张半贴图上

                float bufferDepth = SAMPLE_DEPTH_TEXTURE_LOD(_RenderCameraDepth, sampler_point_clamp, screenUV, 0);

                float eyeDepth = IsOrthographicCamera() ? OrthographicDepthBufferToLinear(bufferDepth) 
                    : LinearEyeDepth(bufferDepth, _ZBufferParams);

                //渲染到的物体的起点
                float3 bufferPos = GetWorldPos(bufferDepth, screenUV);
                
                //追踪出盒子的起点以及终点耗时
                RayTracingBox(direction, input.boxMaxPositionWS, input.boxMinPositionWS, inT, outT, _WorldSpaceCameraPos);

                float3 targetPos = _WorldSpaceCameraPos + direction * outT;


                float3 beginPos = _WorldSpaceCameraPos + direction * max(inT, 0);
                float3 end;

                //如果物体的起点比盒子的起点更近，就是被物体挡住，不渲染
                if (Check1Near2(_WorldSpaceCameraPos, bufferPos, beginPos)) return float4(0, 0, 0, 0);

                if(Check1Near2(_WorldSpaceCameraPos, bufferPos, targetPos)) end = bufferPos;
                else end = targetPos;


                float3 color = 0;
                float seed = 0;
                // float2 jitter = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_linear_repeat, (screenUV * 0.5) * _ScreenParams.xy, 0).xy;
                float2 jitter = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_linear_repeat, screenUV * (_ScreenParams.xy / 30), 0).xy;
                seed = frac(jitter.x + jitter.y);

                float3 step = (end - beginPos) / _RayCount;
                float3 current = beginPos;
                [unroll(36)] 
                for(uint i = 0; i < _RayCount; i++){
                    float3 uvw = LineraLerp(current + step * seed, input.boxMinPositionWS, input.boxMaxPositionWS);
                    color += SAMPLE_TEXTURE3D(_Light3D, sampler_Light3D, uvw).rgb;
                    current += step;
                }
                // color /= _RayCount;
                color *= _BulkLightShrinkRadio * length(step) / _RayCount * _LightIntensity;
                color = max(0, color);

                return float4(color, 1);
            }




            ENDHLSL
        }
    }
}
