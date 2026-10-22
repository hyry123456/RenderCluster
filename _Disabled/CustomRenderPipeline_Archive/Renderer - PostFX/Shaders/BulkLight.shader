Shader "CustomRP/PostEffect/BulkLight"
{
    SubShader
    {
        Pass
        {
            Name "Static Bulk Light"

            Blend One One
            ZWrite Off
            Cull Front
            HLSLPROGRAM

            #pragma target 4.6

            #pragma multi_compile _ _DRAW_OTHER_LIGHT
            #pragma multi_compile _ _DRAW_DIRECTION_LIGHT
            #pragma vertex vert
            #pragma fragment frag
            
            #include "../../ShaderLibrary/Common.hlsl"
            #include "../Input/BulkLightPass.hlsl"

            ENDHLSL
        }
    }
}
