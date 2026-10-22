using Common;
using UnityEngine;

namespace Skill
{
    public class SimpleAttack : SkillBase
    {
        static GameObject simpleAttack, sphereEnd;
        Info.CharacterInfo target;
        SkillManage mana;
        public SimpleAttack()
        {
            maxSkillCount = -1;
            skillCount = maxSkillCount;
            releaseTime = 1f;
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
            this.mana = mana;
            target = Info.CharacterInfo.GetNearCharacter(mana.transform.position,
                Info.CharacterType.Player);
            if (target == null)
                return false;
            waitTime = 0f; count = 0;
            SustainCoroutine.Instance.AddCoroutine(ReleaseSphere);
            return true;
        }

        float waitTime;
        int count = 0;
        bool ReleaseSphere()
        {
            waitTime -= Time.deltaTime;
            if (waitTime < 0)
            {
                waitTime = 0.3f;
                PoolingObject obj = SceneObjectPool.Instance.GetObject<PoolingObject>("SimpleAttack",
                    simpleAttack, mana.transform.position,
                    Quaternion.identity);
                // [PARTICLE-DISABLED] CustomRenderPipline.GPUParticleSystem particle = obj.GetComponent<CustomRenderPipline.GPUParticleSystem>();
                // particle.ChangeState(CustomRenderPipline.State.Playing);

                obj.transform.forward = (target.transform.position + Vector3.up - obj.transform.position).normalized;

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

                var colliderEvent = obj.gameObject.AddComponent<TriggerEvent>();
                colliderEvent.SetCollsionEnterEvent((collider, oriObj) =>
                {
                    if (((int)Mathf.Pow(2, collider.gameObject.layer) & mana.CharacterInfo.attackLayer.value) == 0)
                    {
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

                count++;
                if (count >= 3)
                {
                    return true;
                }
                return false;
            }
            return false;
        }

        public void MoveSkill(PoolingObject pooling)
        {
            pooling.transform.position += pooling.transform.forward * Time.deltaTime * 20;
        }
    }
}