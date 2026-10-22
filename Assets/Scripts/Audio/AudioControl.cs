using UnityEngine;
using Common;

namespace Audio
{
    public class AudioControl : MonoBehaviour
    {
        private static AudioControl instance;
        public static AudioControl Instance
        {
            get
            {
                if(instance == null)
                {
                    GameObject game = new GameObject("AudioControl");
                    game.AddComponent<AudioControl>();
                }
                return instance;
            }
        }

        AudioSource nowAudioSource;
        AudioSource preAudioSource;



        float changeTime = 3,       //背景音乐切换时需要的时间 
            volume = 1;             //音乐大小
        float nowRadio;

        private void Awake()
        {
            if(instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
            nowAudioSource = gameObject.AddComponent<AudioSource>();
            preAudioSource = gameObject.AddComponent<AudioSource>();
            nowAudioSource.loop = preAudioSource.loop = true;
        }

        public void ChangeBackgroundAduio(AudioClip audio)
        {
            if (nowAudioSource.clip == null)
            {
                preAudioSource.clip = null;
                preAudioSource.Stop();
            }
            else
            {
                preAudioSource.clip = nowAudioSource.clip;
                preAudioSource.volume = volume;
                preAudioSource.Play();
            }
            nowAudioSource.clip = audio;
            nowAudioSource.volume = 0;
            nowAudioSource.Play();
            SustainCoroutine.Instance.AddCoroutine(ChangeAduio);
        }

        /// <summary>     /// 动态更新背景音乐    /// </summary>
        bool ChangeAduio()
        {
            nowRadio += Time.deltaTime * (1.0f / changeTime);
            if(nowRadio >= 1.0f)
            {
                nowAudioSource.volume = volume;   //完全开启
                if (preAudioSource.clip != null)
                {
                    preAudioSource.Stop();            //停止播放
                    preAudioSource.volume = 0;        //关闭之前
                }
                return true;
            }
            nowAudioSource.volume = Mathf.Lerp(0, volume, nowRadio);
            if (preAudioSource.clip != null)
                preAudioSource.volume = Mathf.Lerp(volume, 0, nowRadio);
            return false;
        }

        /// <summary>
        /// 简单该音乐是否正在播放
        /// </summary>
        /// <param name="audio">需要用来匹配的音乐文件</param>
        /// <returns>true表示正在播放中，没必要换</returns>
        public bool CheckAudioIsPlaying(AudioClip audio)
        {
            if (nowAudioSource == null || nowAudioSource.clip != audio)
                return false;
            return true;
        }
    }
}