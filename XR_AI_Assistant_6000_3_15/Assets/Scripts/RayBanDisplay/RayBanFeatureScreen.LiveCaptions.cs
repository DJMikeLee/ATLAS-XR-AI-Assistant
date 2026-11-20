using TMPro;
using UnityEngine;

public partial class RayBanFeatureScreen
{
    private void BuildLiveCaptions()
    {
        var view = views.Find(v => v.key == "captions");
        if (view == null) return;
        var panel = view.root.Find("Transcript panel") as RectTransform;
        if (panel != null) { panel.anchoredPosition = new Vector2(16, -220); panel.sizeDelta = new Vector2(568, 268); }
        var speaker = view.root.Find("Speaker");
        if (speaker != null) speaker.gameObject.SetActive(false);
        if (captionTranscript != null)
        { captionTranscript.rectTransform.anchoredPosition = new Vector2(40, -242); captionTranscript.rectTransform.sizeDelta = new Vector2(520, 222); captionTranscript.fontSize = 27; captionTranscript.richText = false; }
        Button(view.root, "Source language", "comm:captions-source", "Speak: English", "mic", 16, 154, 282, 52, 1, 0);
        Button(view.root, "Target language", "comm:captions-target", "To: Spanish", "captions", 302, 154, 282, 52, 1, 1);
        foreach (var control in controls)
            if (control != null && (control.ActionId == "comm:captions-pause" || control.ActionId == "comm:captions-language")) control.Row = 2;
    }

    private bool HandleLiveCaptions(string action)
    {
        var bridge = shell != null ? shell.captionBridge : null;
        switch (action)
        {
            case "comm:captions-pause": if (bridge != null) bridge.Toggle(); break;
            case "comm:captions-language": if (bridge != null) bridge.ToggleView(); break;
            case "comm:captions-source": if (bridge != null) bridge.CycleSource(); break;
            case "comm:captions-target": if (bridge != null) bridge.CycleTarget(); break;
            default: return false;
        }
        RefreshLiveCaptions();
        return true;
    }

    private void RefreshLiveCaptions()
    {
        if (shell == null || !shell.UsesLiveFeatures) return;
        var bridge = shell.captionBridge;
        var manager = bridge != null ? bridge.manager : null;
        string detail = bridge != null ? bridge.Status : "Caption service is not connected.";
        if (captionStatus != null)
        {
            captionStatus.fontSize = 20;
            captionStatus.text = manager == null ? "Captions not connected" : bridge.IsBusy ? "Preparing captions…" :
                bridge.IsListening ? "Listening…" : "Captions paused";
        }
        if (captionLanguage != null) captionLanguage.text = manager != null && manager.ShowTranslation ? "TRANSLATED" : "ORIGINAL";
        if (captionTranscript != null)
        {
            string history = manager != null ? manager.History : "";
            string current = manager != null ? manager.CurrentText : "";
            string value = (history + current).Trim();
            if (manager != null && !bridge.IsListening && !bridge.IsBusy && value.Length > 0 &&
                detail != "Captions paused." && detail != "Choose Start captions.") value += "\n\n" + detail;
            captionTranscript.richText = false;
            value = value.Length > 0 ? value : manager == null ? detail + "\n\nStart captions is unavailable." :
                detail == "Captions paused." || detail == "Choose Start captions."
                    ? "Choose languages, then select Start captions.\n\nMicrophone and internet access required."
                    : detail;
            if (captionTranscript.text != value || captionTranscript.overflowMode != TextOverflowModes.Page)
            {
                captionTranscript.overflowMode = TextOverflowModes.Page;
                captionTranscript.text = value;
                captionTranscript.ForceMeshUpdate();
                captionTranscript.pageToDisplay = Mathf.Max(1, captionTranscript.textInfo.pageCount);
            }
        }
        bool listening = bridge != null && bridge.IsListening;
        SetButtonLabel("comm:captions-pause", listening ? "Pause" : "Start captions");
        SetButtonSymbol("comm:captions-pause", listening ? "pause" : "mic");
        SetButtonLabel("comm:captions-language", manager != null && manager.ShowTranslation ? "Show original" : "Translate");
        SetButtonLabel("comm:captions-source", "Speak: " + (manager != null ? LiveTranslationManager.LanguageNames[manager.SourceIndex] : "English"));
        SetButtonLabel("comm:captions-target", "To: " + (manager != null ? LiveTranslationManager.LanguageNames[manager.TargetIndex] : "Spanish"));
        SetControlAvailable("comm:captions-pause", manager != null && bridge.IsAcquired && !bridge.IsBusy);
        SetControlAvailable("comm:captions-source", manager != null && !bridge.IsBusy);
        SetControlAvailable("comm:captions-target", manager != null && !bridge.IsBusy);
        SetControlAvailable("comm:captions-language", manager != null);
    }
}
