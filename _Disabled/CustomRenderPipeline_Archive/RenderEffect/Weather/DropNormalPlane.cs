using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{

    class DropProperty
    {
        public static int
            TimeSliceBuffer = Shader.PropertyToID("_TimeSliceBuffer"),
            MatrixBuffer = Shader.PropertyToID("_MatrixBuffer"),
            DeltaFlashSpeed = Shader.PropertyToID("_DeltaFlashSpeed"),
            DropNormalScale = Shader.PropertyToID("_DropNormalScale"),
            DropNormalTex = Shader.PropertyToID("_DropNormalTex"); 


    }

    /// <summary> /// 水滴法线平面 /// </summary>
    public class DropNormalPlane : MonoBehaviour, ISetup
    {
        [SerializeField]
        private RenderTexture targetTex;
        private RenderTexture tempTex;
        public ComputeShader shader;
        public Material material;
        public float timeSpeed = 1.25f;
        private Matrix4x4[] matrixs;
        //private Vector2[] times;
        private ComputeBuffer matrixBuffers;
        private ComputeBuffer timeSliceBuffers;
        private Material gusMat;
        private DrawNode drawNode;
        private Mesh fullScreenMesh;

        public MeshRenderer[] setMats;
        [Range(5f, 20.0f)]
        public float normalScale = 10;

        [Range(3, 15)]
        public int groupCount = 10;
        const int GroupSize = 64;
        const float SCALE = 0.02f;

        private void Start()
        {
            fullScreenMesh = MeshUtility.CreateFullScreenMesh();
            InitializedTarget();

            int maxCount = groupCount * GroupSize;
            Vector2[] times = new Vector2[maxCount];
            matrixs = new Matrix4x4[maxCount];
            for(int i = 0; i < maxCount; i++)
            {
                times[i] = new Vector2(Random.Range(0, 1f), Random.Range(0.8f, 1.2f));
                matrixs[i] = Matrix4x4.identity;
                matrixs[i].m00 = SCALE;
                matrixs[i].m11 = SCALE;
                matrixs[i].m22 = SCALE;

                matrixs[i].m03 = Random.Range(-1f, 1f);
                matrixs[i].m13 = Random.Range(-1f, 1f);
            }
            matrixBuffers = new ComputeBuffer(maxCount, 64);
            matrixBuffers.SetData(matrixs);
            timeSliceBuffers = new ComputeBuffer(maxCount, 8);
            timeSliceBuffers.SetData(times);
            shader.SetBuffer(0, DropProperty.MatrixBuffer, matrixBuffers);
            shader.SetBuffer(0, DropProperty.TimeSliceBuffer, timeSliceBuffers);

            //gusMat = new Material(Shader.Find("Hidden/GausBlur"));
            material.SetBuffer(DropProperty.TimeSliceBuffer, timeSliceBuffers);

            drawNode = new DrawNode();
            drawNode.setupEvent = Setup;
            drawNode.Submit();
        }

#if UNITY_EDITOR

        private void OnValidate()
        {
            if (Application.isPlaying)
            {
                SetMat();
            }
        }
#endif

        private void SetMat()
        {
            if(setMats != null)
            {
                for(int i = 0; i < setMats.Length; i++)
                {
                    if (setMats[i] != null &&
                        setMats[i].material != null)
                    {
                        setMats[i].material.EnableKeyword("_DROP_NORMAL");
                        setMats[i].material.SetTexture(DropProperty.DropNormalTex,
                            targetTex);
                        setMats[i].material.SetFloat(DropProperty.DropNormalScale,
                            normalScale);
                    }
                }
            }
        }

        private void OnDestroy()
        {
            tempTex.Release();
            targetTex.Release();
            drawNode.Remove();
            matrixBuffers.Dispose();
            timeSliceBuffers.Dispose();
        }

        /// <summary>
        /// 初始化目标的法线贴图
        /// </summary>
        private void InitializedTarget()
        {
            targetTex = RenderTexture.GetTemporary(512, 512, 0, RenderTextureFormat.ARGB32);
            targetTex.name = "DropTarget";
            tempTex = RenderTexture.GetTemporary(512, 512, 0, RenderTextureFormat.ARGB32);
            tempTex.name = "DropTempTarget";

            SetMat();
        }

        public void Setup(ScriptableRenderContext context, CommandBuffer buffer, Camera camera,
            int width, int height, bool useHDR)
        {
            buffer.SetRenderTarget(targetTex);
            buffer.ClearRenderTarget(false, true, new Color(0.5f, 0.5f, 1, 1));

            buffer.DrawMeshInstanced(fullScreenMesh, 0,
                material, 0, matrixs);

            RenderUtility.Instance.Draw(targetTex, tempTex, UtilityPass.BlurHorizontal, buffer);
            RenderUtility.Instance.Draw(tempTex, targetTex, UtilityPass.BlurVertical, buffer);
        }

        private void Update()
        {
            shader.SetFloat(DropProperty.DeltaFlashSpeed, Time.deltaTime * timeSpeed);
            shader.Dispatch(0, matrixBuffers.count / GroupSize, 1, 1);
            matrixBuffers.GetData(matrixs);
        }
    }
}