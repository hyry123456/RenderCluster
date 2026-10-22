using UnityEngine;

namespace Info
{

    public class PlayerInfo : CharacterInfo
    {
        //DefferedRender.PostFXSetting fXSetting;

        private void Awake()
        {
            attack = InfoMap.Instance.GetIntData("Attack");
            defense = InfoMap.Instance.GetIntData("Defense");
            Common.SceneBeginData beginData = Common.SceneControl.Instance.BeginData;
            if (!beginData.haveBeginData) return;
            transform.position = beginData.beginPos;
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            //fXSetting.SetColorFilter(Color.white);
        }


        Color minCol = new Color(1, 0.7f, 0.7f);

        protected override void Update()
        {
            base.Update();
            Color target = Color.Lerp(minCol, Color.white, (float)hp / maxHP);
        }

        protected override void DealWithDeath()
        {
            hp = maxHP;

        }

    }
}