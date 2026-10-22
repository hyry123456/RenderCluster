using System.Collections.Generic;
using UnityEngine;

namespace CustomRenderPipline
{
    public class MeshUtility
    {
        private static void SetPointData<T>(List<T> point, T data, int vertexCount, int i, int j)
        {
            int index = i * vertexCount + j;
            point[index] = data;
        }

        private static T GetPointData<T>(List<T> point, int vertexCount, int i, int j)
        {
            int index = i * vertexCount + j;
            return point[index];
        }

        /// <summary>
        /// Create a plane Mesh, that have Edge and fix point flag
        /// 创建一个平面，其具有边界以及结点旗帜
        /// </summary>
        public static Mesh CreatePlaneMesh(Vector3 size, int vertexCount)
        {
            var mesh = new Mesh();
            vertexCount++;      //must add 1, that can insure every mid point can combine
            Vector3 gridsize = size / (vertexCount - 1);
            List<Vector3> vertices = new List<Vector3>(vertexCount * vertexCount);
            List<Color32> colors = new List<Color32>(vertexCount * vertexCount);
            List<Vector2> uvs = new List<Vector2>(vertexCount * vertexCount);
            List<int> indices = new List<int>();

            for (int i = 0; i < vertexCount; i++)
            {
                for (int j = 0; j < vertexCount; j++)
                {
                    Vector3 pos = new Vector3(i * gridsize.x, 0, j * gridsize.z);
                    Vector2 posUV = new Vector2(i * gridsize.x, j * gridsize.z);
                    posUV.x /= size.x; posUV.y /= size.z;       //缩放为0-1
                    Color32 color32 = new Color32(0, 0, 0, 0);

                    vertices.Add(pos);
                    uvs.Add(posUV);
                    colors.Add(color32);
                }
            }
            Color32 col32; byte x, y;
            //处理4个边界，不包含顶点
            for (int i = 1; i < vertexCount - 1; i++)
            {
                //下边
                col32 = GetPointData(colors, vertexCount, i, 0);
                x = 1; col32.r = x; SetPointData(colors, col32, vertexCount, i, 0);

                //右边
                col32 = GetPointData(colors, vertexCount, vertexCount - 1, i);
                x = 2; col32.r = x; SetPointData(colors, col32, vertexCount, vertexCount - 1, i);

                //上边
                col32 = GetPointData(colors, vertexCount, i, vertexCount - 1);
                x = 3; col32.r = x; SetPointData(colors, col32, vertexCount, i, vertexCount - 1);

                //左边
                col32 = GetPointData(colors, vertexCount, 0, i);
                x = 4; col32.r = x; SetPointData(colors, col32, vertexCount, 0, i);

                //////////////////////////拉伸边界次一行//////////////////////
                ////检测的方式时=是一个方向一个方向检测
                ////没改完
                ////下边
                //col32 = GetPointData(colors, vertexCount, i, 1);
                //y = (byte)(((1 << 4) ^ col32.g) ^ (1 << 5));
                //col32.g = y; SetPointData(colors, col32, vertexCount, i, 1);

                ////右边
                //col32 = GetPointData(colors, vertexCount, vertexCount - 2, i);
                //y = (byte)(((1 << 3) ^ col32.g) ^ (1 << 5)); col32.g = y;
                //SetPointData(colors, col32, vertexCount, vertexCount - 2, i);

                ////上边
                //col32 = GetPointData(colors, vertexCount, i, vertexCount - 2);
                //y = (byte)(((1 << 2) ^ col32.g) ^ (1 << 5)); col32.g = y;
                //SetPointData(colors, col32, vertexCount, i, vertexCount - 2);

                ////左边
                //col32 = GetPointData(colors, vertexCount, 1, i);
                //y = (byte)(((1 << 1) ^ col32.g) ^ (1 << 5)); col32.g = y; 
                //SetPointData(colors, col32, vertexCount, 1, i);
            }

            //处理4个边界，标记为需要边界检测，用二进制保证4个顶点都能检测到，且不会相互影响
            //边界4个边也是按照下列逻辑赋值，通过&可以在方向对应时都能正确匹配到
            //binary offset can check by '&' to know this point is edge, and both direction can check
            for (int i = 0; i < vertexCount; i++)
            {
                //下边
                col32 = GetPointData(colors, vertexCount, i, 0);
                y = (byte)((1 << 4) ^ col32.g); col32.g = y; SetPointData(colors, col32, vertexCount, i, 0);

                //右边
                col32 = GetPointData(colors, vertexCount, vertexCount - 1, i);
                y = (byte)((1 << 3) ^ col32.g); col32.g = y; SetPointData(colors, col32, vertexCount, vertexCount - 1, i);

                //上边
                col32 = GetPointData(colors, vertexCount, i, vertexCount - 1);
                y = (byte)((1 << 2) ^ col32.g); col32.g = y; SetPointData(colors, col32, vertexCount, i, vertexCount - 1);

                //左边
                col32 = GetPointData(colors, vertexCount, 0, i);
                y = (byte)((1 << 1) ^ col32.g); col32.g = y; SetPointData(colors, col32, vertexCount, 0, i);
            }

            //进行顶点连接
            for (int i = 0; i < vertexCount - 1; i++)
            {
                for (int j = 0; j < vertexCount - 1; j++)
                {
                    int leftDown = i * vertexCount + j;
                    int rightDown = i * vertexCount + j + 1;
                    int leftUp = (i + 1) * vertexCount + j;
                    int rightUp = (i + 1) * vertexCount + j + 1;

                    indices.Add(rightUp);
                    indices.Add(leftUp);
                    indices.Add(leftDown);

                    indices.Add(rightDown);
                    indices.Add(rightUp);
                    indices.Add(leftDown);
                }
            }

            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetIndices(indices, MeshTopology.Triangles, 0);
            mesh.SetColors(colors);
            return mesh;
        }

