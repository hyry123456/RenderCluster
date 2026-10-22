using Common;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CustomRenderPipline.Custer
{
    public struct QuardrangleMeshData
    {
        public Vector3[] vects;
        public Vector3[] normals;
        public Vector2[] uv0s;
        public Vector4[] tanges;

        public int[] tris;
    }

    public struct Triangle
    {
        /// <summary>
        /// һӵĶλ
        /// </summary>
        public int[] indexs;

        public bool isUse;

        /// <summary>    /// 判断两个三角形是否共用一条边（即「相邻」）。    ///    /// 注意：索引网格里判断相邻必须比较【顶点索引】，不能比较世界坐标距离——    /// 同一坐标可能是不同索引（UV 接缝 / 硬边），且距离阈值 0.1 会随模型尺度失效。    /// 两三角形共用一条边 == 共享恰好 2 个顶点索引。    /// </summary>
        public bool IsBorderUpon(Triangle triangle, Vector3[] points)
        {
            int shared = 0;
            for (int i = 0; i < 3; i++)
            {
                for (int k = 0; k < 3; k++)
                {
                    if (indexs[i] == triangle.indexs[k])
                        shared++;
                }
            }

            // 共用 2 个索引 = 共边（相邻）；共用 3 个 = 完全重合（也视为相邻）。
            return shared >= 2;
        }


        //public int[] GetEqualData(Triangle triangle)
        //{
        //    int[] vectes = new int[3];
        //    vectes[0] = vectes[1] = vectes[2] = -1;

        //    for(int i = 0; i < 3; i++)
        //    {
        //        for(int j = 0; j <3; j++)
        //        {
        //            if (points[i].Equals(triangle.points[j]))
        //                vectes[i]++;
        //        }
        //    }
        //    return vectes;
        //}

        public Vector3 GetCenter(Vector3[] points)
        {
            return (points[indexs[0]] + points[indexs[1]] + points[indexs[2]]) / 3;
        }

        ///// <summary>    /// ཻĵλ    /// </summary>
        ///// <param name="index">ڼ</param>
        ///// <param name="move">ƶ</param>
        //public void SetPointsPosition(int index, int move)
        //{
        //    if(index == 1)
        //    {
        //        int ori = index;
        //        int ne = index + move;
        //        Vector3 po; Vector2 uv; Vector4 tangent;
        //        //
        //        po = points[ori]; points[ori] = points[ne]; points[ne] = po;
        //        //
        //        po = normals[ori]; normals[ori] = normals[ne]; normals[ne] = po;
        //        //uv
        //        uv = uv0s[ori]; uv0s[ori] = uv0s[ne]; uv0s[ne] = uv;
        //        //
        //        tangent = tanges[ori]; tanges[ori] = tanges[ne]; tanges[ne] = tangent;

        //        po = points[ne]; points[ne] = points[1]; points[1] = po;
        //        po = normals[ne]; normals[ne] = normals[1]; normals[1] = po;
        //        uv = uv0s[ne]; uv0s[ne] = uv0s[1]; uv0s[1] = uv;
        //        tangent = tanges[ne]; tanges[ne] = tanges[1]; tanges[1] = tangent;
        //    }
        //}
    }

    public struct Quardrangle
    {
        public Triangle triangle1;
        public Triangle triangle2;
        public bool isUse;

        public void SetUse()
        {
            isUse = true;
        }

        /// <summary>    /// ıǷཻ    /// </summary>
        /// <param name="queue">һı</param>
        public bool IsBorderUpon(Quardrangle queue, Vector3[] points)
        {
            if (triangle1.IsBorderUpon(queue.triangle1, points) || 
                triangle1.IsBorderUpon(queue.triangle2, points))
            {
                return true;
            }
            if (triangle2.IsBorderUpon(queue.triangle1, points) || 
                triangle2.IsBorderUpon(queue.triangle2, points))
            {
                return true;
            }
            return false;
        }


        /// <summary>    /// õıεľһıεĵľ    /// </summary>
        /// <param name="queue">һı</param>
        public float GetDistance(Quardrangle queue, Vector3[] points)
        {
            Vector3 center1 = (triangle1.GetCenter(points) + triangle2.GetCenter(points)) / 2;
            Vector3 center2 = (queue.triangle1.GetCenter(points) + queue.triangle2.GetCenter(points)) / 2;
            return Vector3.Distance(center2, center1);
        }
    }

    [Serializable]
    /// <summary>
    /// ݣһ164ͣ64㣬ֻ洢ıε
    /// </summary>
    public struct ClusterData
    {
        public int[] tris;
        public int triCount;       // 真实三角索引数（无 padding），簇索引区间长度
        public Vector3 boundsMax;
        public Vector3 boundsMin;
    }

    /// <summary>
    /// ݣ洢еǱݣԶֿ洢
    /// </summary>
    [Serializable]
    public class ClustersData
    {
        public Vector3[] vecs;
        public Vector3[] normals;
        public Vector4[] tangles;
        public Vector2[] uv0s;

        public ClusterData[] clusters;

        /// <summary>        /// ClustersDataתΪClusterıʽ        /// </summary>
        public new string ToString()
        {
            StringBuilder strs = new StringBuilder();

            #region ΪʹMeshRenderĵƹͼֻ洢ݣֱMeshȡ
            ////涥
            //for (int i = 0; i < vecs.Length; i++)
            //{
            //    strs.Append(vecs[i].x.ToString() + "," +
            //                vecs[i].y.ToString() + "," +
            //                vecs[i].z.ToString() + "|");
            //}
            //strs.Append('\n');
            ////淨
            //for (int i = 0; i < normals.Length; i++)
            //{
            //    strs.Append(normals[i].x.ToString() + "," +
            //                normals[i].y.ToString() + "," +
            //                normals[i].z.ToString() + "|");
            //}
            //strs.Append('\n');
            ////
            //for (int i = 0; i < tangles.Length; i++)
            //{
            //    strs.Append(tangles[i].x.ToString() + "," +
            //                tangles[i].y.ToString() + "," +
            //                tangles[i].z.ToString() + "," +
            //                tangles[i].w.ToString() + "|");
            //}
            //strs.Append('\n');
            ////uv
            //for (int i = 0; i < uv0s.Length; i++)
            //{
            //    strs.Append(uv0s[i].x.ToString() + "," +
            //                uv0s[i].y.ToString() + "|");
            //}
            //strs.Append('\n');

            #endregion

            //clusters
            for (int i = 0; i < clusters.Length; i++)
            {
                ClusterData clusterData = clusters[i];
                strs.Append(clusterData.boundsMax.x.ToString() + "," +
                            clusterData.boundsMax.y.ToString() + "," +
                            clusterData.boundsMax.z.ToString() + "|");
                strs.Append(clusterData.boundsMin.x.ToString() + "," +
                            clusterData.boundsMin.y.ToString() + "," +
                            clusterData.boundsMin.z.ToString() + "|\n");//űһ
                for (int j = 0; j < clusterData.tris.Length; j++)
                {
                    strs.Append(clusterData.tris[j].ToString() + "|");
                }
                strs.Append("\n");
            }
            return strs.ToString();
        }

        /// <summary>        /// ClusterıתΪClustersData        /// </summary>
        /// <param name="fileData">Clusterı</param>
        public static ClustersData CreateClustersData(string fileData)
        {
            ClustersData clustersData = new ClustersData();
            string[] lines = fileData.Split('\n');
            #region ϰ汾ȫ
            //clustersData.vecs = ReadVector3(lines[0]);
            //clustersData.normals = ReadVector3(lines[1]);
            //clustersData.tangles = ReadVector4(lines[2]);
            //clustersData.uv0s = ReadVector2(lines[3]);
            //List<ClusterData> datas = new List<ClusterData>();
            //for(int i = 4; i < lines.Length - 1; i += 2)
            //{
            //    ClusterData data = new ClusterData();
            //    Vector3[] maxAndMin = ReadVector3(lines[i]);
            //    data.boundsMax = maxAndMin[0]; data.boundsMin = maxAndMin[1];
            //    data.tris = ReadInts(lines[i + 1]);
            //    datas.Add(data);
            //}
            //clustersData.clusters = datas.ToArray();

            #endregion

            //Ŀǰֻ
            List<ClusterData> datas = new List<ClusterData>();
            for (int i = 0; i < lines.Length - 1; i += 2)
            {
                ClusterData data = new ClusterData();
                Vector3[] maxAndMin = ReadVector3(lines[i]);
                data.boundsMax = maxAndMin[0]; data.boundsMin = maxAndMin[1];
                data.tris = ReadInts(lines[i + 1]);
                datas.Add(data);
            }
            clustersData.clusters = datas.ToArray();

            return clustersData;
        }

        public Pair<Vector3, Vector3>[] GetClusterMinMaxs()
        {
            Pair<Vector3, Vector3>[] res = new Pair<Vector3, Vector3>[clusters.Length];
            for(int i = 0; i < res.Length; i++)
            {
                res[i] = new Pair<Vector3, Vector3>(clusters[i].boundsMin, clusters[i].boundsMax);
            }
            return res;
        }

        private static Vector3[] ReadVector3(string line)
        {
            List<Vector3> re = new List<Vector3>();
            string[] vector3s = line.Split('|');
            for(int i = 0; i < vector3s.Length - 1; i++)
            {
                string[] xyz = vector3s[i].Split(',');
                re.Add(new Vector3(float.Parse(xyz[0]), float.Parse(xyz[1]), float.Parse(xyz[2])));
            }
            return re.ToArray();
        }

        private static Vector4[] ReadVector4(string line)
        {
            List<Vector4> re = new List<Vector4>();
            string[] vector4s = line.Split('|');
            for (int i = 0; i < vector4s.Length - 1; i++)
            {
                string[] xyzw = vector4s[i].Split(',');
                re.Add(new Vector4(float.Parse(xyzw[0]), float.Parse(xyzw[1]), 
                                    float.Parse(xyzw[2]), float.Parse(xyzw[3])));
            }
            return re.ToArray();
        }

        private static Vector2[] ReadVector2(string line)
        {
            List<Vector2> re = new List<Vector2>();
            string[] vector2s = line.Split('|');
            for (int i = 0; i < vector2s.Length - 1; i++)
            {
                string[] xy = vector2s[i].Split(',');
                re.Add(new Vector2(float.Parse(xy[0]), float.Parse(xy[1])));
            }
            return re.ToArray();
        }

        private static int[] ReadInts(string line)
        {
            List<int> re = new List<int>();
            string[] ints = line.Split('|');
            for (int i = 0; i < ints.Length - 1; i++)
            {
                re.Add(int.Parse(ints[i]));
            }
            return re.ToArray();
        }
    }
}