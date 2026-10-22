using UnityEditor;
using UnityEngine.Rendering;

namespace CustomRenderPipline.MyRenderPipeline.Editor
{
    public static class SHEditorUtil{
        public static void UpdateGPUAsyncRequest(AsyncGPUReadbackRequest req){
            EditorApplication.CallbackFunction callUpdate = null;
            callUpdate = ()=>{
                if(req.done){
                    return;
                }
                req.Update();
                EditorApplication.delayCall += callUpdate;
            };
            callUpdate();
        }

    }
}
