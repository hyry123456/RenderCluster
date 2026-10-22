using Common;
using UnityEngine;

namespace Skill
{
    //��ͨ����
    public class MagicAttack : SkillBase
    {
        static GameObject simpleAttack, sphereEnd;
        AnimateManage animateManage;
        Info.CharacterInfo characterInfo;
        LayerMask attackLayer;

        Info.CharacterInfo target;

        /// <summary>        /// ��ʼ������        /// </summary>
        public MagicAttack()
        {
            maxSkillCount = -1;
            skillCount = maxSkillCount;
            releaseTime = 3.5f;
            coolTime = 8;
            skillName = "��ͨ����";
            skillType = SkillType.LongDisAttack;
            if (simpleAttack == null)
            {
                simpleAttack = (GameObject)Resources.Load("Skill/SimpleAttack");
                sphereEnd = (GameObject)Resources.Load("Skill/SphereEnd");
            }
        }

        public override bool OnSkillRelease(SkillManage mana)
        {
            mana.AnimateManage.SetAttack(1, 1.5f);
            mana.AnimateManage.animateMidEvent = AnimateMid;
            mana.AnimateManage.animateEndEvent = AnimateEnd;
            animateManage = mana.AnimateManage;
            attackLayer = mana.CharacterInfo.attackLayer;
            characterInfo = mana.CharacterInfo;
            target = Info.CharacterInfo.GetNearCharacter(animateManage.transform.position,
                Info.CharacterType.Enemy);
            if (target == null) return true;
            Vector3 beg = animateManage.transform.position, targetPos = target.transform.position;
            if (!Physics.Raycast(beg, targetPos - beg, out RaycastHit hit,
                100, 5121))
            //100))
            {
                Vector3 direction = (target.transform.position - animateManage.transform.position).normalized;
                //direction.y = animateManage.transform.position.y;
                direction.y = 0;
                animateManage.transform.forward = direction.normalized;        //��ת����ȷ����
            }
            else
            {
                target = null;
            }



            return true;
        }

        public void AnimateMid()
        {

            isRelease = false;
            SustainCoroutine.Instance.AddCoroutine(FollowHand);
            waitTime = -1;
        }

        public void AnimateEnd()
        {
            isRelease = true;
        }

        bool isRelease = false;
        TriggerEvent colliderEvent;
        float waitTime;

        public bool FollowHand()
        {
            if (!isRelease)
            {
                if(waitTime < 0)
                {
                    waitTime = 0.3f;
                    PoolingObject obj = SceneObjectPool.Instance.GetObject<PoolingObject>("SimpleAttack",
                        simpleAttack, (animateManage.RightHand.position + animateManage.LeftHand.position) / 2,
                        Quaternion.identity);
                    // [PARTICLE-DISABLED] CustomRenderPipline.GPUParticleSystem particle = obj.GetComponent<CustomRenderPipline.GPUParticleSystem>();
                    // particle.ChangeState(CustomRenderPipline.State.Playing);


                    if(target != null)
                    {
                        obj.transform.forward = (target.transform.position - obj.transform.position).normalized;
                    }
                    else
                        obj.transform.forward = animateManage.transform.forward;

                    var body = obj.GetComponent<Rigidbody>();
                    body.angularVelocity = Vector3.zero; body.velocity = Vector3.zero;
                    obj.WaitTimeToClose(5, MoveSkill, (poolObj) =>
                    {
                        // [PARTICLE-DISABLED] CustomRenderPipline.GPUParticleSystem particle = poolObj.GetComponent<CustomRenderPipline.GPUParticleSystem>();
                        // particle.ChangeState(CustomRenderPipline.State.ReleasePaused);
                        poolObj.WaitTimeToClose(3f, null, (poolObjs) =>
                        {
                            poolObjs.CloseObject();
                        });
                    });

                    colliderEvent = obj.gameObject.AddComponent<TriggerEvent>();
                    colliderEvent.SetCollsionEnterEvent((collider, oriObj) =>
                    {
                        if(((int)Mathf.Pow(2, collider.gameObject.layer) & attackLayer.value) == 0)
                        {
                            //Debug.Log($"{collider.gameObject.name} �ܵ�����");
                            return false;       //���ǿ��Դ��������壬ֱ���˳�
                        }

                        //�ر���Ч������
                        var colliderPooling = oriObj.GetComponent<PoolingObject>();
                        // [PARTICLE-DISABLED] CustomRenderPipline.GPUParticleSystem particle = colliderPooling.GetComponent<CustomRenderPipline.GPUParticleSystem>();
                        // particle.ChangeState(CustomRenderPipline.State.ReleasePaused);
                        colliderPooling.WaitTimeToClose(3f, null, (poolObjs) =>
                        {
                            poolObjs.CloseObject();
                        });

                        PoolingObject endObj = SceneObjectPool.Instance.GetObject<PoolingObject>("SphereEnd",
                            sphereEnd, oriObj.transform.position,
                            Quaternion.identity);

                        var contact = collider.ClosestPoint(oriObj.transform.position);
                        endObj.transform.up = (oriObj.transform.position - contact).normalized;
                        // [PARTICLE-DISABLED] CustomRenderPipline.GPUParticleSystem endPar = endObj.GetComponent<CustomRenderPipline.GPUParticleSystem>();
                        // endPar.ChangeState(CustomRenderPipline.State.Playing);
                        endObj.WaitTimeToClose(0.3f, null, (endObj1) =>
                        {
                            // endPar.ChangeState(CustomRenderPipline.State.ReleasePaused);
                            endObj1.WaitTimeToClose(2f, null, (endObj2) =>
                            {
                                endObj2.CloseObject();
                            });
                        });
                        return true;

                    });
                }
                //obj.transform.position = (animateManage.RightHand.position + animateManage.LeftHand.position) / 2;
                waitTime -= Time.deltaTime;

                return false;
            }

            return true;
        }

        public void MoveSkill(PoolingObject pooling)
        {
            pooling.transform.position += pooling.transform.forward * Time.deltaTime * 20;
        }
    }
}