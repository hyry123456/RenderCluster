Shader "Unlit/PlaneText"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass{
            Blend One Zero
            ZWrite On
            ZTest On
            HLSLPROGRAM
                #include "../../ShaderLibrary/Common.hlsl"
                #include "../../CommonInput/CommonInput.hlsl"
                #include "Input/PlaneInput.hlsl"

                #pragma vertex vert
                #pragma fragment frag
                struct appdata
                {
                    float4 vertex : POSITION;
                    float2 uv : TEXCOORD0;
                    uint instanceID : SV_InstanceID;
                    half4 color : COLOR;
                };

                struct OutVert {
                    float4 pos : SV_POSITION;
                    float3 positionWS : VAR_POSITION;     //XYZ:worldPos
                    float4 uv : UV;
                };

                OutVert vert(appdata v) {
                    OutVert o;
                    uint4 node = _Nodes[v.instanceID];
                    float4 nodeSize = _NodeSize[node.z];
                    v.vertex.xyz /= _PointCount;
                    v.vertex.xz = nodeSize * (node.xy + v.vertex.xz);
                    o.uv.xy = v.uv;

/////////////////////////////边界检测，往下拉/////////////////////////////////////////////////////////
                    uint mode = v.color.y * 255;
                    if(mode & node.w){
                        v.vertex.y = -100;
                    }
/////////////////////////////边界检测，往下拉/////////////////////////////////////////////////////////
                    v.vertex.xyz += _BeginPosition;
                    o.pos = TransformWorldToHClip(v.vertex.xyz);
                    o.positionWS = v.vertex.xyz;
                    return o;
                }

                float4 frag(OutVert i) : SV_TARGET{
                    return float4(i.uv.xyz, 1);
                    // return i.uv.z;
                }
            ENDHLSL
        }

        Pass{
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest On
            HLSLPROGRAM
                #include "../../ShaderLibrary/Common.hlsl"
                #include "../../CommonInput/CommonInput.hlsl"
                #include "Input/PlaneInput.hlsl"

                #pragma vertex vert
                #pragma fragment frag
                struct appdata
                {
                    float4 vertex : POSITION;
                    uint instanceID : SV_InstanceID;
                };

                struct OutVert {
                    float4 pos : SV_POSITION;
                };

                OutVert vert(appdata v) {
                    OutVert o;
                    Bounds bound = BoundsList[v.instanceID];
                    v.vertex.xyz = bound.minPosition + (bound.maxPosition - bound.minPosition) * v.vertex.xyz;
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
