using UnityEngine;


namespace CustomRenderPipline
{
    public class BoxCollsion : IGetCollsion
    {
        public Vector3 cubeSize = Vector3.one;

        public override CollsionStruct GetCollsionStruct()
        {
            CollsionStruct collsion = new CollsionStruct();
            collsion.mode = 0;
            collsion.offset = cubeSize * 0.5f;
            collsion.localToWorld = transform.localToWorldMatrix;
            collsion.worldToLocal = transform.worldToLocalMatrix;
            return collsion;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            Gizmos.color = Color.green;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, cubeSize);
        }
#endif

        public static CollsionStruct CreateBoxCollsionStruct(
            Vector3 position, Vector3 offset, Quaternion quaternion, Vector3 scale)
        {
            CollsionStruct collsionStruct = new CollsionStruct();
            collsionStruct.mode = 0;
            collsionStruct.offset = offset;
            Matrix4x4 matrix = Matrix4x4.TRS(position, quaternion, scale);
            collsionStruct.localToWorld = matrix;
            collsionStruct.worldToLocal = matrix.inverse;
            return collsionStruct;
        }
    }
}