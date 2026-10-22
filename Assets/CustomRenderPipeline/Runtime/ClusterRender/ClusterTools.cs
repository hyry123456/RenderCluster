using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace CustomRenderPipline.Custer
{
    public static class ClusterTools
    {
        private const int clusterCount = 16;
        /// <summary>        /// һصı        /// </summary>
        public static int ClusterQuardCount => clusterCount;

        /// <summary>        /// һصıߵ        /// </summary>
        public static int ClusterLineCount => clusterCount * 6;

        private static List<Triangle> GetAllTriangles(int[] tris, 
            //int subIndex
            int subMeshStart, int subMeshCount
            )
        {
            //if (mesh == null)
            //{
            //    Debug.Log("mesh is null");
            //    return null;
            //}
            List<Triangle> triangles = new List<Triangle>();


            //int[] tris = mesh.triangles;
            //var subMesh = mesh.GetSubMesh(subIndex);

            //for (int i = subMesh.indexStart; i < subMesh.indexStart + subMesh.indexCount; i += 3)
            for (int i = subMeshStart; i < subMeshStart + subMeshCount; i += 3)
            {
                Triangle triangle = new Triangle();
                triangle.indexs = new int[3];
                triangle.isUse = false;
                triangle.indexs[0] = tris[i];
                triangle.indexs[1] = tris[i + 1];
                triangle.indexs[2] = tris[i + 2];

                triangles.Add(triangle);
            }

            return triangles;
        }

        /// <summary>
        /// ڵתΪı
        /// </summary>
        /// <param name="triangles">source triangles</param>
        private static List<Quardrangle> GetAllQuard(List<Triangle> triangles, Vector3[] points)
        {
            Triangle[] triArray = triangles.ToArray();
            List<Quardrangle> intersectQuard = new List<Quardrangle>();
            //Vector3[] points = mesh.vertices;
            for (int i = 0; i < triArray.Length; i++)
            {
                if (triArray[i].isUse) continue;
                for (int j = i+1; j < triArray.Length && !triArray[i].isUse; j++)
                {
                    if (triArray[j].isUse) continue;
                    if (triArray[i].IsBorderUpon(triArray[j], points))
                    {
                        triArray[i].isUse = true;
                        triArray[j].isUse = true;
                        Quardrangle quard = new Quardrangle();
                        quard.isUse = false;
                        quard.triangle1 = triArray[i];
                        quard.triangle2 = triArray[j];
                        intersectQuard.Add(quard);
                        break;
                    }
                }

            }

            for (int i = 0; i < triArray.Length; i++)
            {
                if (!triArray[i].isUse)
                {
                    Quardrangle triangle = new Quardrangle();
                    triangle.triangle1 = triArray[i];
                    triangle.triangle2 = triArray[i];
                    intersectQuard.Add(triangle);
                }
            }

            return intersectQuard;
        }

        /// <summary>
        /// ıǷıཻ
        /// </summary>
        /// <param name="intersectQuard">ı</param>
        /// <param name="quardrangle">ı</param>
        private static bool CheckUpon(List<Quardrangle> intersectQuard, 
            Quardrangle quardrangle, Vector3[] points)
        {
            for (int i = 0; i < intersectQuard.Count; i++)
            {
                if (quardrangle.IsBorderUpon(intersectQuard[i], points))
                    return true;
            }
            return false;
        }

        public static int currentState = -1;

        /// <summary>  /// MeshΪΪȾMeshҪд  /// </summary>
        //public static ClustersData CreateClusterData(Mesh mesh, int subCount)
        public static ClustersData CreateClusterData(int[] tris,
            //int subIndex
            int subMeshStart, int subMeshCount, Vector3[] points, bool isSimple)
        {
            ClustersData clusters = null;
            currentState = 0;   //׶
                                //ȷе
            List<Triangle> triangles = GetAllTriangles(tris, subMeshStart, subMeshCount);

            currentState = 1;   //ıν׶
                                //ϲı
            List<Quardrangle> intersectQuard = GetAllQuard(triangles, points);

            currentState = 2;   //ؽ׶
                                //еĴ
            List<ClusterData> clusterDatas;

            if (isSimple)
                clusterDatas = CreateClustersByQuardSimple(intersectQuard, points);
            else
                clusterDatas = CreateClustersByQuard(intersectQuard, points);   

            clusters = new ClustersData();
            clusters.clusters = clusterDatas.ToArray();
            currentState = 3;   //׶ν׶

            return clusters;
        }


        //public static Mesh CreateMeshByClusterData(ClustersData clustersData)
        //{
        //    Mesh mesh = new Mesh();
        //    int clusterTriCount = ClusterQuardCount * 6;    //һж
        //    int[] tris = new int[clusterTriCount * clustersData.clusters.Length];
        //    for (int i = 0; i < clustersData.clusters.Length; i++)
        //    {
        //        for (int j = 0; j < clusterTriCount; j++)
        //            tris[i * clusterTriCount + j] = clustersData.clusters[i].tris[j];

        //    }

        //    mesh.vertices = clustersData.vecs;
        //    mesh.triangles = tris;
        //    return mesh;
        //}

        private static List<ClusterData> CreateClustersByQuard(List<Quardrangle> quard,
            Vector3[] points)
        {
            Quardrangle[] quardrangles1 = quard.ToArray();

            List<ClusterData> clusters = new List<ClusterData>();

            //Vector3[] points = mesh.vertices;

            for (int i = 0; i < quardrangles1.Length; i++)
            {
                List<Quardrangle> intersectQuard = new List<Quardrangle>();
                if (quardrangles1[i].isUse) continue;
                quardrangles1[i].isUse = true;
                intersectQuard.Add(quardrangles1[i]);

                ///ģϳһ
                while (true)
                {
                    int minIndex = int.MaxValue;
                    float distance = float.MaxValue;
                    for (int j = i + 1; j < quardrangles1.Length; j++)
                    {
                        if (quardrangles1[j].isUse) continue;
                        //Debug.Log("Equal");
                        if (CheckUpon(intersectQuard, quardrangles1[j], points))
                        {
                            float temDis = quardrangles1[i].GetDistance(quardrangles1[j], points);
                            if (temDis < distance)
                            {
                                minIndex = j;
                                distance = temDis;
                            }
                        }
                    }
                    if (distance == float.MaxValue || intersectQuard.Count == ClusterQuardCount)
                        break;
                    else
                    {
                        quardrangles1[minIndex].isUse = true;
                        intersectQuard.Add(quardrangles1[minIndex]);
                    }
                }
                clusters.Add(CreateCluster(intersectQuard, points));
                intersectQuard.Clear();

            }
            return clusters;
        }

        private static List<ClusterData> CreateClustersByQuardSimple(List<Quardrangle> quard,
    Vector3[] points)
        {
            Quardrangle[] quardrangles1 = quard.ToArray();

            List<ClusterData> clusters = new List<ClusterData>();
            List<Quardrangle> intersectQuard = new List<Quardrangle>();

            //Vector3[] points = mesh.vertices;
            for (int i = 0; i < quardrangles1.Length; i++)
            {
                intersectQuard.Add(quardrangles1[i]);
                if(intersectQuard.Count == ClusterQuardCount)
                {
                    clusters.Add(CreateCluster(intersectQuard, points));
                    intersectQuard.Clear();
                    intersectQuard = new List<Quardrangle>();
                }
            }

            if(intersectQuard.Count > 0)
            {
                clusters.Add(CreateCluster(intersectQuard, points));
            }

            return clusters;
        }

        private static ClusterData CreateCluster(List<Quardrangle> quardrangles,
            Vector3[] points)
        {
            // 防御：空列表直接返回空簇，避免下面 quardrangles[Count-1] 越界崩。
            if (quardrangles == null || quardrangles.Count == 0)
            {
                return new ClusterData
                {
                    tris = new int[0],
                    triCount = 0,
                    boundsMax = Vector3.zero,
                    boundsMin = Vector3.zero,
                };
            }

            // 【P7 meshlet】：不再把簇 padding 到固定 16 四边形（96 索引）。
            // 变长簇（最后一个簇可能不足 16 四边形）能彻底消除索引放大 ——
            // 旧版把每个簇都摊平成 96 顶点 slab 正是顶点调用数 2.2× 放大的根因。
            int realQuadCount = quardrangles.Count;
            int[] tris = new int[realQuadCount * 2 * 3];
            for (int i = 0; i < realQuadCount; i++)
            {
                int index = i * 6;
                tris[index + 0] = quardrangles[i].triangle1.indexs[0];
                tris[index + 1] = quardrangles[i].triangle1.indexs[1];
                tris[index + 2] = quardrangles[i].triangle1.indexs[2];
                tris[index + 3] = quardrangles[i].triangle2.indexs[0];
                tris[index + 4] = quardrangles[i].triangle2.indexs[1];
                tris[index + 5] = quardrangles[i].triangle2.indexs[2];
            }

            Vector3 max = Vector3.one * float.MinValue, min = Vector3.one * float.MaxValue;
            for(int i = 0; i < tris.Length; i++)
            {
                max = Vector3.Max(max, points[tris[i]]);
                min = Vector3.Min(min, points[tris[i]]);
            }

            ClusterData data = new ClusterData();
            data.tris = tris;
            data.triCount = tris.Length;
            data.boundsMax = max;
            data.boundsMin = min;
            return data;
        }
    }
}