using UnityEngine;

namespace Motor
{
    public class EnemyMotor2D : MotorBase
    {
        [SerializeField]
        /// <summary>    /// 当前速度, 期望速度, 连接物体的速度    /// </summary>
        Vector3 velocity, desiredVelocity, connectionVelocity;
        /// <summary>    /// 加速度    /// </summary>
        public float groundAcceleration = 10f, airAcceleration = 5;

        Rigidbody body;
        GameObject connectObj, preConnectObj;

        /// <summary>    /// 是否在地面上    /// </summary>
        private bool onGround = false;

        /// <summary>        /// 最大地面的倾斜夹角，以及楼梯倾斜夹角        /// </summary>
        [Range(0, 90)]
        public float maxGroundAngle = 25f, maxStairAngle = 25;
        private float minGroundDot = 0, minStairsDot = 0;

        /// <summary>
        /// 接触面的法线，这个法线是平均法线，用来确定移动面的方向
        /// </summary>
        Vector3 contactNormal;

        /// <summary>    /// 贴地检查的层，以及楼梯检查层    /// </summary>
        [SerializeField]
        LayerMask stairsMask = -1;

        Info.CharacterInfo characterInfo;
        Vector3 connectionWorldPostion;

        /// <summary>  /// 限制角色的z空间位置  /// </summary>
        [SerializeField]
        private float ClampZ = 0;

        int groundCount = 0;

        void Awake()
        {
            velocity = Vector3.zero;
            body = GetComponent<Rigidbody>();
            minGroundDot = Mathf.Cos(maxGroundAngle * Mathf.Deg2Rad);
            minStairsDot = Mathf.Cos(maxStairAngle * Mathf.Deg2Rad);
            characterInfo = GetComponent<Info.CharacterInfo>();
            if (characterInfo == null) Debug.LogError("角色信息为空");
        }


        private void FixedUpdate()
        {
            //更新数据，用来对这一个物理帧的数据进行更新之类的
            UpdateState();

            //确定在空中还是在地面
            AdjustVelocity();
            if(isAutoRotate)
                AutoRotate();
            //velocity = Vector3.ClampMagnitude(velocity, 40);

            Vector3 temp = transform.position;
            temp.z = ClampZ; transform.position = temp;

            body.velocity = velocity;
            ClearState();
        }

        void UpdateState()
        {
            velocity = body.velocity;
            //当不在地面时执行贴近地面方法
            if (onGround/*在地上*/)
            {
                contactNormal.Normalize();
            }
            else
                contactNormal = Vector3.up;

            if (connectObj && connectObj.tag == "CheckMove")
            {
                UpdateConnectionState();
            }
        }

        void UpdateConnectionState()
        {
            //只有物体相同，才有必要计算
            if (connectObj == preConnectObj)
            {
                Vector3 connectionMovment =
                    connectObj.transform.position - connectionWorldPostion;
                connectionVelocity = connectionMovment / Time.deltaTime;
            }
            connectionWorldPostion = connectObj.transform.position;
        }

        private void OnCollisionExit(Collision collision)
        {
            EvaluateCollision(collision);
        }


        private void OnCollisionStay(Collision collision)
        {
            EvaluateCollision(collision);
        }

        void EvaluateCollision(Collision collision)
        {
            float minDot = GetMinDot(collision.gameObject.layer);
            if (collision.contactCount > 0)
                groundCount = 1;
            else groundCount = 0;
            for (int i = 0; i < collision.contactCount; i++)
            {
                Vector3 normal = collision.GetContact(i).normal;
                float upDot = Vector3.Dot(Vector3.up, normal);
                if (upDot >= minDot)
                {
                    onGround = true;
                    //保证如果有多个接触面时能够正确的获取法线
                    contactNormal += normal;
                    connectObj = collision.gameObject;
                }
                //陡峭面移动控制，但是避免彻底的垂直面
                else if (upDot > -0.01f)
                {
                    connectObj = collision.gameObject;
                }

            }
        }


        /// <summary>
        /// 调整移动方向，用来保证移动的方向是沿着平面的
        /// </summary>
        void AdjustVelocity()
        {
            //因为速度用的也是世界坐标，因此移动时投影也依靠的是世界坐标，其中right控制x轴，foward控制Y轴
            //将1，0，0投影到接触平面上，
            Vector3 xAixs = ProjectDirectionOnPlane(Vector3.right, contactNormal);

            Vector3 relativeVelocity = velocity - connectionVelocity;
            //确定实际上在这个平面上的X移动值
            float currentX = Vector3.Dot(relativeVelocity, xAixs);

            float acceleration = onGround ? groundAcceleration : airAcceleration;
            float maxSpeedChange = acceleration * Time.fixedDeltaTime;

            //确定根据期望得到的移动值
            float newX = Mathf.MoveTowards(currentX, desiredVelocity.x, maxSpeedChange);

            //移动要根据这个平面的方向来移动，因此根据实际值与期望值的差确定要增加的速度大小，
            //然后乘以投影计算出来的X值以及Z值确定最后的移动值
            velocity += xAixs * (newX - currentX);
        }

        /// <summary>
        /// 清除数据，把一些数据归为初始化
        /// </summary>
        void ClearState()
        {
            onGround = false;
            contactNormal = connectionVelocity = Vector3.zero;
            preConnectObj = connectObj;
            connectObj = null;
        }

        float GetMinDot(int layer)
        {
            //判断是楼梯还是正常地面
            return (stairsMask & (1 << layer)) == 0 ?
                minGroundDot : minStairsDot;
        }


        /// <summary>    /// 确定该方向投影到该平面上的方向值，进行过标准化的    /// </summary>
        Vector3 ProjectDirectionOnPlane(Vector3 direction, Vector3 normal)
        {
            return (direction - normal * Vector3.Dot(direction, normal)).normalized;
        }

        [SerializeField]
        bool isAutoRotate = true;

        /// <summary>    /// 自动根据模型移动方向旋转角度     /// </summary>
        void AutoRotate()
        {
            Vector3 veloiryDir = -velocity;
            veloiryDir.y = 0; veloiryDir.z = 0;
            //有一定速度才会旋转
            if (Mathf.Abs( veloiryDir.x) > 1f)
            {
                transform.right = veloiryDir.x > 0 ? Vector3.left : Vector3.right;
            }
        }


        public override void Move(float horizontal, float vertical)
        {
            //实际上就是加速度是定值，角色输入值是目标速度，逐帧将速度变化为目标速度
            Vector2 playInput = new Vector2(vertical, horizontal);
            playInput = Vector2.ClampMagnitude(playInput, 1);
            //如果由传入的移动空间就根据这个空间的数值移动，不然就直接用世界坐标移动
            desiredVelocity = Vector3.forward * playInput.x + Vector3.right * playInput.y;
            desiredVelocity = desiredVelocity * characterInfo.RunSpeed;
        }

        public override void DesireJump()
        {

        }

        public override bool OnGround()
        {
            return groundCount > 0;
        }

        public override void BeginAutoRotate()
        {
            isAutoRotate = true;
        }

        public override void EndAutoRotate()
        {
            isAutoRotate = false;
        }
    }
}