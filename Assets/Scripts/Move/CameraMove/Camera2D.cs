using UnityEngine;

namespace Motor
{

    public class Camera2D : CameraBase
    {
        /// <summary>        /// 锁定的位置        /// </summary>
        [SerializeField]
        Transform focus = default;

        /// <summary>/// 锁定的偏移距离，当目标点与实际锁定点大于该距离时，才会接近锁定点/// </summary>
        [SerializeField, Min(0f)]
        float focusRadius = 1f;

        /// <summary> /// 摄像机相对主角的偏移高度 /// </summary>
        [SerializeField]
        float upDistance = 2f;

        [SerializeField]
        Vector3 offsetSize;

        /// <summary>    /// 要锁定的目标点，摄像机会移动到该点上,
        /// 但是这个不是实际目标所在位置，可能会有一定的偏移    /// </summary>
        Vector3 focusPoint;
        /// <summary>    /// 每秒的接近比例，设为0.5就是每秒缩小一半    /// </summary>
        [SerializeField, Range(0f, 1f)]
        float focusCentering = 0.5f;

        public override void AdjustPosition(Vector3 offset)
        {
            offsetSize += offset;
        }

        public override void BackToBegin()
        {
            offsetSize = Vector3.up * upDistance;
        }

        public override void BeginFollow()
        {

        }

        public override void StopFollow()
        {
        }

        void Start()
        {
            if (focus == null)
            {
                Destroy(gameObject);
                return;
            }
            focusPoint = transform.position;
            offsetSize = Vector3.up * upDistance;
            focusPoint.z = -10; //默认2D视角的摄像机Z位置
            focusPoint += offsetSize;
            transform.position = focusPoint;
            transform.rotation = Quaternion.identity;
        }

        void Update()
        {
            UpdateFocusPoint();
            transform.position = focusPoint;
        }

        void UpdateFocusPoint()
        {
            Vector3 targetPoint = focus.position;
            targetPoint.z = -10;
            targetPoint += offsetSize;

            if (focusRadius > 0)
            {
                float distance = Vector3.Distance(targetPoint, focusPoint);
                float t = 1f;
                if (focusCentering > 0f)
                {
                    t = 1f - focusCentering * Time.unscaledDeltaTime;
                }
                if (distance > focusRadius)
                {
                    t = Mathf.Min(t, focusRadius / distance);
                }
                focusPoint = Vector3.Lerp(targetPoint, focusPoint, t);
            }
            else
                focusPoint = targetPoint;

        }
    }
}