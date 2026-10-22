using Cluster;
using UnityEngine;

namespace CustomRenderPipline.MyRenderPipeline
{
    public enum RenderMode
    {
        CPUCompute,
        GPUCompue,
    }

    [ExecuteAlways]
    public class MyMeshRender : MonoBehaviour
    {
        public RenderMode renderMode;
        public Mesh renderMesh;
        public Material material;
        private void OnEnable()
        {
            var meshFilter = gameObject.GetComponent<MeshFilter>();
            if(meshFilter == null)
                return;

            //if (Application.isPlaying)
            //{
            //    var render = gameObject.GetComponent<MeshRenderer>();
            //    if (render != null) render.enabled = false;
            //}


            renderMesh = meshFilter.sharedMesh;

            switch (renderMode)
            {
                case RenderMode.CPUCompute:
                    MyRenderObject myRender = MyRenderObject.Instance;
                    //开始时才可以创建
                    if (myRender == null)
                        myRender = MyRenderObject.CreateInstance();
                    if(gameObject.isStatic)
                        myRender.InsertStaticCPURender(this, renderMesh.bounds);
                    //else
                    break;
            }
        }

        private void OnDisable()
        {
            switch (renderMode)
            {
                case RenderMode.CPUCompute:
                    MyRenderObject myRender = MyRenderObject.Instance;
                    if (myRender == null) return;
                    if(gameObject.isStatic)
                        myRender.RemoveStaticCPURender(this);
                    break;
            }
        }


#if UNITY_EDITOR

        public void SaveMeshDataSimple()
        {
            MeshFilter meshFilter = GetComponent<MeshFilter>();
            ClusterControl clusterControl = ClusterControl.Instance;
            clusterControl.SaveMesh(meshFilter.sharedMesh, true);
        }

        public void SaveMeshData()
        {
            MeshFilter meshFilter = GetComponent<MeshFilter>();
            ClusterControl clusterControl = ClusterControl.Instance;
            clusterControl.SaveMesh(meshFilter.sharedMesh, false);
        }
    }
#endif
}