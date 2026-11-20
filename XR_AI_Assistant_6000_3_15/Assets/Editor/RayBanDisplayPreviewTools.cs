using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;

/// <summary>Author and inspect the native UI without starting any headset/cloud services.</summary>
public static class RayBanDisplayPreviewTools
{
    private const string FontFolder = "Assets/Art/RayBanDisplay/Fonts/";

    [MenuItem("Tools/Ray-Ban Display/Rebuild visual preview")]
    public static void Install()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before rebuilding the preview.");
        var router = UnityEngine.Object.FindFirstObjectByType<QuestDisplayUI>(FindObjectsInactive.Include);
        if (!router || router.gameObject.scene.path != "Assets/Scenes/UI Quest Display.unity")
            throw new InvalidOperationException("Open the UI Quest Display scene first.");
        Undo.RecordObject(router, "Connect Ray-Ban display preview");
        var shell = router.GetComponent<RayBanDisplayShell>();
        if (!shell) shell = Undo.AddComponent<RayBanDisplayShell>(router.gameObject);
        Undo.RecordObject(shell, "Configure Ray-Ban display preview");
        var regular = FontAsset("Regular");
        var bold = FontAsset("Bold");
        regular.fontWeightTable[7] = new TMP_FontWeightPair { regularTypeface = bold };
        // Reuse the already imported language fonts for live speech and place names.
        var fallbackFonts = new[] { "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset", "Assets/NANUMGOTHIC SDF.asset", "Assets/NotoSansCJKjp-Regular SDF.asset", "Assets/NotoSansCJKsc-Regular SDF.asset" }
            .Select(path => AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path)).Where(asset => asset != null).ToArray();
        foreach (var face in new[] { regular, bold })
        {
            if (face.fallbackFontAssetTable == null) face.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset>();
            foreach (var fallback in fallbackFonts)
                if (fallback != face && !face.fallbackFontAssetTable.Contains(fallback)) face.fallbackFontAssetTable.Add(fallback);
            EditorUtility.SetDirty(face);
            AssetDatabase.SaveAssetIfDirty(face);
        }
        EditorUtility.SetDirty(regular);
        AssetDatabase.SaveAssetIfDirty(regular);
        shell.font = regular;
        shell.samplePhotos = new[] { "coast", "forest", "overlook" }
            .Select(name => AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/RayBanDisplay/Samples/gallery-" + name + ".png")).ToArray();
        if (shell.samplePhotos.Any(photo => photo == null)) throw new InvalidOperationException("Import the three bundled sample photos first.");
        shell.router = router;
        shell.previewEnabled = true;
        shell.appSettings = shell.GetComponent<RayBanAppSettings>();
        if (!shell.appSettings) shell.appSettings = Undo.AddComponent<RayBanAppSettings>(shell.gameObject);
        shell.cameraBridge = shell.GetComponent<RayBanCameraBridge>();
        if (!shell.cameraBridge) shell.cameraBridge = Undo.AddComponent<RayBanCameraBridge>(shell.gameObject);
        shell.musicBridge = shell.GetComponent<RayBanMusicBridge>();
        if (!shell.musicBridge) shell.musicBridge = Undo.AddComponent<RayBanMusicBridge>(shell.gameObject);
        shell.assistantBridge = shell.GetComponent<RayBanAssistantBridge>();
        if (!shell.assistantBridge) shell.assistantBridge = Undo.AddComponent<RayBanAssistantBridge>(shell.gameObject);
        Undo.RecordObject(shell.assistantBridge, "Connect existing Assistant");
        shell.assistantBridge.manager = UnityEngine.Object.FindObjectsByType<AIManager>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Single(c => c.gameObject.scene == router.gameObject.scene);
        shell.assistantBridge.serviceRoot = router.gameObject.scene.GetRootGameObjects().Single(go => go.name == "AI Behaviour");
        EditorUtility.SetDirty(shell.assistantBridge);
        shell.captionBridge = shell.GetComponent<RayBanCaptionBridge>();
        if (!shell.captionBridge) shell.captionBridge = Undo.AddComponent<RayBanCaptionBridge>(shell.gameObject);
        shell.mapBridge = shell.GetComponent<RayBanMapBridge>();
        if (!shell.mapBridge) shell.mapBridge = Undo.AddComponent<RayBanMapBridge>(shell.gameObject);
        Undo.RecordObject(shell.captionBridge, "Connect existing Captions");
        Undo.RecordObject(shell.mapBridge, "Connect existing Maps");
        shell.captionBridge.manager = UnityEngine.Object.FindObjectsByType<LiveTranslationManager>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Single(c => c.gameObject.scene == router.gameObject.scene);
        shell.mapBridge.search = UnityEngine.Object.FindObjectsByType<MapSearch>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Single(c => c.gameObject.scene == router.gameObject.scene);
        EditorUtility.SetDirty(shell.captionBridge);
        EditorUtility.SetDirty(shell.mapBridge);
        Undo.RecordObject(shell.cameraBridge, "Connect existing camera");
        Undo.RecordObject(shell.musicBridge, "Connect existing music");
        shell.cameraBridge.pictureTaker = UnityEngine.Object.FindObjectsByType<PassthroughCameraSamples.PassthroughCameraPictureTaker>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Single(c => c.gameObject.scene == router.gameObject.scene);
        shell.cameraBridge.cameraAccess = UnityEngine.Object.FindObjectsByType<Meta.XR.PassthroughCameraAccess>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Single(c => c.gameObject.scene == router.gameObject.scene);
        shell.musicBridge.player = UnityEngine.Object.FindObjectsByType<SimpleAudioPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Single(c => c.gameObject.scene == router.gameObject.scene);
        EditorUtility.SetDirty(shell.cameraBridge);
        EditorUtility.SetDirty(shell.musicBridge);
        shell.servicesToSuspend = router.gameObject.scene.GetRootGameObjects()
            .Where(go => go.name == "AI Behaviour" || go.name == "Passthrough Camera").ToArray();
        if (shell.servicesToSuspend.Length != 2) throw new InvalidOperationException("Expected AI Behaviour and Passthrough Camera service roots.");
        router.visualShell = shell;
        if (router.canvasGroup) { Undo.RecordObject(router.canvasGroup, "Show visual preview"); router.canvasGroup.alpha = 1; }
        Undo.RegisterFullObjectHierarchyUndo(router.gameObject, "Build native display UI");
        shell.InitializePreview();
        shell.BuildVisualTree();
        EditorUtility.SetDirty(shell);
        EditorUtility.SetDirty(router);
        EditorSceneManager.MarkSceneDirty(router.gameObject.scene);
        SceneView.RepaintAll();
    }

    private static TMP_FontAsset FontAsset(string weight)
    {
        string path = FontFolder + "NotoSans-" + weight + " SDF.asset";
        var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        if (existing) return existing;
        var source = AssetDatabase.LoadAssetAtPath<Font>(FontFolder + "NotoSans-" + weight + ".ttf");
        if (!source) throw new InvalidOperationException("Noto Sans source font not imported: " + weight);
        var asset = TMP_FontAsset.CreateFontAsset(source, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic);
        asset.name = "NotoSans-" + weight + " SDF";
        string chars = string.Concat(Enumerable.Range(32, 95).Select(i => (char)i)) + "’‘“”·–—…";
        if (!asset.TryAddCharacters(chars, out string missing)) throw new InvalidOperationException("Missing font characters: " + missing);
        asset.atlasPopulationMode = AtlasPopulationMode.Static;
        AssetDatabase.CreateAsset(asset, path);
        asset.material.name = asset.name + " Material";
        AssetDatabase.AddObjectToAsset(asset.material, asset);
        foreach (var texture in asset.atlasTextures)
        {
            texture.name = asset.name + " Atlas";
            AssetDatabase.AddObjectToAsset(texture, asset);
        }
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);
        return asset;
    }

    /// <summary>Render a copy of the actual authored Canvas in a temporary, isolated preview scene.</summary>
    public static Texture2D Render(RayBanDisplayShell shell, int pixels = 900)
    {
        if (!shell || !shell.VisualRoot) throw new InvalidOperationException("Build the display preview first.");
        var previewScene = EditorSceneManager.NewPreviewScene();
        RenderTexture target = null;
        var previous = RenderTexture.active;
        try
        {
            var copy = UnityEngine.Object.Instantiate(shell.VisualRoot.gameObject);
            foreach (var feature in copy.GetComponentsInChildren<RayBanFeatureScreen>(true)) feature.MarkAsRenderCopy();
            foreach (var map in copy.GetComponentsInChildren<RouteMapView>(true)) map.enabled = false;
            SceneManager.MoveGameObjectToScene(copy, previewScene);
            copy.SetActive(true);
            copy.transform.SetParent(null, false);
            copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            copy.transform.localScale = Vector3.one;
            foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 31;
            var originalTiles = shell.VisualRoot.GetComponentsInChildren<RayBanTile>(true);
            var copiedTiles = copy.GetComponentsInChildren<RayBanTile>(true);
            for (int i = 0; i < originalTiles.Length; i++)
                copiedTiles[i].SetVisualFocus(originalTiles[i].Surface != null && originalTiles[i].Surface.Focus > .5f);
            foreach (var t in copy.GetComponentsInChildren<TMP_Text>(true)) t.ForceMeshUpdate(true);
            var cameraObject = new GameObject("Temporary UI render camera", typeof(Camera));
            SceneManager.MoveGameObjectToScene(cameraObject, previewScene);
            var camera = cameraObject.GetComponent<Camera>();
            camera.scene = previewScene;
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 300;
            camera.aspect = 1;
            camera.transform.position = new Vector3(0, 0, -1000);
            camera.nearClipPlane = 1;
            camera.farClipPlane = 2000;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.035f, .065f, .095f, 1);
            camera.cullingMask = 1 << 31;
            target = new RenderTexture(pixels, pixels, 24, RenderTextureFormat.ARGB32);
            target.antiAliasing = 4;
            camera.targetTexture = target;
            copy.GetComponent<Canvas>().worldCamera = camera;
            Canvas.ForceUpdateCanvases();
            camera.Render();
            RenderTexture.active = target;
            var image = new Texture2D(pixels, pixels, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, pixels, pixels), 0, 0);
            image.Apply();
            return image;
        }
        finally
        {
            RenderTexture.active = previous;
            if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
    }

    public static void Capture(RayBanDisplayShell shell, string path)
    {
        var image = Render(shell, 1200);
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, image.EncodeToPNG()); }
        finally { UnityEngine.Object.DestroyImmediate(image); }
    }

    [MenuItem("Tools/Ray-Ban Display/Open visual preview")]
    public static void OpenPreview() => RayBanDisplayPreviewWindow.Open();
}

