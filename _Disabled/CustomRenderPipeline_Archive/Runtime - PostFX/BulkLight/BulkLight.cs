using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    public struct BulkLightStruct
    {
        public Vector3 center;
        public Vector3 extend;
    };

    public interface IBulkLight
    {
        void DrawBulkLightNode(CommandBuffer buffer);
    }

    /// <summary> 
    /// 绘制非视角空间体积光用到的类，将所有的Cluster数据传入，进行体积光绘制
    /// </summary>
    public class BulkLight : MonoBehaviour
    {
        private static BulkLight instance;
        public static BulkLight Instance
        {
            get 
            {
                return instance;
            }
        }

        public static BulkLight CreateInstance()
        {
            if (instance == null)
            {
                GameObject game = new GameObject("BulkLight");
                game.AddComponent<BulkLight>();
                game.hideFlags = HideFlags.HideInHierarchy;
            }
            return instance;
        }

        public static bool UseBulkLight
        {
            get
            {
                if (instance == null)
                    return false;
                return true;
            }
        }

        [SerializeField]
        List<BulkLightStruct> drawing 
            = new List<BulkLightStruct>();

        [SerializeField]
        List<BulkLightStruct> waiting
            = new List<BulkLightStruct>();

        /// <summary>   /// 渲染节点，这个一般是烘焙体积光     /// </summary>
        List<IBulkLight> bulkLightNodes = new List<IBulkLight>();

        ComputeBuffer boxsBuffer;
        Material material;

        private static Mesh cube;
        public static Mesh BulkLightCube
        {
            get
            {
                if(cube == null)
                    cube = MeshUtility.CreateCenterCube(1);
                return cube;
            }
        }

        private void Awake()
        {
            if(instance != null)
            {
                DestroyImmediate(gameObject);
                return;
            }

            instance = this;
            material = new Material(Shader.Find("CustomRP/PostEffect/BulkLight"));
        }

        private void OnDestroy()
        {
            boxsBuffer?.Release();
            boxsBuffer = null;
            waiting.Clear();
            drawing.Clear();
            //是该物体被删除才进行删除
            if (instance.gameObject == gameObject)
            {
                instance = null;
            }

        }

        /// <summary>   /// 插入体积光节点    /// </summary>
        public void AddBulkLightBox(BulkLightStruct bulkLight)
        {
            if(waiting.Count == 0)
            {
                waiting.Add(bulkLight);
                Common.SustainCoroutine.Instance.AddCoroutine(RecaculateBulkLight);
            }
            else
            {
                waiting.Add(bulkLight);
            }

        }

        /// <summary>   
        /// 计算当前需要运行的体积光节点，实时体积光
        /// </summary>
        private bool RecaculateBulkLight()
        {
            if(boxsBuffer == null)
            {
                if (drawing.Count + waiting.Count == 0) return true;
                boxsBuffer = new ComputeBuffer(drawing.Count + waiting.Count,
                    Marshal.SizeOf(new BulkLightStruct()));
                return false;
            }
            if (boxsBuffer == null || drawing == null || waiting == null)
                Debug.Log("Null");

            if (boxsBuffer.count != drawing.Count + waiting.Count)
            {
                //数量不等就释放全部节点
                boxsBuffer.Release();
                return false;
            }

            //添加等待的节点
            drawing.AddRange(waiting);
            waiting.Clear();
            //插入所有节点的数据到GPU缓冲区
            boxsBuffer.SetData(drawing.ToArray(), 0, 0, boxsBuffer.count);
            return true;
        }

        /// <summary>  /// 将单独的渲染节点加入到体积光渲染队列中    /// </summary>
        /// <param name="bulkLight">该渲染节点需要执行的方法</param>
        public void AddBulkLightDrawNode(IBulkLight bulkLight)
        {
            if (bulkLightNodes.Contains(bulkLight))
                return;
            bulkLightNodes.Add(bulkLight);
        }

        public void SetUp(Shader bulkShader)
        {
            if(material == null || material.shader != bulkShader)
                material = new Material(bulkShader);
        }

        /// <summary>  /// 移除渲染节点    /// </summary>
        /// <param name="bulkLight">渲染节点</param>
        public void RemoveBulkLightDrawNode(IBulkLight bulkLight)
        {
            bulkLightNodes.Remove(bulkLight);
        }

        /// <summary>   /// 渲染体积光的方法，动态以及静态都在这里渲染  /// </summary>
        /// <param name="buffer"></param>
        public void DrawBulkLight( CommandBuffer buffer, int pass)
        {
            if (boxsBuffer != null)
            {
                buffer.SetGlobalBuffer("_BoxsBuffer", boxsBuffer);
                buffer.DrawMeshInstancedProcedural(BulkLightCube, 0, material, pass, boxsBuffer.count);
            }

            for(int i = 0; i < bulkLightNodes.Count; i++)
            {
                bulkLightNodes[i].DrawBulkLightNode(buffer);
            }
        }


    }
}