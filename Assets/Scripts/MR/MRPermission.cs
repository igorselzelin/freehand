using System;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace Freehand.MR
{
    /// <summary>
    /// The Android permission the Quest asks for before an app may read the room it set up
    /// (walls, furniture): com.oculus.permission.USE_SCENE. Meta's OpenXR runtime returns no
    /// surface at all without it, so the plane source waits for the answer before it asks
    /// for any. Outside an Android device there is no such permission: asking answers "no".
    /// </summary>
    public static class MRPermission
    {
        public const string UseScene = "com.oculus.permission.USE_SCENE";

        public static bool HasScene
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return Permission.HasUserAuthorizedPermission(UseScene);
#else
                return false;
#endif
            }
        }

        /// <summary>Asks for the scene permission (the Quest shows its own dialog); `done(granted)` is called once it is answered - at once if it is already granted.</summary>
        public static void RequestScene(Action<bool> done)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(UseScene))
            {
                done(true);
                return;
            }

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => done(true);
            // "Denied and don't ask again" arrives as a denial too (Unity marks its own event unreliable).
            callbacks.PermissionDenied += _ => done(false);
            Permission.RequestUserPermission(UseScene, callbacks);
#else
            done(false);
#endif
        }
    }
}
