Shader "Unlit/PlaneDrawTemp"
{
    // Properties
    // {
    //     _Steepness("Steepness", Range(0, 0.5)) = 0.1
    //     _WaveLength ("Wave Length", float) = 5
    //     _Speed("Speed", float) = 1
    //     _WaveDir0("Direction0", Vector) = (1, -1, 1, 1)
    //     _WaveDir1("Direction1", Vector) = (1, 2, 3, -1)

	//     _WaveScale ("Wave scale", Range (0.02,0.15)) = 0.063
    //     // _RefrDistort ("Refraction distort", Range (0,1.5)) = 0.40
    //     // _RefrColor ("Refraction color", COLOR)  = ( .34, .85, .92, 1)
    //     _SpecularColor ("Specular Color", COLOR) = (1, 1, 1, 1)
    //     _NearColor ("Near Side Color", Color) = (1, 1, 1, 1)
    //     [NoScaleOffset] _BumpMap ("Normalmap ", 2D) = "bump" {}
    //     WaveSpeed ("Wave speed (map1 x,y; map2 x,y)", Vector) = (19,9,-16,-7)
    //     [NoScaleOffset] _ReflectiveColor ("Reflective color (RGB) fresnel (A) ", 2D) = "" {}
    //     _WaterColor ("Water color", COLOR)  = ( .172, .463, .435, .3)

    //     _Gloss("Specular Scale", Float) = 50
    //     _NearDistance("Near Distance", Range(0.0, 10.0)) = 0.01
    //     _NearRange ("Near Range", Range(0.01, 10.0)) = 1

	// 	// [Toggle(_IS_REFR)] _Is_Refr ("Use Refrect Color", Float) = 0
	// 	// [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
	// 	// [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
    // }
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

            // #include "../Input/TerrainInclude.hlsl"
        ENDHLSL

        Pass{
            Name "GPU Terrain GBuffer"

            Tags {
                "LightMode" = "OutGBuffer"
            }
            Blend One Zero
            ZWrite On


            HLSLPROGRAM

                #pragma vertex vert
                #pragma fragment frag
                // #pragma shader_feature _DEBUG
                struct RenderPatch{
                    float2 position;
                    uint4 lod;      //xyz:nodeLod, w:patch offset
                };
                // #include "HLSL/GPUTerrainPass.hlsl"
                float4 _NodeSize[5];       //每一级的x, z的大小，xy=xz的大小
                StructuredBuffer<RenderPatch> PatchList;
                float _LodMapPixelSize[5];      //LodMap的每一级的像素量
                uint _LodLevel;
                float4x4 _ObjectToWorld;
                TEXTURE2D(_LodMap);
                TEXTURE2D(_HightMap);

                static float NodeCount[] = {0, 4.0, 2.0, 1.0, 0.5};

                struct appdata
                {
                    float4 vertex : POSITION;
                    float2 uv : TEXCOORD0;
                    uint instanceID : SV_InstanceID;
                };

                struct OutVert {
                    float4 pos : SV_POSITION;
                    float3 color : BASE_UV;
                };

                bool IsFix(float3 objPos, uint4 lod, float2 objUV, out float3 debug){
                    uint row = lod.w / 8; uint col = lod.w % 8;
                    float pixelSize = _LodMapPixelSize[lod.z];
                    float2 uv = (float2)lod.xy * pixelSize + pixelSize / 2;      //node center
                    debug = 0;

                    float nodeZ = lod.z / (float)_LodLevel;

                    if(objUV.x == 0.0 && row == 0){     //左边
                        float left = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(-pixelSize, 0), 0).x;
                        float offset = left * _LodLevel - (float)lod.z;
                        int offsetI = floor(offset + 0.1);
                        if(offsetI >= 1)
                            debug = offsetI;
                    }
                    if(objUV.x == 1.0 && row == 7){
                        float right = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(pixelSize, 0), 0).x;
                        float offset = right * _LodLevel - (float)lod.z;
                        int offsetI = floor(offset + 0.1);
                        if(offsetI >= 1)
                            debug = offsetI;
                    }

                    if(objUV.y == 1.0 && col == 7){     //上边
                        float up = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, pixelSize), 0).x;
                        float offset = up * _LodLevel - (float)lod.z;
                        int offsetI = floor(offset + 0.1);
                        if(offsetI >= 1)
                            debug = offsetI;
                    }
                    else if(objUV.y == 0.0 && col == 0){     //下边
                        float down = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, -pixelSize), 0).x;
                        float offset = down * _LodLevel - (float)lod.z;
                        int offsetI = floor(offset + 0.1);
                        if(offsetI >= 1)
                            debug = offsetI;
                    }
                    return false;
                }

                #define random(seed) sin(seed * 641.5467987313875 + 1.943856175)

                float3 FixVertex(float3 vertex, uint4 lod, float2 objUV){
                    uint row = lod.w / 8; uint col = lod.w % 8;
                    float pixelSize = _LodMapPixelSize[lod.z];
                    float2 uv = (float2)lod.xy * pixelSize + pixelSize / 2;      //node center

                    if(objUV.x == 0.0 && row == 0){     //左边
                        float left = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(-pixelSize, 0), 0).x;
                        float offset = left * _LodLevel - (float)lod.z;
                        int offsetI = floor(offset + 0.1);
                        if(offsetI >= 1)
                        {
                            //将fixRange内的顶点都合并在同一个位置
                            float fixRange = NodeCount[_LodLevel] * pow(2.0, offsetI);
                            float zpos = floor(vertex.z / fixRange);
                            vertex.z = zpos * fixRange;
                        }
                    }
                    else if(objUV.x == 1.0 && row == 7){
                        float right = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(pixelSize, 0), 0).x;
                        float offset = right * _LodLevel - (float)lod.z;
                        int offsetI = floor(offset + 0.1);
                        if(offsetI >= 1)
                        {
                            //将fixRange内的顶点都合并在同一个位置
                            float fixRange = NodeCount[_LodLevel] * pow(2.0, offsetI);
                            float zpos = floor(vertex.z / fixRange);
                            vertex.z = zpos * fixRange;
                        }
                    }

                    if(objUV.y == 1.0 && col == 7){     //上边
                        float up = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, pixelSize), 0).x;
                        float offset = up * _LodLevel - (float)lod.z;
                        int offsetI = floor(offset + 0.1);
                        if(offsetI >= 1)
                        {
                            //将fixRange内的顶点都合并在同一个位置
                            float fixRange = NodeCount[_LodLevel] * pow(2.0, offsetI);
                            float xpos = floor(vertex.x / fixRange);
                            vertex.x = xpos * fixRange;
                        }
                    }
                    else if(objUV.y == 0.0 && col == 0){     //下边
                        float down = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, -pixelSize), 0).x;
                        float offset = down * _LodLevel - (float)lod.z;
                        int offsetI = floor(offset + 0.1);
                        if(offsetI >= 1)
                        {
                            //将fixRange内的顶点都合并在同一个位置
                            float fixRange = NodeCount[_LodLevel] * pow(2.0, offsetI);
                            float xpos = floor(vertex.x / fixRange);
                            vertex.x = xpos * fixRange;
                        }
                    }
                    return vertex;
                }

                OutVert vert(appdata v) {
                    OutVert o;
                    RenderPatch patch = PatchList[v.instanceID];
                    float2 nodeSize = _NodeSize[patch.lod.z].xy;
                    float3 va;
                    if(IsFix(v.vertex.xyz, patch.lod, v.uv, va)){
                        // o.color = float3(0, 0, 1);
                        o.color = 0;
                    }
                    else{
                        o.color = 0;
                    }
                    // o.color += va;

                    v.vertex.xyz = FixVertex(v.vertex.xyz, patch.lod, v.uv);

                    v.vertex.xz *= nodeSize / 64;       //first 8 is divided into 8 pieces, second 8 is one patch 8m
                    v.vertex.x += patch.position.x;
                    v.vertex.z += patch.position.y;

                    // v.vertex.y = SAMPLE_TEXTURE2D_LOD(_HightMap, sampler_point_repeat, v.vertex.xz * 0.01, 0).r * 10;

                    v.vertex = mul(_ObjectToWorld, v.vertex);

                    o.pos = TransformWorldToHClip(v.vertex.xyz);
                    o.color += float3(v.uv, 0) * 0.3;
                    return o;
                }

                float4 frag(OutVert i) : SV_TARGET{
                    return float4(i.color.xyz, 1);
                }


            ENDHLSL
        }

        Pass{
            Name "GPU Terrain GBuffer"

            Tags {
                "LightMode" = "OutGBuffer"
            }
            Blend SrcAlpha One
            ZWrite Off


            HLSLPROGRAM

                #pragma vertex vert
                #pragma fragment frag
                float4x4 _ObjectToWorld;

                static float NodeCount[] = {0, 4.0, 2.0, 1.0, 0.5};
                struct Bounds{
                    float3 minPosition;
                    float3 maxPosition;
                };
                StructuredBuffer<Bounds> BoundsList;    //Bounds写入的目标位置

                struct appdata
                {
                    float4 vertex : POSITION;
                    float2 uv : TEXCOORD0;
                    uint instanceID : SV_InstanceID;
                };

                struct OutVert {
                    float4 pos : SV_POSITION;
                };

                OutVert vert(appdata v) {
                    OutVert o;
                    Bounds bound = BoundsList[v.instanceID];
                    v.vertex.xyz *= (bound.maxPosition - bound.minPosition);
                    v.vertex.xyz += bound.minPosition;
                    o.pos = TransformWorldToHClip(v.vertex.xyz);

                    return o;
                }

                float4 frag(OutVert i) : SV_TARGET{
                    return 0.5;
                }


            ENDHLSL
        }



    }
}
