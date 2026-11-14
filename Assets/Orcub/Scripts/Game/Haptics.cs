using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Orcub.Game
{
    /// <summary>
    /// Thin cross-platform haptics layer.
    ///  - iOS: UIImpact / UISelection / UINotification feedback generators (Assets/Plugins/iOS/OrcubHaptics.mm).
    ///  - Android: Vibrator + VibrationEffect predefined effects (API 29+), short one-shots on API 26-28.
    ///  - Editor / other: no-op.
    /// </summary>
    public static class Haptics
    {
        public enum Impact { Light = 0, Medium = 1, Heavy = 2, Soft = 3, Rigid = 4 }
        public enum Notice { Success = 0, Warning = 1, Error = 2 }

        public static bool Enabled = true;

        const float MinInterval = 0.028f; // the Taptic Engine blurs anything faster
        static float _last = -1f;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void OrcubHaptics_Prepare();
        [DllImport("__Internal")] static extern void OrcubHaptics_Impact(int style, float intensity);
        [DllImport("__Internal")] static extern void OrcubHaptics_Selection();
        [DllImport("__Internal")] static extern void OrcubHaptics_Notify(int type);
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaObject _vibrator;
        static AndroidJavaClass _effectClass;
        static int _sdk;

        static bool EnsureAndroid()
        {
            if (_vibrator != null) return true;
            try
            {
                using var version = new AndroidJavaClass("android.os.Build$VERSION");
                _sdk = version.GetStatic<int>("SDK_INT");
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                _vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                if (_sdk >= 26) _effectClass = new AndroidJavaClass("android.os.VibrationEffect");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Orcub] Android haptics unavailable: " + e.Message);
                _vibrator = null;
            }
            return _vibrator != null;
        }

        // VibrationEffect.EFFECT_CLICK = 0, EFFECT_DOUBLE_CLICK = 1, EFFECT_TICK = 2, EFFECT_HEAVY_CLICK = 5
        static void AndroidEffect(int predefined, long fallbackMs, int fallbackAmplitude)
        {
            if (!EnsureAndroid()) return;
            try
            {
                if (_sdk >= 29)
                {
                    using var effect = _effectClass.CallStatic<AndroidJavaObject>("createPredefined", predefined);
                    _vibrator.Call("vibrate", effect);
                }
                else if (_sdk >= 26)
                {
                    using var effect = _effectClass.CallStatic<AndroidJavaObject>("createOneShot", fallbackMs, fallbackAmplitude);
                    _vibrator.Call("vibrate", effect);
                }
                else
                {
                    _vibrator.Call("vibrate", fallbackMs);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Orcub] vibrate failed: " + e.Message);
            }
        }
#endif

        public static void Prepare()
        {
#if UNITY_IOS && !UNITY_EDITOR
            OrcubHaptics_Prepare();
#elif UNITY_ANDROID && !UNITY_EDITOR
            EnsureAndroid();
#endif
        }

        static bool Gate()
        {
            if (!Enabled) return false;
            float now = Time.unscaledTime;
            if (now - _last < MinInterval) return false;
            _last = now;
            return true;
        }

        /// <summary>Gear tooth passing: the lightest possible tick.</summary>
        public static void Tick()
        {
            if (!Gate()) return;
#if UNITY_IOS && !UNITY_EDITOR
            OrcubHaptics_Selection();
#elif UNITY_ANDROID && !UNITY_EDITOR
            AndroidEffect(2, 8, 60);
#endif
        }

        public static void Play(Impact style, float intensity = 1f)
        {
            if (!Gate()) return;
#if UNITY_IOS && !UNITY_EDITOR
            OrcubHaptics_Impact((int)style, intensity);
#elif UNITY_ANDROID && !UNITY_EDITOR
            switch (style)
            {
                case Impact.Heavy: case Impact.Rigid: AndroidEffect(5, 30, 255); break;
                case Impact.Medium: AndroidEffect(0, 20, 180); break;
                default: AndroidEffect(2, 10, 90); break;
            }
#endif
        }

        public static void Notify(Notice type)
        {
            if (!Enabled) return;
            _last = Time.unscaledTime;
#if UNITY_IOS && !UNITY_EDITOR
            OrcubHaptics_Notify((int)type);
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (type == Notice.Success) AndroidEffect(1, 40, 200);
            else AndroidEffect(5, 60, 255);
#endif
        }

#if UNITY_ANDROID
        // Never called: referencing Handheld.Vibrate makes Unity add the VIBRATE permission to the Android manifest.
        static void RequestVibratePermission() => Handheld.Vibrate();
#endif
    }
}
