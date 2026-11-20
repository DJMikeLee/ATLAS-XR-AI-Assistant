# XR AI Assistant for Meta Quest 3

This standalone Unity project presents a compact heads-up display with hand gestures, live captions, translation, assistant speech, camera and gallery controls, and walking navigation. Some features use external services. The scene uses the Built-in Render Pipeline and the Oculus XR Plug-in. The campus game that later integrated this assistant lives in the [ATLAS Quad Games repository](https://github.com/ATLAS-Illinois/ATLAS-VR-Quad-Games).

## Open the project

1. Install Unity **6000.3.15f1** with Android Build Support, its SDK/NDK tools, and OpenJDK.
2. Restore the ten packages pinned in `Assets/packages.config`. Before opening Unity, run `nugetforunity restore .` from this folder with the [NuGetForUnity CLI 4.5.0](https://github.com/GlitchEnzo/NuGetForUnity#restoring-nuget-packages-over-the-command-line). Alternatively, use **NuGet → Restore Packages** in the editor. A first import without the NuGet dependencies can show compiler errors until restore finishes.
3. In Unity Hub, add this folder and open `Assets/Scenes/UI Quest Display.unity`. For a headset build, choose Android in Build Profiles. The scene is already enabled in the build settings.

## Configure optional services

Choose **ATLAS VR → API Setup** to enter your own Gemini, Azure Speech, or PieSocket settings for the corresponding features. Unity writes the settings to the ignored `Assets/Resources/API_Secrets.asset`. For Wit voice recognition, connect your own application through the Meta Voice tooling. This release contains blank Wit token fields. Keep populated settings local, including any edits to the tracked Wit configuration templates. Unity embeds assets from Resources into player builds, so distribute only builds configured with services suitable for client deployment.

## Media and fonts

The published playlist uses three original synthesized instrumentals with matching covers. The project also includes neutral replacements for selected legacy icons and sample textures. The original music, album covers, and Windows system fonts are excluded. The Japanese and Chinese font fallbacks use Noto Sans CJK and the Korean fallback uses Nanum Gothic.

Find the open font licenses and ZXing license in `ThirdPartyNotices/`. [Noto CJK](https://github.com/notofonts/noto-cjk/tree/main/Sans) and [Nanum Gothic](https://github.com/google/fonts/tree/main/ofl/nanumgothic) supply the fallback fonts. Additional runtime-art credits and licenses are in `Assets/Art/RayBanDisplay/` and its subfolders. The camera samples retain their own license in `Assets/PassthroughCameraApiSamples/`. Unity, Meta and the external service SDKs retain their respective terms.
