Shader "CustomRP/Terrain/GPUTerrainPro"
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
        ENDHLSL

        Pass{
            Name "Draw GBuffer Reflect"
            Blend One Zero
            ZWrite On
            HLSLPROGRAM

                #pragma vertex vert
                #pragma fragment frag


                float3 _BeginPosition;
                struct appdata
                {
                    float4 vertex : POSITION;
                    float2 uv : TEXCOORD0;
                    uint instanceID : SV_InstanceID;
                };

                float4 _TerrainSize;
                TEXTURE2D(_HeighMap);


                struct OutVert {
                    float4 pos : SV_POSITION;
                    float2 uv : VAR_UV;
                };

                OutVert vert(appdata v) {
                    OutVert o;
                    RenderPatch patch = PatchList[v.instanceID];
                    float2 nodeSize = _NodeSize[patch.lod.z].xz;

                    v.vertex.xyz = FixVertex(v.vertex.xyz, patch.lod, v.uv);

                    v.vertex.xz *= nodeSize / 64;       //first 8 is divided into 8 pieces, second 8 is one patch 8m
                    v.vertex.x += patch.position.x;
                    v.vertex.z += patch.position.y;
                    float2 worldUV = v.vertex.xz / _TerrainSize.xz;
                    
                    float heigh = SAMPLE_TEXTURE2D_LOD(_HeighMap, sampler_linear_clamp, worldUV, 0).x * _TerrainSize.y;
                    v.vertex.y = heigh;
                    v.vertex.xyz += _BeginPosition;

                    o.pos = TransformWorldToHClip(v.vertex.xyz);
                    o.uv = v.uv;
                    return o;
                }

                float4 frag (OutVert i)  : SV_TARGET
                {
                    return float4(i.uv, 0, 1);
                }

            ENDHLSL
        }

        Pass{

            Tags {
                "LightMode" = "OutGBuffer"
            }
            Blend SrcAlpha One
            ZWrite Off


            HLSLPROGRAM

                #pragma vertex vert
                #pragma fragment frag
                float3 _BeginPosition;

                // static float NodeCount[] = {0, 4.0, 2.0, 1.0, 0.5};
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
