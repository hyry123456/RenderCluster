using UnityEngine;
namespace Motor
{
    public class CharacterMotor : MotorBase
    {
        CharacterController controller;
        public Transform playerInputSpace;
        public Vector3 currentSpeed;

        private void Start()
        {
            controller = GetComponent<CharacterController>();
        }

        public override void BeginAutoRotate()
        {
        }

        public override void DesireJump()
        {
        }

        public override void EndAutoRotate()
        {
        }

        public override void Move(float horizontal, float vertical)
        {
            //实际上就是加速度是定值，角色输入值是目标速度，逐帧将速度变化为目标速度
            Vector2 playInput = new Vector2(vertical, horizontal);
            playInput = Vector2.ClampMagnitude(playInput, 1);

            Vector3 desiredVelocity;
            //如果由传入的移动空间就根据这个空间的数值移动，不然就直接用世界坐标移动
            if (playerInputSpace)
            {
                Vector3 forward = playerInputSpace.forward; forward.y = 0; forward.Normalize();
                Vector3 right = playerInputSpace.right; right.y = 0; right.Normalize();

                desiredVelocity = forward * playInput.x + right * playInput.y;
                //Vector3 offset = desiredVelocity; offset.y = 0;
                //if (offset.x > 0.1) offset.x = 1;
                //else if (offset.x < -0.1) offset.x = -1;

                //if (offset.z > 0.1) offset.z = 1;
                //else if (offset.z < -0.1) offset.z = -1;
                //transform.LookAt(transform.position + offset.normalized);
                transform.forward = Vector3.Slerp(transform.forward, desiredVelocity.normalized, 0.3f);
            }
            else
                desiredVelocity = Vector3.forward * playInput.x + Vector3.right * playInput.y;
            //desiredVelocity = desiredVelocity * characterInfo.RunSpeed;
            desiredVelocity.y = -1f;
            if(SpeedSize(currentSpeed) > SpeedSize(desiredVelocity))
            {
                currentSpeed = Vector3.Lerp(currentSpeed, desiredVelocity, 1f);
            }
            else
            {
                currentSpeed = Vector3.Lerp(currentSpeed, desiredVelocity, 0.1f);
            }

            controller.Move(currentSpeed * 0.15f);
        }
        private float SpeedSize(Vector3 speed)
        {
            return Mathf.Abs(speed.x) + Mathf.Abs(speed.y) + Mathf.Abs(speed.z);
        }

        public override bool OnGround()
        {
            return false;
        }
    }
}