Shader "Unlit/ClusterPBR"
{
    Properties
    {
        // _MainTex_ST("MainTex Scale Offset", vector) = (1, 1, 0, 0)
		_MainTex("Texture", 2D) = "white" {}
        _CullOff("Cull Off", Range(0, 1)) = 0.3
        _MapId("Map Id", int) = 0
		_NormalScale("Normal Scale", Range(0, 1)) = 0.5
		_Metallic("Metallic", Range(0, 1)) = 0.5
		_Roughness("Roughness", Range(0.05, 1)) = 0.8


		_DetailMap("Details", 2D) = "linearGrey" {}
        _DetailNormalMap("Detail Normals", 2D) = "bump" {}
        _Color("Main Color", Color) = (1, 1, 1, 1)
        _DetailAlbedo("Detail Albedo", Range(0, 1)) = 0
        _DetailNormal("Detail Normal", Range(0, 1)) = 0
        _DetailRoughness("Detail Roughness", Range(0, 1)) = 0

        // 1 = 图集 mask / emission 有效；0 = 跳过对应取样（mask 视为白、emission 视为黑）。
        // 与 DefferStandard 的 GetMask 回退、以及 BRG 路径的 _UseMask/_UseEmission 同口径。
        [HideInInspector] _UseMask("Use Mask Map", Float) = 1
        [HideInInspector] _UseEmission("Use Emission", Float) = 1

        // RVT 图集（运行时由 ClusterCore 绑定）。必须声明为【材质属性】而不是只用全局纹理：
        // Unity 只有在采样器对应材质属性纹理时，才会沿用该纹理自己的 anisoLevel。
        // 全局 sampler_linear_repeat 恒为 aniso=1，而场景贴图多为 aniso=9，
        // 会导致掠射角表面（圆柱、地面）比标准路径明显糊。
        [HideInInspector] _VirtualAlbedo("RVT Albedo", 2D) = "white" {}
        [HideInInspector] _VirtualNormal("RVT Normal", 2D) = "white" {}
        [HideInInspector] _VirtualMask("RVT Mask", 2D) = "white" {}
        [HideInInspector] _VirtualEmission("RVT Emission", 2D) = "white" {}
        [HideInInspector] _VirtualDetail("RVT Detail", 2D) = "white" {}
    }
    SubShader
    {

		HLSLINCLUDE
			#include "../../ShaderLibrary/Common.hlsl"
		ENDHLSL

        Pass{
			Tags {
				"Queue" = "Geometry" 
				"LightMode" = "Feedback"
			}

            ZWrite ON
            Blend One Zero

            
            HLSLPROGRAM
            #include "Input/ClusterInput.hlsl"
            #include "Input/FeedbackPass.hlsl"
			#pragma multi_compile _ _FEED_BACK_DEBUG
#pragma target 4.5
#pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma vertex VertexSimple

            #pragma fragment fragSimple


            

            ENDHLSL
        }

        Pass
		{
			Tags {
				"Queue" = "Geometry" 
				"LightMode" = "OutGBuffer"
			}

			HLSLPROGRAM
			#pragma vertex LitPassVertex
			#pragma fragment LitPassFragment

			#pragma target 4.5

			#pragma multi_compile _ LIGHTMAP_ON
#pragma multi_compile _ DOTS_INSTANCING_ON
			// Alpha Cutout 门控：ClusterPass.hlsl 里 clip() 只在 _CLIPPING 下执行。
			// 用 multi_compile 而不是 shader_feature：AssetMat 是运行时 new 出来的，
			// 构建时没有任何材质资产引用本 shader 的这一变体，shader_feature 会被整条剥离。
			#pragma multi_compile _ _CLIPPING

			#pragma multi_compile_instancing

            #include "../../ShaderLibrary/GI.hlsl"
            #include "../../ShaderLibrary/Surface.hlsl"
            #include "../../ShaderLibrary/Shadows.hlsl"
            #include "../../ShaderLibrary/Light.hlsl"
            #include "../../ShaderLibrary/BRDF.hlsl"
            #include "../../ShaderLibrary/Lighting.hlsl"
            #include "../DeffererLibrary/DeffererInput.hlsl"
			
            #include "Input/ClusterInput.hlsl"
			#include "Input/ClusterPass.hlsl"


			ENDHLSL
		}

        Pass
		{
			Tags {
				"LightMode" = "ShadowCaster"
			}

			ColorMask 0

			HLSLPROGRAM
			#pragma vertex LitPassVertex
			#pragma fragment LitPassFragment

			#pragma target 4.5

			#pragma multi_compile_instancing
#pragma multi_compile _ DOTS_INSTANCING_ON

			//BRG 关键修复 3：本 pass 此前只补了实例 ID 链（保住 InstanceVisibility cbuffer），
			//但从未声明 UnityPerMaterial + MaterialPropertyMetadata（SRP Batcher 兼容所需的
			//材质 cbuffer 脚手架）。真实场景开阴影后，BRG 批次被 ShadowCaster 的
			//DrawRenderers 带上，Submit 校验该 DOTS 变体失败 → "wrong cbuffer setup"。
			//Test 场景探针因无灯光无阴影，ShadowCaster 变体从未被校验，故二分时未暴露。
			#include "Input/ClusterDOTSMaterial.hlsl"

            //与 DefferStandard 的 ShadowCaster（ShadowInput.hlsl）保持一致。
            //Shadows.cs 在渲染平行光阴影图前会把 _ShadowPancaking 设为 1：
            //把跨过阴影相机近平面的几何体压到近平面，否则会被整体裁剪掉，
            //该物体在本级联阴影图里直接缺失 —— 而主画面完全正常，
            //表现就是“看得见的房子没有投下应有的阴影，只有部分物体有阴影”。
            bool _ShadowPancaking;

			//BRG 关键修复 1：ShadowCaster / WriteDeep 的 DOTS 变体此前没有初始化实例 ID，
			//也没人引用 unity_DOTSVisibleInstances —— 编译器会把 UnityInstancingDOTS_InstanceVisibility
			//cbuffer 整个优化掉，BRG 校验该变体时报 "wrong cbuffer setup / Missing DOTS_INSTANCING_ON variant"
			//（Unity 官方确认的两大成因之一："Not initializing instance ID correctly"）。
			//凡是声明了 DOTS_INSTANCING_ON 变体的 pass，都必须走完整的实例 ID 初始化链。
			struct Attributes {
				float3 positionOS : POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings {
				float4 positionCS_SS : SV_POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			Varyings LitPassVertex (Attributes input) {
				Varyings output;

				UNITY_SETUP_INSTANCE_ID(input);
				UNITY_TRANSFER_INSTANCE_ID(input, output);

				output.positionCS_SS = TransformObjectToHClip(input.positionOS);
				if (_ShadowPancaking) {
					#if UNITY_REVERSED_Z
						output.positionCS_SS.z = min(
							output.positionCS_SS.z, UNITY_NEAR_CLIP_VALUE
						);
					#else
						output.positionCS_SS.z = max(
							output.positionCS_SS.z, UNITY_NEAR_CLIP_VALUE
						);
					#endif
				}
				return output;
			}
			void LitPassFragment (Varyings input) {
				UNITY_SETUP_INSTANCE_ID(input);
				//保活引用：_CullOff ∈ [0,1]，+1 后恒 > 0，永不裁剪，行为零变化。
				//作用是把 UnityPerMaterial / metadata cbuffer 留在编译产物里，
				//防止被编译器优化掉后 BRG 校验再次失败（同 InstanceVisibility 的教训）。
				clip(INPUT_PROP(float, _CullOff) + 1.0);
			}


			ENDHLSL
		}

		Pass{
			Tags {
				"LightMode" = "WriteDeep"
			}
			HLSLPROGRAM
			#pragma target 4.5
#pragma multi_compile _ DOTS_INSTANCING_ON
			#pragma vertex LitPassVertex
			#pragma fragment LitPassFragment

			//同 ShadowCaster：补齐 UnityPerMaterial + metadata 脚手架（BRG 关键修复 3）。
			#include "Input/ClusterDOTSMaterial.hlsl"

			struct Attributes {
				float3 positionOS : POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings {
				float4 positionCS_SS : SV_POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};


			Varyings LitPassVertex (Attributes input) {
				Varyings output;

				UNITY_SETUP_INSTANCE_ID(input);
				UNITY_TRANSFER_INSTANCE_ID(input, output);

				output.positionCS_SS = TransformObjectToHClip(input.positionOS);
				return output;
			}

			void LitPassFragment (Varyings input) {
				UNITY_SETUP_INSTANCE_ID(input);
				//保活引用（同 ShadowCaster）：把 UnityPerMaterial / metadata cbuffer 留在编译产物里。
				clip(INPUT_PROP(float, _CullOff) + 1.0);
			}

			ENDHLSL
		}

		//SRPDefaultUnlit —— 此前怀疑的“我加的 pass”。经验证：本工程没有任何 DrawRenderers
		//使用 "SRPDefaultUnlit" 这个 LightMode 标签（grep 确认），而 BRG 的 draw command 没有
		//pass 字段、是按 DrawRenderers 的 ShaderTagId 选 pass 的；DefferPipeline 用 "OutGBuffer"。
		//因此此 pass 是死代码，BRG 永远不会画它；仅作占位保留，不影响正确性。
		Pass
		{
			Tags {
				"Queue" = "Geometry"
				"LightMode" = "SRPDefaultUnlit"
			}

			HLSLPROGRAM
			#pragma vertex LitPassVertex
			#pragma fragment LitPassFragment

			#pragma target 4.5

			#pragma multi_compile _ LIGHTMAP_ON
			#pragma multi_compile _ DOTS_INSTANCING_ON

			#pragma multi_compile_instancing

			#include "../../ShaderLibrary/GI.hlsl"
			#include "../../ShaderLibrary/Surface.hlsl"
			#include "../../ShaderLibrary/Shadows.hlsl"
			#include "../../ShaderLibrary/Light.hlsl"
			#include "../../ShaderLibrary/BRDF.hlsl"
			#include "../../ShaderLibrary/Lighting.hlsl"
			#include "../DeffererLibrary/DeffererInput.hlsl"

			#include "Input/ClusterInput.hlsl"
			#include "Input/ClusterPass.hlsl"

			ENDHLSL
		}

    }
}
