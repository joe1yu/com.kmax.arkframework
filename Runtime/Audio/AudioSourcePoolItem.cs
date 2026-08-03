using System;
using UnityEngine;
using UnityEngine.Audio;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public sealed class AudioSourcePoolItem
    {
        internal AudioSourcePoolItem(Transform parent, long sequence)
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            GameObject = new GameObject(
                "OneShot." + sequence,
                typeof(AudioSource));
            GameObject.hideFlags = HideFlags.HideAndDontSave;
            GameObject.transform.SetParent(parent, false);
            Source = GameObject.GetComponent<AudioSource>();
            Source.playOnAwake = false;
            GameObject.SetActive(false);
        }

        public GameObject GameObject { get; private set; }

        public AudioSource Source { get; private set; }

        internal void Rent()
        {
            if (GameObject == null || Source == null)
            {
                throw new InvalidOperationException(
                    "The pooled AudioSource was destroyed externally.");
            }

            GameObject.SetActive(true);
        }

        internal void Return()
        {
            if (Source != null)
            {
                Source.Stop();
                Source.clip = null;
                Source.outputAudioMixerGroup = null;
                Source.loop = false;
                Source.volume = 1f;
                Source.mute = false;
            }

            if (GameObject != null)
            {
                GameObject.SetActive(false);
            }
        }

        internal void Destroy()
        {
            if (GameObject != null)
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(GameObject);
                }
                else
                {
                    Object.DestroyImmediate(GameObject);
                }
            }

            GameObject = null;
            Source = null;
        }
    }
}
