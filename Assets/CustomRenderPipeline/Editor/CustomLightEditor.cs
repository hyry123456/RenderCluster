using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{

	[CanEditMultipleObjects]
	[CustomEditorForRenderPipeline(typeof(Light), typeof(PipelineAsset))]
	public class CustomLightEditor : LightEditor
	{

		static GUIContent renderingLayerMaskLabel =
			new GUIContent("Rendering Layer Mask", "Truly effective property.");

		public override void OnInspectorGUI()
		{
			base.OnInspectorGUI();
			DrawRenderingLayerMask();
			if (
				!settings.lightType.hasMultipleDifferentValues &&
				(LightType)settings.lightType.enumValueIndex == LightType.Spot
			)
			{
				settings.DrawInnerAndOuterSpotAngle();
			}
			settings.ApplyModifiedProperties();


			var light = target as Light;
			if (light.cullingMask != -1)
			{
				EditorGUILayout.HelpBox(
					light.type == LightType.Directional ?
						"Culling Mask only affects shadows." :
						"Culling Mask only affects shadow unless Lights Per Objects is on.",
					MessageType.Warning
				);
			}
		}

        void DrawRenderingLayerMask()
        {
            SerializedProperty property = settings.renderingLayerMask;
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            int mask = property.intValue;
            if (mask == int.MaxValue)
            {
                mask = -1;
            }
            mask = EditorGUILayout.MaskField(
                renderingLayerMaskLabel, mask,
                GraphicsSettings.currentRenderPipeline.renderingLayerMaskNames
            );
            if (EditorGUI.EndChangeCheck())
            {
                property.intValue = mask == -1 ? int.MaxValue : mask;
            }
            EditorGUI.showMixedValue = false;
        }
    }
}