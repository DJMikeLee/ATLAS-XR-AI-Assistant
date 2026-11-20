using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Authored screens using the original focus path; selected media/settings have live adapters.</summary>
[DisallowMultipleComponent]
public partial class RayBanFeatureScreen : MonoBehaviour
{
    [Serializable] private class View
    {
        public string key;
        public RectTransform root;
        public TMP_Text heading;
    }
    [SerializeField] private RayBanDisplayShell shell;
    [SerializeField] private TMP_FontAsset font;
    [SerializeField] private List<View> views = new List<View>();
    [SerializeField] private List<RayBanTile> controls = new List<RayBanTile>();
    [SerializeField] private string currentView;
    [SerializeField] private RayBanTile focused;
    public Texture2D[] samplePhotos = Array.Empty<Texture2D>();
    public string CurrentView => currentView;
    public IReadOnlyList<RayBanTile> Controls => controls;
    private bool renderingCopy;
    public void MarkAsRenderCopy() { UnbindMediaTwist(); renderingCopy = true; }
    private void Update()
    {
        if (renderingCopy) return;
        TickMediaTwist();
        TickMedia();
        if (shell != null && shell.UsesLiveFeatures)
        {
            if (currentView == "assistant") RefreshLiveAssistant();
            if (currentView == "captions") RefreshLiveCaptions();
            TickLiveNavigation();
        }
    }

    private void OnDisable()
    {
        UnbindMediaTwist();
        if (renderingCopy) return;
        if (shell != null && shell.captionBridge != null) shell.captionBridge.Release();
        ShowLiveNavigation(false);
        if (shell != null && shell.UsesLiveFeatures && shell.assistantBridge != null) shell.assistantBridge.Release();
        if (shell != null && shell.UsesLiveFeatures && shell.cameraBridge != null)
            shell.cameraBridge.SetCameraVisible(false);
        if (mediaCameraImage && Application.isPlaying && shell != null && shell.UsesLiveFeatures)
            mediaCameraImage.texture = null;
    }

    public void Build(RayBanDisplayShell owner, TMP_FontAsset typeface, Texture2D[] photos)
    {
        shell = owner;
        font = typeface;
        samplePhotos = photos ?? Array.Empty<Texture2D>();
        views.Clear();
        controls.Clear();
        BuildMedia();
        BuildCommunication();
        BuildLiveCaptions();
        BuildUtilities();
        foreach (var view in views) view.root.gameObject.SetActive(false);
    }

    public void Open(string app)
    {
        settingsParent = false;
        app = string.IsNullOrEmpty(app) ? "Settings" : app.Replace("\n", " ");
        string key = app.ToLowerInvariant();
        if (key == "whatsapp" || key == "messenger" || key == "messages")
        {
            SetMessagingBrand(app);
            key = "messages";
        }
        if (!views.Exists(v => v.key == key))
        {
            key = "unavailable";
            SetHeader(key, app);
            if (unavailableDescription) unavailableDescription.text = app + " is a visual preview.\nThis control is not connected yet.";
        }
        ShowView(key);
    }

    public void OpenNotification(string sender, int index)
    {
        notificationSender.text = sender;
        notificationMessage.text = new[] { "Yes! Let’s do it!", "You have 5 sample messages.", "Let’s meet at 3pm", "Check up with Sheena" }[Mathf.Clamp(index, 0, 3)];
        ShowView("notifications");
    }

    public void Back()
    {
        if (settingsParent && (currentView == "volume" || currentView == "brightness"))
        { settingsParent = false; ShowView("settings"); return; }
        if (!CommunicationBack()) shell.ReturnToLauncher();
    }

    public void Activate(RayBanTile tile)
    {
        if (tile == null || !tile.gameObject.activeInHierarchy || !tile.IsInteractable() || !controls.Contains(tile)) return;
        if (tile.ActionId == "back") { Back(); return; }
        if (HandleMedia(tile.ActionId) || HandleCommunication(tile.ActionId)) return;
        HandleUtilities(tile.ActionId);
    }