        public static Mesh CreateCube(float size)
        {
            var mesh = new Mesh();
            List<Vector3> vertices = new List<Vector3>();

            vertices.Add(new Vector3(0, 0, 0));
            vertices.Add(new Vector3(size, 0, 0));
            vertices.Add(new Vector3(size, size, 0));
            vertices.Add(new Vector3(0, size, 0));

            vertices.Add(new Vector3(0, size, size));
            vertices.Add(new Vector3(size, size, size));
            vertices.Add(new Vector3(size, 0, size));
            vertices.Add(new Vector3(0, 0, size));


            int[] triangles = {
                0, 2, 1, //face front
                0, 3, 2,
                2, 3, 4, //face top
                2, 4, 5,
                1, 2, 5, //face right
                1, 5, 6,
                0, 7, 4, //face left
                0, 4, 3,
                5, 4, 7, //face back
                5, 7, 6,
                0, 6, 7, //face bottom
                0, 1, 6
            };

            mesh.SetVertices(vertices);
            mesh.triangles = triangles;
            mesh.UploadMeshData(false);
            return mesh;
        }


        public static Mesh CreateCube(Vector3 size)
        {
            var mesh = new Mesh();
            List<Vector3> vertices = new List<Vector3>();

            vertices.Add(Vector3.zero);
            vertices.Add(new Vector3(size.x, 0, 0));
            vertices.Add(new Vector3(size.x, size.y, 0));
            vertices.Add(new Vector3(0, size.y, 0));

            vertices.Add(new Vector3(0, size.y, size.z));
            vertices.Add(new Vector3(size.x, size.y, size.z));
            vertices.Add(new Vector3(size.x, 0, size.z));
            vertices.Add(new Vector3(0, 0, size.z));


            int[] triangles = {
                0, 2, 1, //face front
                0, 3, 2,
                2, 3, 4, //face top
                2, 4, 5,
                1, 2, 5, //face right
                1, 5, 6,
                0, 7, 4, //face left
                0, 4, 3,
                5, 4, 7, //face back
                5, 7, 6,
                0, 6, 7, //face bottom
                0, 1, 6
            };

            mesh.SetVertices(vertices);
            mesh.triangles = triangles;
            mesh.UploadMeshData(false);
            return mesh;
        }

