Shader "Hidden/PackDetail"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            // Blend One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };



            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            sampler2D _Albedo, _Normal, _Roughness;

            // Returns ±1
            float2 signNotZero(float2 v) {
                return float2((v.x >= 0.0) ? +1.0 : -1.0, (v.y >= 0.0) ? +1.0 : -1.0);
            }
            // Assume normalized input. Output is on [-1, 1] for each component.
            float2 float32x3_to_oct(in float3 v) {
                // Project the sphere onto the octahedron, and then onto the xy plane
                float2 p = v.xy * (1.0 / (abs(v.x) + abs(v.y) + abs(v.z)));
                // Reflect the folds of the lower hemisphere over the diagonals
                return (v.z <= 0.0) ? ((1.0 - abs(p.yx)) * signNotZero(p)) : p;
            }
            float3 oct_to_float32x3(float2 e) {
                float3 v = float3(e.xy, 1.0 - abs(e.x) - abs(e.y));
                if (v.z < 0) v.xy = (1.0 - abs(v.yx)) * signNotZero(v.xy);
                return normalize(v);
            }

            static float2 PackNormal(float3 normalWS){
                float2 re = 0;
                re.xy = float32x3_to_oct(normalWS);
                re.xy = re.xy * 0.5 + 0.5;
                return re;
            }


            float4 frag (v2f i) : SV_Target
            {
                float albedo = tex2D(_Albedo, i.uv).x;
                float4 normal = tex2D(_Normal, i.uv);
                float roughness = tex2D(_Roughness, i.uv).x;

                float2 packNormal = PackNormal(normal.xyz);


                return float4(albedo, packNormal.x, roughness, packNormal.y);
            }
            ENDCG
        }
    }
}
