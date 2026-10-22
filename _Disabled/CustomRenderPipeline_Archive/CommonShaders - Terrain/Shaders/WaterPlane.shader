Shader "Unlit/WaterPlane"
{
    SubShader
    {
        HLSLINCLUDE
            #include "../../../ShaderLibrary/Common.hlsl"
            #include "../../../CommonInput/CommonInput.hlsl"
            #include "../../../ShaderLibrary/Surface.hlsl"
            #include "../../../ShaderLibrary/Shadows.hlsl"
            #include "../../../ShaderLibrary/Light.hlsl"
            #include "../../../ShaderLibrary/BRDF.hlsl"
            #include "../../../ShaderLibrary/GI.hlsl"
            #include "../../../ShaderLibrary/Lighting.hlsl"
            #include "../../../ShaderLibrary/Fragment.hlsl"

            #include "../../../DefferPipeline/DeffererLibrary/DeffererInput.hlsl"

            #include "../Input/PlaneInput.hlsl"
            #include "../Input/OceanPlane.hlsl"

            #define _BumpSize 0.3

            TEXTURECUBE(_CubeMap);
            SAMPLER(sampler_CubeMap);
            // float4 _HorizontalColor;
            // TEXTURE2D(_BumpMap);
            // TEXTURE2D(_ScreenCopyTexture);
            TEXTURE2D(_WaterColorRamp);
            // TEXTURE2D(_CausticColorRamp);
            // TEXTURE2D(_FoamTex);
            // float4 _CausticColor;
            // float2 _CausticData;                //x:center, y: range
            // float4 _WaterColor;
            // float _ColorRange;
            float4 _PlaneSize;                   //x：水面长度，z:水面宽度, y:水面深度，用来控制深度映射效果,w:水面高度
            float _RampRange;
            // float2 _WaterUp_Down;                //水的顶端高度和深度的值，x:up,y:down
            TEXTURE2D(_FlowMap);
            TEXTURE2D(_WaveTopMap);
            TEXTURE2D(_NormalMap);
            float4 _NormalMap_ST;
            // float _WaveSpeed;
            float _FlowSpeed;
            float _FlowStrength;
            float _Tiling;              //控制整体的缩放情况
            float _Roughness;
            float _NormalScale;
            float2 _WaveTopMask;
            TEXTURE2D(_ColorMap);
            TEXTURE2D(_GrandDepthTex);

            inline half3 ReflectProbe(half3 refDir, float mip_Level)
            {
                float4 environment = SAMPLE_TEXTURECUBE_LOD(
                    _CubeMap, sampler_CubeMap, refDir, mip_Level
                );
                return environment.xyz;
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

            inline float3x3 TBN() {
                float3 T = float3(1, 0, 0);
                float3 B = float3(0, 0, 1);
                float3 N = float3(0, 1, 0);
                float3x3 TBN = float3x3(T, B, N);
                return TBN;
            }

            float3 getFlowNormal_float(float2 uv, float4 normalTex_ST) {
                float4 v_flowTex = SAMPLE_TEXTURE2D(_FlowMap, sampler_linear_repeat, uv) * 2.0 - 1.0;
                float2 flow_dir = v_flowTex.xy * _FlowStrength;  // uv 扰动方向向量
                float noise = v_flowTex.a;

                float phase = _Time.x * _FlowSpeed;
                float phase0 = frac(phase + noise);
                float phase1 = frac(phase + 0.5 + noise);  // +0.5 使两个相位的波峰波谷交错出现

                float2 uv_jump = float2(0.25, 0.1);
                float2 phase0_jump = (phase - phase0) * uv_jump;
                float2 phase1_jump = (phase - phase1) * uv_jump;

                float2 uv_st = uv * normalTex_ST.xy + normalTex_ST.zw;

                float2 uv0 = uv_st - phase0 * flow_dir + phase0_jump;
                float2 uv1 = uv_st - phase1 * flow_dir + phase1_jump;

                float3 tex0 = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_linear_repeat, uv0), _NormalScale);
                float3 tex1 = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_linear_repeat, uv1), _NormalScale) ;

                float3 normalWS_0 = normalize(mul(tex0, TBN()));
                float3 normalWS_1 = normalize(mul(tex1, TBN()));

                float flowLerp = abs(1 - 2 * phase0);
                return normalize(lerp(normalWS_0, normalWS_1, flowLerp));
            }

        ENDHLSL

        Pass{
            Name "Draw GBuffer Reflect"
            Blend One Zero
            ZWrite On
            ZTest On
            HLSLPROGRAM

                #pragma vertex vert
                #pragma fragment frag

                #pragma multi_compile _ LIGHTMAP_ON
                #pragma multi_compile _ _DIRECTIONAL_PCF3 _DIRECTIONAL_PCF5 _DIRECTIONAL_PCF7
                #pragma multi_compile _ _OTHER_PCF3 _OTHER_PCF5 _OTHER_PCF7
                #pragma multi_compile _ _CASCADE_BLEND_SOFT _CASCADE_BLEND_DITHER

                struct appdata
                {
                    float4 vertex : POSITION;
                    float2 uv : TEXCOORD0;
                    uint instanceID : SV_InstanceID;
                };


                struct OutVert {
                    float4 pos : SV_POSITION;
                    float3 positionWS : VAR_POSITION;     //XYZ:worldPos
                    float2 baseUV : UV_BASE;        //01uv
                    float2 waterHeight : WATER_HEIGHT;       //x:水高度值，即从水的最高点(为0)开始算，当前水的高度值, y:objPos.Y
                    float3 baseNormal : NORMAL;
                };

                OutVert vert(appdata v) {
                    OutVert o;
                    RenderPatch patch = PatchList[v.instanceID];
                    float2 nodeSize = _NodeSize[patch.lod.z].xy;

                    v.vertex.xyz = FixVertex(v.vertex.xyz, patch.lod, v.uv);

                    v.vertex.xz *= nodeSize / 64;       //first 8 is divided into 8 pieces, second 8 is one patch 8m
                    v.vertex.x += patch.position.x;
                    v.vertex.z += patch.position.y;

                    float2 uv01 = (v.vertex.xz / _PlaneSize.xz);
                    float depth = SAMPLE_TEXTURE2D_LOD(_GrandDepthTex, sampler_linear_repeat, uv01, 0).x * _PlaneSize.y;
                    float waterGrandDistance = max(depth - _PlaneSize.w, 0);
                    float steepnessAdjust = lerp(_MinSteepness, _MaxSteepness, smoothstep(0, 3, waterGrandDistance));
                    // waterGrandDistance = smoothstep(0, 3, waterGrandDistance);
                    Gerstner gerstner = GerstnerWave(_WaveMainDirection, v.vertex.xyz, 
                        _WaveCount, 
                        _MaxWaveLength, 
                        _MinWaveLength, steepnessAdjust, _MinSteepness, _RandomIntensity);

                    float3 normal = 0;
                    // Gerstner gerstner = GerstnerWave(_WaveMainDirection, v.vertex.xyz, 
                    //     _WaveCount, 
                    //     _MaxWaveLength, 
                    //     _MinWaveLength, _MaxSteepness, _MinSteepness, _RandomIntensity);




                    v.vertex.xyz = gerstner.positionWS;
                    float3 binormal = gerstner.binormal;
                    float3 tangent = gerstner.tangent;
                    float3 normalWS = normalize(cross(binormal,tangent));

                    // v.vertex.y = SAMPLE_TEXTURE2D_LOD(_HightMap, sampler_point_repeat, v.vertex.xz * 0.01, 0).r * 10;
                    o.baseUV = (v.vertex.xz / _PlaneSize.xz) * _Tiling;
                    o.waterHeight = float2(abs(v.vertex.y - _PlaneSize.w), v.vertex.y);
                    

                    v.vertex = mul(_ObjectToWorld, v.vertex);


                    o.pos = TransformWorldToHClip(v.vertex.xyz);
                    o.positionWS = v.vertex.xyz;
                    o.baseNormal = normalWS;
                    
                    return o;
                }



                float4 frag (OutVert i) : SV_TARGET
                {
                    Fragment fragment = GetFragment(i.pos);
                    // float2 baseUV = i.baseUV;

                    // //这个深度差一般只用来处理近岸的水波效果，而水面的透明效果建议还是用回深度来算
                    // float rampX = saturate(waterGrandDistance / _RampRange);
                    // float4 baseCol = SAMPLE_TEXTURE2D_LOD(_WaterColorRamp, sampler_linear_clamp, float2(rampX, 0.5f), 0);


                    float rampX = saturate((fragment.bufferDepth - fragment.depth) / _RampRange);
                    float4 base = SAMPLE_TEXTURE2D_LOD(_WaterColorRamp, sampler_linear_clamp, float2(rampX, 0.5f), 0);
                    // return baseCol;

                    // float3 normal = getFlowNormal_float(i.baseUV, _FlowSpeed);
                    // float2 baseUV = 
                    float3 normal_Wave = getFlowNormal_float(i.baseUV, _NormalMap_ST);
                    // float3 normal = normalize( BlendNormalRNM(normal_Wave, i.baseNormal) );
                    float3 normal = (normal_Wave + i.baseNormal) / 2;

                    Surface surface = (Surface)0;
	                surface.position = i.positionWS;
                    surface.normal = normal;
                    surface.viewDirection = normalize(_WorldSpaceCameraPos - i.positionWS);
                    surface.depth = -TransformWorldToView(i.positionWS).z;
                    surface.color = base.rgb;
                    surface.alpha = base.a;
                    surface.metallic = 0.67f;
                    surface.roughness = _Roughness;
                    surface.ambientOcclusion = 1;
                    surface.dither = InterleavedGradientNoise(i.pos.xy, 0);
                    float3 specColor = GetLight_Spec(surface);

                    float3 viewDirection = normalize(_WorldSpaceCameraPos - i.positionWS);
                    float3 reflect_dir = reflect(-viewDirection, normal);
                    float3 reflectCol = ReflectProbe(reflect_dir, 0) ;

                    half oneMinusReflectivity = (1 - surface.metallic) * 0.96;
                    half nv = saturate(dot(normal, surface.viewDirection));

                    half3 reSpecColor = lerp(0.14, reflectCol.xyz, surface.metallic);

                    half grazingTerm = saturate((1 - surface.roughness) + (1 - oneMinusReflectivity));
                    reflectCol *= ComputeFresnelLerp(reSpecColor, grazingTerm, nv);

                    float4 grandColor = SAMPLE_TEXTURE2D_LOD(_ColorMap, sampler_linear_repeat, fragment.screenUV + normal.xz * 0.02f, 0);
                    grandColor *= base;
                    // float3 finalCol = (reflectCol + grandColor.xyz) / 2;
                    float3 finalCol = lerp(grandColor, reflectCol, clamp(rampX, 0.1f, 0.8f));
                    
                    float maskHeight = smoothstep(_WaveTopMask.x, _WaveTopMask.y, i.waterHeight.y);
                    float3 topMap = SAMPLE_TEXTURE2D(_WaveTopMap, sampler_linear_repeat, 
                        i.positionWS.xz + _Time.x * -_WaveMainDirection).xyz * maskHeight;
                    // return screenDepth;
                    return float4(specColor * rampX + finalCol + topMap, 1);
                    // return float4(i.baseNormal, 1);
                    // return float4(normal, 1);
                    // return base;

                    //1:根据深度差，确定水体颜色.
                    //2:根据水底的世界坐标和水面的距离，计算水底的灯光波纹(建议这样算，这样才是正确的效果)
                    //3:靠近地面的部分，绘制水波纹效果
                    

                    // return waterGrandDistance;
                    // return _PlaneSize.y;
                    // return float4(i.baseUV, 0, 1);
                    // return 0.5f;
                    // return color * 0.5;
                    // return 1;
                    // float4 map0 = SAMPLE_TEXTURE2D(_BumpMap, sampler_linear_repeat, i.bumpUV.xy); float3 bump0 = DecodeNormal(map0, _BumpSize);
                    // float4 map1 = SAMPLE_TEXTURE2D(_BumpMap, sampler_linear_repeat, i.bumpUV.zw); float3 bump1 = DecodeNormal(map1, _BumpSize);
                    // float3 bump = (bump0 + bump1) * 0.5;
                    // // float3 normal = BlendTriplanarNormal(bump, i.normal);
                    // float3 normal = i.normal;

                    // float2 normalPack = PackNormal(normal);
                    // Fragment f = GetFragment(i.pos);

                    // float2 refrOffV;
                    // float refrOff = GetBufferDepth(f, normal.xz * 0.02f);
                    // refrOffV = normal.xz * 0.05f;

                    // float bufferDepth = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, sampler_point_clamp, f.screenUV, 0);
                    // float3 bufferPos = GetWorldPos(bufferDepth, f.screenUV);

                    // float distanceDelta = i.worldPos.y - bufferPos.y;	//获得深度差
                    // float rampX = saturate(distanceDelta / _ColorRange);	//进行透明控制


                    // float3 viewDirection = normalize(_WorldSpaceCameraPos - i.worldPos);
                    // float3 reflect_dir = reflect(-viewDirection, normal);
                    // float3 refl = ReflectProbe(reflect_dir, 0);

	                // target._GBufferRT0 = float4(1, 1, 1, 1);
	                // target._GBufferRT1 = float4(normalPack, 1, 0);
	                // target._GBufferRT2 = float4(0, 0, 0, 0.05);
	                // target._GBufferRT3 = float4(refl, rampX);
                }

            ENDHLSL
        }

        // Pass{
        //    Name "Draw GBuffer Reflect"
        //     Blend SrcAlpha OneMinusSrcAlpha
        //     ZWrite On
        //     HLSLPROGRAM

        //         #pragma vertex vert
        //         #pragma fragment frag




        //         // TEXTURECUBE(_Skybox);
        //         // SAMPLER(sampler_Skybox);

        //         struct appdata
        //         {
        //             float4 vertex : POSITION;
        //             float2 uv : TEXCOORD0;
        //             uint instanceID : SV_InstanceID;
        //         };


        //         struct OutVert {
        //             float4 pos : SV_POSITION;
        //             float3 normal : NORMAL;
        //             float3 worldPos : VAR_POSITION;
        //             float4 bumpUV : NOISE_UV;
        //             float2 moveDir : TEXCOORD0;
        //         };

        //         OutVert vert(appdata v) {
        //             OutVert o;
        //             RenderPatch patch = PatchList[v.instanceID];
        //             float2 nodeSize = _NodeSize[patch.lod.z].xy;

        //             v.vertex.xyz = FixVertex(v.vertex.xyz, patch.lod, v.uv);

        //             v.vertex.xz *= nodeSize / 64;       //first 8 is divided into 8 pieces, second 8 is one patch 8m
        //             v.vertex.x += patch.position.x;
        //             v.vertex.z += patch.position.y;

        //             // v.vertex.y = SAMPLE_TEXTURE2D_LOD(_HightMap, sampler_point_repeat, v.vertex.xz * 0.01, 0).r * 10;

        //             v.vertex = mul(_ObjectToWorld, v.vertex);
        //             float3 normal = 0;

        //             GetWave(v.vertex.xyz, normal);

        //             o.pos = TransformWorldToHClip(v.vertex.xyz);
                    
        //             o.normal = mul((float3x3)_ObjectToWorld, normal);
        //             o.worldPos = v.vertex.xyz;
        //             float4 moveDir; 
        //             moveDir.xy = _WaveDir0.xy + _WaveDir1.xy * 0.25; 
        //             moveDir.zw = -(_WaveDir0.zw * 0.5 + _WaveDir1.zw * 0.125);
        //             moveDir *= _Time.x * _Speed * 3;
        //             float4 noise = v.vertex.xzxz * float4(_WaveScale, _WaveScale, _WaveScale * 0.4, _WaveScale * 0.45) + moveDir;
        //             o.bumpUV = noise;
        //             o.moveDir = (moveDir.xy + moveDir.zw) * 3;

        //             return o;
        //         }




        //         float4 frag (OutVert i) : SV_TARGET 
        //         {
        //             // return float4(i.bumpUV.xy, 0, 1);
        //             float4 map0 = SAMPLE_TEXTURE2D(_BumpMap, sampler_linear_repeat, i.bumpUV.xy); float3 bump0 = DecodeNormal(map0, _BumpSize);
        //             float4 map1 = SAMPLE_TEXTURE2D(_BumpMap, sampler_linear_repeat, i.bumpUV.zw); float3 bump1 = DecodeNormal(map1, _BumpSize);
        //             float3 bump = (bump0 + bump1) * 0.5;
        //             float3 normal = BlendTriplanarNormal(bump, i.normal);
                    
        //             Fragment f = GetFragment(i.pos);

        //             float2 refrOffV;
        //             float refrOff = GetBufferDepth(f, normal.xz * 0.02f);
        //             refrOffV = normal.xz * 0.05f;

        //             float bufferDepth = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, sampler_point_clamp, f.screenUV + refrOffV, 0);
        //             float3 bufferPos = GetWorldPos(bufferDepth, f.screenUV);

        //             float distanceDelta = i.worldPos.y - bufferPos.y;	//获得深度差
        //             float rampX = saturate(distanceDelta / _ColorRange);	//进行透明控制


        //             float3 rampColor = SAMPLE_TEXTURE2D_LOD(_WaterColorRamp, sampler_linear_clamp, float2(rampX, 0.5f), 0).xyz;

        //             float3 refrCol = SAMPLE_TEXTURE2D_LOD(_ScreenCopyTexture, sampler_linear_clamp, f.screenUV + refrOffV, 0).xyz * rampColor;

        //             float caustic = CausticCreate(bufferPos.xz + i.moveDir, _Time.y);

        //             float3 causticCol = SAMPLE_TEXTURE2D_LOD(_CausticColorRamp, sampler_linear_clamp, float2(caustic, 0.5f), 0).xyz * caustic;

        //             float causticRange = pow( saturate(1.0 - abs(distanceDelta - _CausticData.x) / _CausticData.y), 5 );

        //             float3 baseCol = lerp(refrCol, refrCol + causticCol, causticRange);


        //             float3 viewDirection = normalize(_WorldSpaceCameraPos - i.worldPos);
        //             float3 reflect_dir = reflect(-viewDirection, normal);
        //             float3 refl = ReflectProbe(reflect_dir, 0) * 0.3;
        //             baseCol += refl * rampX;
                    

        //             return float4(baseCol, 1);
                    
        //         }

        //     ENDHLSL
        // }



    }
}
