using UnityEngine;

namespace CustomRenderPipline
{
    [CreateAssetMenu(menuName = "Rendering/GPUDravin/Bulk Light")]
    public class BulkLightAssets : ScriptableObject
    {
        public Material material;
        //public ComputeShader compute;

        public int longCount = 15;
        public int perCircleCount = 15;
    }
}