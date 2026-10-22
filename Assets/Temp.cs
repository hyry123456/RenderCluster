using UnityEngine;
using UnityEngine.Rendering;

public class Temp : MonoBehaviour
{

    private Matrix4x4 TRS(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        float num = rotation.x * 2f;
        float num2 = rotation.y * 2f;   //0
        float num3 = rotation.z * 2f;//0
        float num4 = rotation.x * num;//0
        float num5 = rotation.y * num2;//0
        float num6 = rotation.z * num3;//0
        float num7 = rotation.x * num2;//0
        float num8 = rotation.x * num3;//0
        float num9 = rotation.y * num3;//0
        float num10 = rotation.w * num;//0
        float num11 = rotation.w * num2;//0
        float num12 = rotation.w * num3;//0

        Matrix4x4 result = default(Matrix4x4);

        result.m00 = scale.x * (1f - (num5 + num6));
        result.m01 = scale.y * (num7 - num12);
        result.m02 = scale.z * (num8 + num11);
        result.m03 = position.x;

        result.m10 = scale.x * (num7 + num12);
        result.m11 = scale.y * (1f - (num4 + num6));
        result.m12 = scale.z * (num9 - num10);
        result.m13 = position.y;

        result.m20 = scale.x * (num8 - num11);
        result.m21 = scale.y * (num9 + num10);
        result.m22 = scale.z * (1f - (num4 + num5));
        result.m23 = position.z;

        result.m30 = 0f;
        result.m31 = 0f;
        result.m32 = 0f;
        result.m33 = 1f;

        return result;
    }
    void Start()
    {
        TRS(Vector3.zero, Quaternion.identity, Vector3.one);
    }

    // Update is called once per frame
    void Update()
    {
    }
}
