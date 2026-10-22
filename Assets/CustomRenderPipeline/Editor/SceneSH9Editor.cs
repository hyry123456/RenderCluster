using UnityEngine;
using UnityEditor;
namespace CustomRenderPipline.MyRenderPipeline.Editor{

    [CustomEditor(typeof(SceneSH9))]
    public class SceneSH9Editor : UnityEditor.Editor
    {

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            if(GUILayout.Button("Update")){
                var example = target as SceneSH9;
                example.Bake();
                SHEditorUtil.UpdateGPUAsyncRequest(example.gpuRequest);
            }
        }
    }
}
