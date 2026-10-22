using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    public enum CustomRP_DrawMode
    {
        None = 0,
        Transparent = 1,
        Shadow = 2,
        GBuffer = 4,
        Opaque = 8,
        Cleanup = 16,
    }

    public delegate void DrawShadowEvent(ScriptableRenderContext context, CommandBuffer buffer);
    ///// <summary>  /// ӰȾӿ  /// </summary>
    public interface IDrawShadow
    {
        void DrawShadow(ScriptableRenderContext context, CommandBuffer buffer);
    }

    public delegate void DrawTransparentEvent(ScriptableRenderContext context,
        CommandBuffer buffer, Camera camera);
    /// <summary>  /// Ⱦ͸Ľӿ /// </summary>
    public interface IDrawTransparent
    {
        void DrawTransparent(ScriptableRenderContext context, CommandBuffer buffer,
            Camera camera);
    }

    public delegate void DrawGBufferEvent(ScriptableRenderContext context,
        CommandBuffer buffer, Camera camera);
    /// <summary>  /// GBufferӿ  /// </summary>
    public interface IDrawGBuffer
    {
        void DrawGBuffer(ScriptableRenderContext context, CommandBuffer buffer, 
            Camera camera);
    }

    public delegate void DrawSSRBefore(ScriptableRenderContext context,
    CommandBuffer buffer, Camera camera);
    /// <summary>  /// GBuffer AddӿڣȾSSRչ  /// </summary>
    public interface IDrawGBufferAdd
    {
        void DrawGBufferAdd(ScriptableRenderContext context, CommandBuffer buffer,
            Camera camera);
    }

    public delegate void DrawSkyboxLater(ScriptableRenderContext context,
        CommandBuffer buffer, Camera camera);

    /// <summary> /// Ⱦ壬д֮󣬼Skybox֮󣬵͸֮ǰ /// </summary>
    public interface IDrawSkyboxLater
    {
        void DrawSkyboxLater(ScriptableRenderContext context, CommandBuffer buffer,
            Camera camera);
    }

    public delegate void DrawOpaqueEvent(ScriptableRenderContext context, 
        CommandBuffer buffer, Camera camera);
    /// <summary> /// ͸Ⱦ /// </summary>
    public interface IDrawOpaque
    {
        void DrawOpaque(ScriptableRenderContext context, CommandBuffer buffer,
            Camera camera);
    }

    public delegate void DrawFeedbackEvent(ScriptableRenderContext context,
    CommandBuffer buffer, Camera camera);
    /// <summary> /// Visual TextureʹʱFeedback
    /// ҪͼʱSetupִ /// </summary>
    public interface IDrawFeedback
    {
        void DrawFeedback(ScriptableRenderContext context, CommandBuffer buffer,
            Camera camera);
    }

    public delegate void SetupEvent(ScriptableRenderContext context,
        CommandBuffer buffer, Camera camera, int width, int height, bool useHDR);
    /// <summary> /// ʼӿ  /// </summary>
    public interface ISetup
    {
        void Setup(ScriptableRenderContext context, CommandBuffer buffer, Camera camera,
            int width, int height, bool useHDR);
    }

    public delegate void CleanUpEvent(ScriptableRenderContext context, 
        CommandBuffer buffer);
    /// <summary> /// ӿڣִ /// </summary>
    public interface ICleanUp
    {
        void CleanUp(ScriptableRenderContext context, CommandBuffer buffer);
    }

    /// <summary> 
    /// 뵽ȾϵͳĽڵ
    /// </summary>
    public class DrawNode : System.IComparable
    {
        public int queue;
        public DrawShadowEvent shadowEvent;
        public DrawTransparentEvent transparentEvent;
        public DrawGBufferEvent gBufferEvent;
        public DrawOpaqueEvent opaqueEvent;
        public SetupEvent setupEvent;
        public CleanUpEvent cleanUpEvent;
        public DrawFeedbackEvent feedbackEvent;

        public DrawSSRBefore ssrBefore;
        public DrawSkyboxLater skyboxLater;


        public DrawNode()
        {
            queue = 0;
            shadowEvent = null;
            transparentEvent = null;
            gBufferEvent = null;
            ssrBefore = null;
            setupEvent = null;
            skyboxLater = null;
        }

        public DrawNode(int queue)
        {
            this.queue = queue;
            shadowEvent = null;
            transparentEvent = null;
            gBufferEvent = null;
            ssrBefore = null;
            setupEvent = null;
        }

        public int CompareTo(object obj)
        {
            DrawNode node = (DrawNode)obj;
            return queue.CompareTo(node.queue);
        }

        /// <summary>   /// ύڵ㵽Ⱦջ  /// </summary>
        public void Submit()
        {
            GPUDravinSystem.Instance.InsertRender(this);
        }

        /// <summary>   /// ȾջƳڵ  /// </summary>
        public void Remove()
        {
            GPUDravinSystem.Instance.RemoveRender(this);
        }


    }

    public class GPUDravinSystem
    {
        private GPUDravinSystem() 
        {
            nodes = new Common.PreorderLinkList<DrawNode>(Common.SortType.Less);
        }

        /// <summary>
        /// ǰĲü棬ÿȾʱʼΣ׶޳
        /// </summary>
        private Plane[] currentCameraPlane = new Plane[6];
        public Plane[] CurrentCameraPlane => currentCameraPlane;

        public Vector4[] CameraPlane
        {
            get
            {
                Vector4[] result = new Vector4[6];
                for(int i = 0; i < 6; i++)
                {
                    result[i] = (Vector4)currentCameraPlane[i].normal;
                    result[i].w = currentCameraPlane[i].distance;
                }
                return result;
            }
        }



        //private Plane[] shadowPlane;
        //public Plane[] ShadowPlane
        //{
        //    get { return shadowPlane; }
        //}
        private Vector4[] shadowPlane;
        public Vector4[] ShadowPlane
        {
            get { return shadowPlane; }
        }

        int _targetRTW, _targetRTH;
        RenderSetting renderSetting;
        public int CurrentRTWidth => _targetRTW;
        public int CurrentRTHeight => _targetRTH;
        public RenderSetting RenderSetting => renderSetting;



        private static GPUDravinSystem instance;
        public static GPUDravinSystem Instance
        {
            get
            {
                if (instance == null) instance = new GPUDravinSystem();
                return instance;
            }
        }

        private static Cubemap _sceneSkybox;
        private static List<Vector4> _sh9 = new List<Vector4>();
        /// <summary> /// _Sh9 系数的数量，与 Shader 中 float4 _Sh9[9] 对应 /// </summary>
        private const int Sh9Count = 9;
        /// <summary> /// 场景未烘焙 SH9 时使用的全零系数，避免上传空数组导致 ArgumentException /// </summary>
        private static readonly Vector4[] _defaultSh9 = new Vector4[Sh9Count];
        public static void SetSceneLighting(List<Vector4> sh9, Cubemap cubemap)
        {
            _sh9 = sh9;
            _sceneSkybox = cubemap;
        }

        /// <summary>   /// 洢нڵ    /// </summary>
        private Common.PreorderLinkList<DrawNode> nodes;

        /// <summary>
        /// һҪȾĶ󣬲ͻ
        /// </summary>
        /// <param name="clustBase">Ķ</param>
        public void InsertRender(DrawNode node)
        {
            if (node == null) return;
            if (!nodes.Contains(node))
                nodes.Add(node);
        }

        /// <summary>        /// ƳȾջ        /// </summary>
        public void RemoveRender(DrawNode node)
        {
            if (node == null) return;
            nodes.Remove(node);
        }

        ///// <summary>        /// ӰͶӰü棬Ӱֻ5棬ȥ        /// </summary>
        ///// <param name="planes"></param>
        //public void SetShadowPlane(Vector4[] planes)
        //{
        //    shadowPlane = new Plane[5];
        //    for (int i = 0; i < 5; i++)
        //    {
        //        shadowPlane[i] = new Plane(new Vector3(planes[i].x, planes[i].y, planes[i].z), planes[i].w);
        //    }
        //}

        /// <summary>
        /// в뵽ջелƣöӦĻƷ
        /// </summary>
        public void DrawShadows(ScriptableRenderContext context, CommandBuffer buffer)
        {
            foreach (DrawNode node in nodes)
            {
                if(node.shadowEvent != null)
                {
                    node.shadowEvent(context, buffer);
                }
            }
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }

        public void SetDirectionCullingPlane(Vector4 cullingSphere,
            Vector3 forward, Vector3 up, Vector3 right)
        {
            shadowPlane = new Vector4[5];
            Vector3 center = cullingSphere;
            // 沿光线方向：把裁剪球在光线轴上的两个极值点各生成一个平面
            shadowPlane[0] = new Vector4(-forward.x, -forward.y, -forward.z,
                -Vector3.Dot(-forward, center + forward * cullingSphere.w));

            // 上
            shadowPlane[1] = new Vector4(-up.x, -up.y, -up.z,
                -Vector3.Dot(-up, center + up * cullingSphere.w));
            // 下（注意：原来这四个平面全写进了 shadowPlane[1]，2/3/4 永远停在 Vector4.zero，
            // 零平面的法线为 0，dot 恒为 0，测试永远判“不裁”——等于阴影裁剪体只剩 2 个面）
            shadowPlane[2] = new Vector4(up.x, up.y, up.z,
                -Vector3.Dot(up, center + -up * cullingSphere.w));
            // 右
            shadowPlane[3] = new Vector4(right.x, right.y, right.z,
                -Vector3.Dot(right, center + -right * cullingSphere.w));
            // 左
            shadowPlane[4] = new Vector4(-right.x, -right.y, -right.z,
                -Vector3.Dot(-right, center + right * cullingSphere.w));
        }

        public void DrawSkyboxLater(ScriptableRenderContext context, CommandBuffer buffer,
            Camera camera)
        {
            foreach (DrawNode node in nodes)
            {
                if (node.skyboxLater != null)
                {
                    node.skyboxLater(context, buffer, camera);
                }
            }
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }

        public void DrawTransparent(ScriptableRenderContext context, CommandBuffer buffer,
            Camera camera)
        {

            foreach (DrawNode node in nodes)
            {
                if (node.transparentEvent != null)
                {
                    node.transparentEvent(context, buffer, camera);
                }
            }
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }
        public void DrawFeedback(ScriptableRenderContext context, CommandBuffer buffer,
            Camera camera)
        {

            foreach (DrawNode node in nodes)
            {
                if (node.feedbackEvent != null)
                {
                    node.feedbackEvent(context, buffer, camera);
                }
            }
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }

        /// <summary>  /// Ⱦ͸ʱGBuffer   /// </summary>
        public void DrawGBuffer(ScriptableRenderContext context, CommandBuffer buffer,Camera camera)
        {
            foreach (DrawNode node in nodes)
            {
                if (node.gBufferEvent != null)
                {
                    node.gBufferEvent(context, buffer,
                       camera);
                }
            }
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }

        /// <summary>
        /// ȾGBufferSSRʱԼߵչд룬õʵʱ
        /// </summary>
        public void DrawBeforeSSR(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            buffer.BeginSample("GBuffer Add");
            foreach (DrawNode node in nodes)
            {
                if (node.ssrBefore != null)
                {
                    node.ssrBefore(context, buffer,
                       camera);
                }
            }
            buffer.EndSample("GBuffer Add");
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }

        /// <summary>
        /// Ⱦ͸壬ǲҪ뵽BRDFȾʱ뵽λ
        /// </summary>
        public void DrawOpaque(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            buffer.BeginSample("DrawOpaque");
            foreach (DrawNode node in nodes)
            {
                if (node.opaqueEvent != null)
                {
                    node.opaqueEvent(context, buffer,
                       camera);
                }
            }
            buffer.EndSample("DrawOpaque");
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }

        public void SetUp(ScriptableRenderContext context, CommandBuffer buffer, 
            Camera camera, int width, int height, RenderSetting renderSetting)
        {
            _targetRTW = width;
            _targetRTH = height;
            this.renderSetting = renderSetting;
            GeometryUtility.CalculateFrustumPlanes(camera, currentCameraPlane);
            buffer.SetGlobalTexture("_MySkybox", _sceneSkybox);
            //注意：SetGlobalVectorArray 不接受空数组，SceneSH9 未烘焙（列表为空或数量不对）时上传全零系数
            if (_sh9 != null && _sh9.Count == Sh9Count)
            {
                buffer.SetGlobalVectorArray("_Sh9", _sh9);
            }
            else
            {
                buffer.SetGlobalVectorArray("_Sh9", _defaultSh9);
            }

#if UNITY_EDITOR
            buffer.BeginSample("GPUDravin Setup");
#endif

            foreach (DrawNode node in nodes)
            {
                if (node.setupEvent != null)
                {
                    node.setupEvent(context, buffer,
                       camera, width, height, renderSetting.allowHDR);
                }
            }

#if UNITY_EDITOR
            buffer.EndSample("GPUDravin Setup");
#endif

            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
            //ÿSetupرգSetupʱ
            camera.transform.hasChanged = false;
        }

        public Vector4[] GetFrustumPlanes()
        {
            Vector4[] planes = new Vector4[currentCameraPlane.Length];
            for (int i = 0; i < currentCameraPlane.Length; i++)
            {
                Vector3 normal = currentCameraPlane[i].normal;
                planes[i] = new Vector4(normal.x, normal.y, normal.z, currentCameraPlane[i].distance);
            }
            return planes;
        }

        public void CleanUp(ScriptableRenderContext context, CommandBuffer buffer)
        {
            foreach (DrawNode node in nodes)
            {
                if (node.cleanUpEvent != null)
                {
                    node.cleanUpEvent(context, buffer);
                }
            }

            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }


        public bool FrustumCulling(Bounds bounds)
        {
            //bounds׶޳ŻģЧѾȽϸ
            return GeometryUtility.TestPlanesAABB(currentCameraPlane, bounds);
        }

        bool IsSphereOutSidePlane(Plane plane, Vector4 range)
        {
            if (Vector3.Dot(plane.normal, range) + plane.distance < -range.w)
                return true;
            return false;
        }


        public bool FrustumCulling(Vector4 range)
        {
            //bounds׶޳ŻģЧѾȽϸ
            //return GeometryUtility.TestPlanesAABB(currentCameraPlane, bounds);
            //return GeometryUtility.TestPlanesAABB
            for(int i = 0; i < currentCameraPlane.Length; i++)
            {
                if (IsSphereOutSidePlane(currentCameraPlane[i], range))
                    return false;
            }
            return true;
        }
    }
}