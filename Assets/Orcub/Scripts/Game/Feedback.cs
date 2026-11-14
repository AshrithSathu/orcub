using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// Maps game events to sound + haptics, so every moment has one consistent "feel".
    ///   Pick      soft tap            + soft impact
    ///   Tick      (silent)            + selection tick      (per gear detent while a ring turns)
    ///   Release   clay clunk          + medium impact       (a clamp slips off: the reward)
    ///   Locked    dull thud           + warning notification (touched a ring that is still holding another)
    ///   PullOut   (silent)            + light impact
    ///   Settle    soft low tock       + soft impact         (splash logo lands)
    ///   Cleared   marimba chime       + success notification
    /// </summary>
    public sealed class Feedback : MonoBehaviour
    {
        const int Voices = 10;

        OrcubArt _art;
        AudioSource[] _voices;
        int _next;

        public static Feedback Create(Transform parent, OrcubArt art)
        {
            var go = new GameObject("Feedback");
            go.transform.SetParent(parent, false);
            var fb = go.AddComponent<Feedback>();
            fb._art = art;
            fb._voices = new AudioSource[Voices];
            for (int i = 0; i < Voices; i++)
            {
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f;
                fb._voices[i] = src;
            }
            // Without a listener nothing is audible; make sure there is exactly one.
            if (FindAnyObjectByType<AudioListener>() == null)
            {
                var host = Camera.main != null ? Camera.main.gameObject : go;
                host.AddComponent<AudioListener>();
            }
            Haptics.Prepare();
            return fb;
        }

        void Play(AudioClip clip, float volume, float pitch)
        {
            if (clip == null) return;
            var v = _voices[_next];
            _next = (_next + 1) % _voices.Length;
            v.pitch = pitch;
            v.PlayOneShot(clip, volume * _art.masterVolume);
        }

        static float Jitter(float amount) => 1f + Random.Range(-amount, amount);

        /// <summary>Bigger rings sound lower, like bigger gears.</summary>
        static float PitchForRadius(float radius) => Mathf.Lerp(1.18f, 0.86f, Mathf.InverseLerp(1.0f, 1.4f, radius));

        public void Pick(float radius)
        {
            Play(_art.sfxPick, 0.55f, PitchForRadius(radius) * Jitter(0.04f));
            Haptics.Play(Haptics.Impact.Soft, 0.55f);
        }

        /// <summary>Gear tooth passing while a ring turns: haptic only (a click sound on every tooth got grating).</summary>
        public void Tick(float radius, float speedDegPerSec)
        {
            Haptics.Tick();
        }

        public void Release()
        {
            Play(_art.sfxClunk, 0.95f, Jitter(0.05f));
            Haptics.Play(Haptics.Impact.Medium, 1f);
        }

        public void Locked()
        {
            Play(_art.sfxThud, 0.85f, Jitter(0.04f));
            Haptics.Notify(Haptics.Notice.Warning);
        }

        /// <summary>Ring pulled out: just a light tap in the hand, no sound (the whoosh felt noisy).</summary>
        public void PullOut(float radius)
        {
            Haptics.Play(Haptics.Impact.Light, 0.8f);
        }

        /// <summary>The splash mark settles into place: a soft, low clay "tock" and a gentle tap.</summary>
        public void Settle()
        {
            Play(_art.sfxPick, 0.8f, 0.72f);
            Play(_art.sfxTick, 0.35f, 0.6f);
            Haptics.Play(Haptics.Impact.Soft, 0.9f);
        }

        public void Cleared()
        {
            Play(_art.sfxChime, 0.8f, 1f);
            Haptics.Notify(Haptics.Notice.Success);
        }
    }
}