public class RayBanDisplayPreviewWindow : EditorWindow
{
    private Texture2D rendered;
    private RayBanDisplayShell shell;
    private int feature;
    public static void Open() => GetWindow<RayBanDisplayPreviewWindow>("Display Preview");
    private void OnDisable() { if (rendered) DestroyImmediate(rendered); }
    private void Refresh()
    {
        if (rendered) DestroyImmediate(rendered);
        rendered = RayBanDisplayPreviewTools.Render(shell);
        Repaint();
    }
    private void OnGUI()
    {
        if (!shell) shell = UnityEngine.Object.FindFirstObjectByType<RayBanDisplayShell>(FindObjectsInactive.Include);
        if (!shell || !shell.VisualRoot) { EditorGUILayout.HelpBox("Use Tools > Ray-Ban Display > Rebuild visual preview.", MessageType.Info); return; }
        EditorGUILayout.LabelField("Native Unity UI · visual preview", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        string[] titles = { "Quick settings", "Notifications", "Apps" };
        for (int i = 0; i < 3; i++)
            if (GUILayout.Button(titles[i])) { shell.SetPage(i); Refresh(); }
        if (GUILayout.Button("Refresh")) Refresh();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        string[] apps = { "Camera", "Photos", "Music", "Assistant", "Captions", "Maps", "Messages", "WhatsApp", "Messenger", "Calls", "Instagram", "Tutorials" };
        feature = EditorGUILayout.Popup(feature, apps);
        if (GUILayout.Button("Open sample")) { shell.OpenApp(apps[feature]); Refresh(); }
        if (GUILayout.Button("Back")) { shell.Back(); Refresh(); }
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(shell.CurrentDetail) && shell.FeatureScreens)
        {
            EditorGUILayout.BeginHorizontal();
            foreach (var control in shell.FeatureScreens.Controls)
                if (control && control.gameObject.activeInHierarchy && control.ActionId != "back" && GUILayout.Button(control.Title))
                { shell.FeatureScreens.Activate(control); Refresh(); break; }
            EditorGUILayout.EndHorizontal();
        }
        if (!rendered) Refresh();
        float size = Mathf.Min(position.width - 16, position.height - 128);
        var rect = GUILayoutUtility.GetRect(size, size);
        GUI.DrawTexture(rect, rendered, ScaleMode.ScaleToFit);
        EditorGUILayout.LabelField("Play Mode: U / H / J / K move · Space selects", EditorStyles.miniLabel);
    }
}
