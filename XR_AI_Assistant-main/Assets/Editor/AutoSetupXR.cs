using UnityEditor;
using UnityEngine;

// [InitializeOnLoad] makes this script run the second the Unity Editor opens!
[InitializeOnLoad]
public class AutoSetupXR
{
    static AutoSetupXR()
    {
        // Check if the project is currently set to Windows/PC instead of Android
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            Debug.LogWarning("⚠️ Wrong platform detected! Switching project to Android for Meta Quest...");

            // Force the project to switch to Android automatically
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        }
    }
}