using System;
using UnityEditor;

// The NuGet CLI can restore these native ARM64 libraries with Android disabled.
// Reapply the four libraries' original Quest import settings on first import.
internal sealed class AzureSpeechAndroidImporter : AssetPostprocessor
{
    private const string AndroidSpeechDirectory =
        "Assets/Packages/Microsoft.CognitiveServices.Speech.1.48.2/runtimes/android-arm64/native/";

    public override uint GetVersion() => 1;

    private void OnPreprocessAsset()
    {
        if (!assetPath.StartsWith(AndroidSpeechDirectory, StringComparison.Ordinal)
            || !assetPath.EndsWith(".so", StringComparison.Ordinal)
            || !(assetImporter is PluginImporter plugin))
            return;

        plugin.SetCompatibleWithAnyPlatform(false);
        plugin.SetCompatibleWithPlatform(BuildTarget.Android, true);
        plugin.SetPlatformData(BuildTarget.Android, "CPU", "ARM64");
    }
}