        /// <summary>  /// 创建一个0点在Cube中心的矩形    /// </summary>
        /// <param name="size">矩形边长</param>
        public static Mesh CreateCenterCube(float size)
        {
            float halfSize = size / 2;
            var mesh = new Mesh();
            List<Vector3> vertices = new List<Vector3>();

            vertices.Add(new Vector3(-halfSize, -halfSize, -halfSize));
            vertices.Add(new Vector3(halfSize, -halfSize, -halfSize));

            vertices.Add(new Vector3(halfSize, halfSize, -halfSize));

            vertices.Add(new Vector3(-halfSize, halfSize, -halfSize));


            vertices.Add(new Vector3(-halfSize, halfSize, halfSize));

            vertices.Add(new Vector3(halfSize, halfSize, halfSize));

            vertices.Add(new Vector3(halfSize, -halfSize, halfSize));

            vertices.Add(new Vector3(-halfSize, -halfSize, halfSize));

            int[] triangles = {
                0, 2, 1, //face front
                0, 3, 2,
                2, 3, 4, //face top
                2, 4, 5,
                1, 2, 5, //face right
                1, 5, 6,
                0, 7, 4, //face left
                0, 4, 3,
                5, 4, 7, //face back
                5, 7, 6,
                0, 6, 7, //face bottom
                0, 1, 6
            };


            mesh.SetVertices(vertices);
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.UploadMeshData(false);
            return mesh;
        }


        /// <summary>  /// 一个正方形的模型，适合做屏幕处理    /// </summary>
        public static Mesh CreateFullScreenMesh()
        {
            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[]{
                new Vector3(-1, -1, 0),
                new Vector3(-1, 1, 0),
                new Vector3(1, 1, 0),
                new Vector3(1, -1, 0)
            };
            mesh.uv = new Vector2[]
            {
                new Vector2(0, 1),
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(1, 1)
            };

            mesh.SetIndices(new int[] { 0, 1, 2, 3 }, MeshTopology.Quads, 0);
            return mesh;

        }

        /// <summary>
        /// 创建一个适合进行大小缩放用的Mesh，用于控制仅渲染目标贴图的一个部分时使用的Mesh
        /// </summary>
        public static Mesh CreateScaleMesh()
        {
            Mesh mQuad = new Mesh();
            List<Vector3> quadVertexList = new List<Vector3>();
            List<int> quadTriangleList = new List<int>();
            List<Vector2> quadUVList = new List<Vector2>();

            quadVertexList.Add(new Vector3(0, 1, 0.1f));
            quadUVList.Add(new Vector2(0, 1));
            quadVertexList.Add(new Vector3(0, 0, 0.1f));
            quadUVList.Add(new Vector2(0, 0));
            quadVertexList.Add(new Vector3(1, 0, 0.1f));
            quadUVList.Add(new Vector2(1, 0));
            quadVertexList.Add(new Vector3(1, 1, 0.1f));
            quadUVList.Add(new Vector2(1, 1));

            quadTriangleList.Add(0);
            quadTriangleList.Add(1);
            quadTriangleList.Add(2);

            quadTriangleList.Add(2);
            quadTriangleList.Add(3);
            quadTriangleList.Add(0);

            mQuad = new Mesh();
            mQuad.SetVertices(quadVertexList);
            mQuad.SetUVs(0, quadUVList);
            mQuad.SetTriangles(quadTriangleList, 0);
            return mQuad;
        }

        private static Mesh scaleMesh;

        /// <summary>        /// 一个共用的ScaleMesh        /// </summary>
        public static Mesh ScaleMesh
        {
            get
            {
                if (scaleMesh == null)
                    scaleMesh = CreateScaleMesh();
                return scaleMesh;
            }
        }
    }
}