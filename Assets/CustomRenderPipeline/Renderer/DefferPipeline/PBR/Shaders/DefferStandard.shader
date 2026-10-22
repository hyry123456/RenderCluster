Shader "CustomRP/Defferer/DefferStandard"
{
    Properties
	{
        [Main(Surface, _, off, off)]
        _group ("Surface", float) = 0
        [Space()]
		[Sub(Surface)] _MainTex("Texture", 2D) = "white" {}
		[Sub(Surface)] [HDR]_Color("Color", Color) = (0.5, 0.5, 0.5, 1.0)


        [SubToggle(Surface, _MASK_MAP)] _MaskMapToggle("Mask Map", Float) = 0
		//r:metallic Mask, g:roughness Mask, b:detail Mask, a:AO
		[Sub(Surface._MASK_MAP)] _MaskMap("Mask (MODS)", 2D) = "white" {}
		[Sub(Surface)] _Metallic("Metallic", Range(0, 1)) = 0.5
		[Sub(Surface)] _Roughness("Roughness", Range(0.05, 1)) = 0.8

        [SubToggle(Surface, _NORMAL_MAP)] _NormalMapToggle("Normal Map", Float) = 0
		[Tex(Surface._NORMAL_MAP, _NormalScale)] _NormalMap("Normals", 2D) = "bump" {}
		[HideInInspector] _NormalScale("Normal Scale", Range(0, 3.0)) = 1

		
        [Main(ClipSetting, _, off, off)]
        _group3 ("Clip Setting", float) = 0
        [Space()]
        [SubToggle(ClipSetting, _CLIPPING)] _Clipping("Draw Alpha Clipping", Float) = 0
		[Sub(ClipSetting)] _CullOff("Draw Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        [Main(Emission, _, off, off)]
        _group1 ("Emission", float) = 0
        [Space()]
		[Tex(Emission, _EmissionColor)] _EmissionMap("Emission", 2D) = "white" {}
		[HideInInspector][HDR] _EmissionColor("Emission", Color) = (0.0, 0.0, 0.0, 0.0)

        [Main(Detail, _, off, off)]
        _group2 ("Detail", float) = 0
        [Space()]
        [SubToggle(Detail, _DETAIL_MAP)] _DetailMapToggle("Detail Maps", Float) = 0
		[Sub(Detail._DETAIL_MAP)] _DetailMap("Details", 2D) = "linearGrey" {}
		[Tex(Detail._DETAIL_MAP, _DetailNormalScale)] _DetailNormalMap("Detail Normals", 2D) = "bump" {}
		[HideInInspector] _DetailNormalScale("Detail Normal Scale", Range(0, 1)) = 0
		[Sub(Detail._DETAIL_MAP)] _DetailAlbedo("Detail Albedo", Range(0, 1)) = 0
		[Sub(Detail._DETAIL_MAP)] _DetailRoughness("Detail Roughness", Range(0, 1)) = 0

        [Main(QualitySetting, _, off, off)]
        _group4 ("Quality Setting", float) = 0
        [Space()]
        [SubToggle(QualitySetting, _RENDER_LIGHTS)] _RenderLight("Is Render Lights", Float) = 0

		[Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend("Src Blend", Float) = 1
		[Enum(UnityEngine.Rendering.BlendMode)] _DstBlend("Dst Blend", Float) = 0
		[Enum(Off, 0, On, 1)] _ZWrite("Z Write", Float) = 1
		[Enum(UnityEngine.Rendering.CullMode)] _CullBack("Cull Off", Float) = 2
	}
    SubShader
	{
		HLSLINCLUDE
			#include "../../../ShaderLibrary/Common.hlsl"
		ENDHLSL
		Pass
		{
			Tags {
				"Queue" = "Geometry" 
				"LightMode" = "OutGBuffer"
			}
			Cull[_CullBack]
			ZWrite[_ZWrite]

			HLSLPROGRAM
			#pragma vertex LitPassVertex
			#pragma fragment LitPassFragment

			#pragma target 3.5
			#pragma shader_feature _ _CLIPPING
			#pragma shader_feature _ _MASK_MAP
			#pragma shader_feature _ _NORMAL_MAP
			#pragma shader_feature _ _DETAIL_MAP
			#pragma shader_feature _ _PLANE_REFLECT


			#pragma multi_compile _ LIGHTMAP_ON
			#pragma multi_compile _ _MY_LIGHTING
			

			#pragma multi_compile_instancing
            #include "../../../ShaderLibrary/GI.hlsl"
            #include "../../../ShaderLibrary/Surface.hlsl"
            #include "../../../ShaderLibrary/Shadows.hlsl"
            #include "../../../ShaderLibrary/Light.hlsl"
            #include "../../../ShaderLibrary/BRDF.hlsl"
            #include "../../../ShaderLibrary/Lighting.hlsl"
            #include "../../DeffererLibrary/DeffererInput.hlsl"

			#include "../../../CommonInput/PBRInput.hlsl"
			#include "../Input/GBufferPass.hlsl"




			ENDHLSL
		}

		// Pass
		// {
		// 	Tags {
		// 		"LightMode" = "SSRBack"
		// 	}
		// 	Cull Front
		// 	ZWrite On
		// 	ColorMask 0

		// 	HLSLPROGRAM
		// 	#pragma vertex SSRBACKPassVertex
		// 	#pragma fragment SSRBACKPassFragment

		// 	#pragma multi_compile_instancing
		// 	#include "../../../CommonInput/PBRInput.hlsl"
		// 	#include "../../DeffererLibrary/SSRBackPass.hlsl"
		// 	ENDHLSL
		// }

		Pass {
			Tags {
				"LightMode" = "ShadowCaster"
			}

			ColorMask 0

			HLSLPROGRAM
			#pragma target 3.5
			#pragma shader_feature _ _SHADOWS_CLIP _SHADOWS_DITHER
			#pragma shader_feature _ _CLIPPING
			#pragma multi_compile _ LOD_FADE_CROSSFADE
			#pragma multi_compile_instancing
			#pragma vertex ShadowCasterPassVertex
			#pragma fragment ShadowCasterPassFragment
			#include "../../../CommonInput/PBRInput.hlsl"
			#include "../Input/ShadowInput.hlsl"
			ENDHLSL
		}

		Pass{		//透明渲染Pass
			Tags {
				"LightMode" = "CustomLit"
				"Queue" = "Transparent" 
			}

			Blend[_SrcBlend][_DstBlend]
			ZWrite[_ZWrite]

			Cull[_CullBack]

			HLSLPROGRAM
			#pragma vertex LitPassVertex
			#pragma fragment LitPassFragment

			#pragma target 3.5
			#pragma shader_feature _CLIPPING
			#pragma shader_feature _MASK_MAP
			#pragma shader_feature _NORMAL_MAP
			#pragma shader_feature _DETAIL_MAP

			#pragma shader_feature _RENDER_LIGHTS

			#pragma shader_feature _PLANE_REFLECT


			#pragma multi_compile _ LIGHTMAP_ON
			#pragma multi_compile _ _DIRECTIONAL_PCF3 _DIRECTIONAL_PCF5 _DIRECTIONAL_PCF7
			#pragma multi_compile _ _OTHER_PCF3 _OTHER_PCF5 _OTHER_PCF7
			#pragma multi_compile _ _CASCADE_BLEND_SOFT _CASCADE_BLEND_DITHER
			#pragma multi_compile _ LOD_FADE_CROSSFADE

			#pragma multi_compile_instancing
			// #include "../Input/PBRPass.hlsl"
			#include "../../../CommonInput/PBRInput.hlsl"
			#include "../../../CommonInput/PBRPass.hlsl"

			ENDHLSL
		}
		Pass {
			Tags {
				"LightMode" = "Meta"
			}

			Cull Off

			HLSLPROGRAM
			#pragma target 3.5
			#pragma vertex MetaPassVertex
			#pragma fragment MetaPassFragment
			#include "../../../CommonInput/PBRInput.hlsl"
			#include "../../../CommonInput/MetaPass.hlsl"
			ENDHLSL
		}

		Pass{
			Tags {
				"LightMode" = "WriteDeep"
			}
			HLSLPROGRAM
			#pragma target 3.5
			#pragma vertex LitPassVertex
			#pragma fragment LitPassFragment

			struct Attributes {
				float3 positionOS : POSITION;
			};

			struct Varyings {
				float4 positionCS_SS : SV_POSITION;
			};


			Varyings LitPassVertex (Attributes input) {
				Varyings output;
				output.positionCS_SS = TransformObjectToHClip(input.positionOS);
				return output;
			}

			void LitPassFragment (Varyings input) {}

			ENDHLSL
		}
    }
    CustomEditor "LWGUI.LWGUI"
}
