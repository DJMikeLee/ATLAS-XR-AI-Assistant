using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// SDK 207 populates this Resources asset even when DevAgent is disabled.
// Sanitize only that disabled feature; leave app API configuration and Editor auth alone.
[InitializeOnLoad]
internal sealed class DisabledMetaDevAgentCredentialGuard : AssetPostprocessor,
    IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    private const string SettingsPath = "Assets/Resources/DevAgentSettings.asset";
    private const string DisabledMarker = "disabled";
    public int callbackOrder => int.MaxValue; // After Meta's order-1 token injection/restoration.

    static DisabledMetaDevAgentCredentialGuard()
    {
        EditorApplication.delayCall += SanitizeAndSave;
    }

    private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
        string[] movedAssets, string[] movedFromAssetPaths, bool didDomainReload)
    {
        bool relevant = didDomainReload;
        foreach (string path in importedAssets)
            if (path == SettingsPath) relevant = true;
        if (!relevant) return;
        EditorApplication.delayCall -= SanitizeAndSave;
        EditorApplication.delayCall += SanitizeAndSave;
    }

    public void OnPreprocessBuild(BuildReport report) => SanitizeAndSave();
    public void OnPostprocessBuild(BuildReport report) => SanitizeAndSave();

    internal static void SanitizeAndSave()
    {
        var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(SettingsPath);
        if (Sanitize(asset)) AssetDatabase.SaveAssetIfDirty(asset);
    }

    internal static bool Sanitize(Object asset)
    {
        if (!asset) return false;
        var serialized = new SerializedObject(asset);
        var enabled = serialized.FindProperty("enabled");
        var token = serialized.FindProperty("accessToken");
        if (enabled == null || token == null || enabled.boolValue || token.stringValue == DisabledMarker)
            return false;
        // Empty values are automatically repopulated by SDK 207, even while disabled.
        // Deliberately enabling DevAgent later requires its normal authentication setup.
        token.stringValue = DisabledMarker;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        return true;
    }
}

internal sealed class DisabledMetaDevAgentSaveGuard : AssetModificationProcessor
{
    private static string[] OnWillSaveAssets(string[] paths)
    {
        foreach (string path in paths)
            if (path == "Assets/Resources/DevAgentSettings.asset")
                DisabledMetaDevAgentCredentialGuard.Sanitize(
                    AssetDatabase.LoadAssetAtPath<ScriptableObject>(path));
        return paths;
    }
}