    public void Navigate(MoveDirection direction)
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        var tile = selected != null ? selected.GetComponent<RayBanTile>() : focused;
        if (tile == null || tile.PageId != -2 || !tile.gameObject.activeInHierarchy) FocusAction("back");
        else NavigateFrom(tile, direction);
    }

    public void NavigateFrom(RayBanTile from, MoveDirection direction)
    {
        RayBanTile best = null;
        int distance = int.MaxValue;
        foreach (var tile in controls)
        {
            if (!tile || !tile.gameObject.activeInHierarchy || !tile.IsInteractable() || tile == from) continue;
            int row = tile.Row - from.Row, col = tile.Column - from.Column;
            bool vertical = direction == MoveDirection.Up || direction == MoveDirection.Down;
            bool match = direction == MoveDirection.Up ? row < 0 : direction == MoveDirection.Down ? row > 0 :
                direction == MoveDirection.Left ? row == 0 && col < 0 : direction == MoveDirection.Right && row == 0 && col > 0;
            int cost = vertical ? Math.Abs(row) * 100 + Math.Abs(col) : Math.Abs(col);
            if (match && cost < distance) { best = tile; distance = cost; }
        }
        if (best) Focus(best);
    }

    private void ShowView(string key)
    {
        if (!views.Exists(v => v.key == key)) return;
        UnbindMediaTwist(); // A held gesture must never continue in the next page.
        if (shell.UsesLiveFeatures)
        {
            if (shell.assistantBridge != null && key != "assistant") shell.assistantBridge.Release();
            if (shell.cameraBridge != null) shell.cameraBridge.SetCameraVisible(key == "camera" || key == "assistant");
            if (key == "music" && shell.musicBridge != null) shell.musicBridge.Acquire();
        }
        currentView = key;
        foreach (var view in views) view.root.gameObject.SetActive(view.key == key);
        if (shell.UsesLiveFeatures)
        {
            if (key == "assistant" && shell.assistantBridge != null) shell.assistantBridge.Acquire();
            if (shell.captionBridge != null) shell.captionBridge.SetVisible(key == "captions");
            ShowLiveNavigation(key == "maps");
        }
        foreach (var view in views)
        {
            var badge = view.root.Find("Demo badge");
            if (badge != null) badge.gameObject.SetActive(!shell.UsesLiveFeatures ||
                !(view.key == "captions" || view.key == "maps" || view.key == "tutorials" || view.key == "assistant" || view.key == "camera" || view.key == "photos" || view.key == "music" ||
                  view.key == "settings" || view.key == "volume" || view.key == "brightness"));
        }
        if (key == "camera") RefreshMediaCamera();
        if (key == "photos") RefreshMediaPhotos();
        if (key == "music") RefreshMediaMusic();
        if (key == "assistant" && shell.UsesLiveFeatures) RefreshLiveAssistant();
        if (key == "captions" && shell.UsesLiveFeatures) RefreshLiveCaptions();
        RefreshSettings();
        FocusAction("back");
        BindMediaTwist();
    }

    private void FocusAction(string action)
    {
        var view = views.Find(v => v.key == currentView);
        if (view == null) return;
        foreach (var tile in controls)
            if (tile && tile.ActionId == action && tile.IsInteractable() && tile.transform.IsChildOf(view.root)) { Focus(tile); return; }
    }

    private void Focus(RayBanTile tile)
    {
        focused = tile;
        foreach (var control in controls) if (control) control.SetVisualFocus(control == tile);
        if (Application.isPlaying && EventSystem.current && tile.gameObject.activeInHierarchy)
            EventSystem.current.SetSelectedGameObject(tile.gameObject);
    }

    private void SetHeader(string key, string title)
    {
        var view = views.Find(v => v.key == key);
        if (view != null) view.heading.text = title;
    }

    private void SetButtonLabel(string action, string value)
    {
        foreach (var tile in controls)
            if (tile && tile.ActionId == action)
            {
                tile.Title = value;
                if (tile.LabelRect) tile.LabelRect.GetComponent<TMP_Text>().text = value;
            }
    }

    private void SetButtonSymbol(string action, string symbol)
    {
        foreach (var tile in controls)
            if (tile && tile.ActionId == action)
            {
                tile.Symbol = symbol;
                if (tile.IconGlyph) tile.IconGlyph.GetComponent<RayBanGlyph>().Symbol = symbol;
            }
    }

    private RectTransform Page(string key, string title)
    {
        var root = Rect(key + " sample screen", transform, 0, 0, 600, 600);
        var heading = Text(root, "Heading", title, 92, 18, 380, 54, 30, true);
        Text(root, "Demo badge", "DEMO", 494, 31, 86, 30, 15, true, new Color(.64f,.78f,.86f));
        views.Add(new View { key = key, root = root, heading = heading });
        Button(root, "Back", "back", "Back", "back", 20, 16, 56, 56, 0, 0, true);
        return root;
    }

    private static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rect.gameObject.layer = 5;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(w, h);
        return rect;
    }

    private TMP_Text Text(Transform parent, string name, string value, float x, float y, float w, float h,
        float size = 24, bool bold = false, Color? color = null)
    {
        var text = Rect(name, parent, x, y, w, h).gameObject.AddComponent<TextMeshProUGUI>();
        text.font = font != null ? font : TMP_Settings.defaultFontAsset;
        text.text = value;
        text.fontSize = size;
        text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        text.color = color ?? new Color(.98f,.99f,1);
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = TextAlignmentOptions.TopLeft;
        return text;
    }

    private RayBanSurface Panel(Transform parent, string name, float x, float y, float w, float h,
        Color? color = null, float radius = 24)
    {
        var panel = Rect(name, parent, x, y, w, h).gameObject.AddComponent<RayBanSurface>();
        panel.color = color ?? new Color(1,1,1,.9f);
        panel.Tint = color ?? new Color(.24f,.48f,.62f);
        panel.Radius = radius;
        panel.raycastTarget = false;
        return panel;
    }

    private RayBanGlyph Glyph(Transform parent, string name, string symbol, float x, float y, float w, float h, Color? color = null)
    {
        var glyph = Rect(name, parent, x, y, w, h).gameObject.AddComponent<RayBanGlyph>();
        glyph.Symbol = symbol;
        glyph.color = color ?? Color.white;
        glyph.raycastTarget = false;
        return glyph;
    }

    private RayBanTile Button(Transform parent, string name, string action, string label, string symbol,
        float x, float y, float w, float h, int row, int col, bool iconOnly = false, Color? accent = null)
    {
        var panel = Panel(parent, name, x, y, w, h);
        panel.Tint = accent ?? new Color(.3f,.6f,.76f);
        panel.raycastTarget = true;
        var rect = panel.rectTransform;
        rect.pivot = new Vector2(.5f,.5f);
        rect.anchoredPosition += new Vector2(w * .5f, -h * .5f);
        var tile = panel.gameObject.AddComponent<RayBanTile>();
        tile.Shell = shell; tile.Surface = panel; tile.PageId = -2;
        tile.ActionId = action; tile.Title = label; tile.Symbol = symbol;
        tile.Row = row; tile.Column = col; tile.targetGraphic = panel;
        tile.transition = Selectable.Transition.None;
        tile.navigation = new Navigation { mode = Navigation.Mode.None };
        var glyph = Glyph(tile.transform, "Control glyph", symbol, iconOnly ? (w-30)/2 : 14, (h-30)/2, 30, 30);
        var glyphRect = glyph.rectTransform;
        glyphRect.pivot = new Vector2(.5f,.5f);
        glyphRect.anchoredPosition += new Vector2(15,-15);
        RectTransform labelRect = null;
        if (!iconOnly)
        {
            var text = Text(tile.transform, "Control label", label, 52, 8, w-62, h-16, 20, true);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            labelRect = text.rectTransform;
        }
        tile.ConfigureVisuals(null, glyphRect, labelRect);
        controls.Add(tile);
        return tile;
    }

    private RawImage Picture(Transform parent, string name, int photoIndex, float x, float y, float w, float h)
    {
        var frame = Panel(parent, name + " frame", x, y, w, h, Color.white, 24);
        frame.Flat = true;
        frame.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        var image = Rect(name, frame.transform, 0, 0, w, h).gameObject.AddComponent<RawImage>();
        image.raycastTarget = false;
        SetPhoto(image, photoIndex);
        return image;
    }

    private void SetPhoto(RawImage image, int index)
    {
        if (!image || samplePhotos == null || samplePhotos.Length == 0) return;
        var texture = samplePhotos[((index % samplePhotos.Length) + samplePhotos.Length) % samplePhotos.Length];
        image.texture = texture;
        if (!texture) return;
        float source = (float)texture.width / texture.height;
        float target = image.rectTransform.rect.width / Mathf.Max(1, image.rectTransform.rect.height);
        float width = Mathf.Min(1, target / source), height = Mathf.Min(1, source / target);
        image.uvRect = new Rect((1-width)/2, (1-height)/2, width, height);
    }

    private Image Bar(Transform parent, string name, float x, float y, float w, float h, Color color)
    {
        var image = Rect(name, parent, x, y, w, h).gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }
}
