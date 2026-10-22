using UnityEngine;

namespace CustomRenderPipline
{
    /// <summary> /// ÇòÐÍÅö×²Æ÷ /// </summary>
    public class SphereCollsion : IGetCollsion
    {
        [Min(0.00001f)]
        public float radius = 1;

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            Gizmos.color = Color.green;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireSphere(Vector3.zero, radius);
        }
#endif

        public override CollsionStruct GetCollsionStruct()
        {
            CollsionStruct collsionStruct = new CollsionStruct();
            collsionStruct.mode = 1;
            collsionStruct.radius = radius;
            collsionStruct.localToWorld = transform.localToWorldMatrix;
            collsionStruct.worldToLocal = transform.worldToLocalMatrix;
            return collsionStruct;
        }

        public static CollsionStruct CreateSphereCollsionStruct(float radio, Vector3 center,
            Quaternion quaternion)
        {
            CollsionStruct collsionStruct = new CollsionStruct();
            collsionStruct.radius = radio;
            collsionStruct.mode = 1;
            Matrix4x4 matrix = Matrix4x4.TRS(center, quaternion, Vector3.one);
            collsionStruct.localToWorld = matrix;
            collsionStruct.worldToLocal = matrix.inverse;
            return collsionStruct;
        }
    }
}