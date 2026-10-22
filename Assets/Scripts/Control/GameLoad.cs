using UnityEngine;

namespace Common
{
    /// <summary>    
    /// �����࣬��������ȫ���ļ��ط�����Ϊ�˱�֤���أ�ÿһ���ೡ������һ����
    /// �����ֻ�ǵ����࣬�����Ƿ��ظ������ǿ�������ʵ�ֵ�
    /// </summary>
    public class GameLoad : MonoBehaviour
    {
        [SerializeField]
        AudioClip sceneBackgroundAudio;

        /// <summary> /// �Ƿ���Ҫ������� /// </summary>
        public bool isNeedBeginBlack = true;
        ///// <summary>    /// ���п�����ڵ���Ч    /// </summary>
        //public DefferedRender.PostFXSetting fXSetting;


        private void Awake()
        {
            Application.targetFrameRate = -1;
            SceneObjectMap objectMap = SceneObjectMap.Instance;

            GameTask.AsynTaskControl.Instance.RuntimeTask();        //��ʼ������

            ////���ֺ���
            //if (isNeedBeginBlack)
            //{
            //    nowRadio = 0;
            //    SustainCoroutine.Instance.AddCoroutine(ChangeColorFilter);
            //}

            //if (sceneBackgroundAudio == null)
            //    return;
            //if(sceneBackgroundAudio != null &&
            //    !Audio.AudioControl.Instance.CheckAudioIsPlaying(sceneBackgroundAudio))
            //{
            //    Audio.AudioControl.Instance.ChangeBackgroundAduio(sceneBackgroundAudio);
            //}
        }

        //bool ChangeColorFilter()
        //{
        //    nowRadio += Time.deltaTime * 0.5f;
        //    if(nowRadio >= 1.0f)
        //    {
        //        fXSetting.SetColorFilter(Color.white);
        //        return true;
        //    }
        //    fXSetting.SetColorFilter(Color.Lerp(Color.black, Color.white, nowRadio));
        //    return false;
        //}

    }
}