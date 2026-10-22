Shader "Hidden/WriteInChannel"
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

            sampler2D _Origin, _Result;
            float4 _Range;
            float4 _Mode;
            int _IsMinus;

            float4 frag (v2f i) : SV_Target
            {
                float originVal, col;
                int mode1 = (int)_Mode.x;
                switch (mode1) {
                    case 0:
                        col = tex2D(_Origin, i.uv).x;
                        originVal = _IsMinus? 1.0f - col : col;
                        break;
                    case 1:
                        col = tex2D(_Origin, i.uv).y;
                        originVal = _IsMinus ? 1.0f - col : col;
                        break;
                    case 2:
                        col = tex2D(_Origin, i.uv).z;
                        originVal = _IsMinus ? 1.0f - col : col;
                        break;
                    case 3:
                        col = tex2D(_Origin, i.uv).w;
                        originVal = _IsMinus ? 1.0f - col : col;
                        break;
                    case 4:
                        float4 cols = tex2D(_Origin, i.uv);
                        float gray = cols.x * 0.299 +
                            cols.y * 0.587 + cols.z * 0.114;
                        originVal = _IsMinus ? 1.0f - gray : gray;
                        break;
                    case 5:
                        originVal = 1;
                        break;
                    default:
                        originVal = 0;
                        break;
                }

                originVal = _Range.x + saturate( (_Range.y - _Range.x) * originVal);

                float4 writeCol = tex2D(_Result, i.uv);
                int mode2 = (int)_Mode.y;
                switch (mode2) {
                    case 0:
                        writeCol.x = originVal;
                        break;
                    case 1:
                        writeCol.y = originVal;
                        break;
                    case 2:
                        writeCol.z = originVal;
                        break;
                    case 3:
                        writeCol.w = originVal;
                        // writeCol.w = 0.5;
                        // writeCol.x = 0.8;
                        break;
                }
                return writeCol;
            }
            ENDCG
        }
    }
}
