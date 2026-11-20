using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public class MenuEntry
{
    public string name;            // label for clarity in the Inspector
    public GameObject page;        // The UI page to show
    public Button button;          // The button that opens this page
}

[DefaultExecutionOrder(-2000)]
public class QuestDisplayUI : MonoBehaviour
{
    [Header("References")]
    public bool showMainMenuOnStart = true;
    public GameObject mainMenuRoot;
    public List<MenuEntry> entries = new List<MenuEntry>();
    public CanvasGroup canvasGroup; // assign the parent CanvasGroup here in Inspector

    [Header("UI-first visual preview")]
    [Tooltip("Optional Ray-Ban-style shell. Disable its Preview Enabled setting to use the original feature pages.")]
    public RayBanDisplayShell visualShell;

    [Header("Fade Settings")]
    [Tooltip("Duration of fade transitions in seconds.")]
    public float fadeDuration = 0.25f;

    private Coroutine fadeRoutine;
    private int lastPreviewHideFrame = -1;

    void Awake()
    {
        if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
        if (visualShell != null && visualShell.InitializePreview())
        {
            DeactivateAllPages();
            if (mainMenuRoot != null) mainMenuRoot.SetActive(false);
        }
    }

    void Start()
    {
        if (canvasGroup == null)
            canvasGroup = GetComponent<CanvasGroup>();

        if (visualShell != null && visualShell.OwnsPreview)
        {
            if (showMainMenuOnStart) visualShell.ShowLauncher();
            else visualShell.Hide();
            return;
        }

        if (showMainMenuOnStart)
            ShowMainMenuInstant();
        else
            HideMainMenuInstant();

        for (int i = 0; i < entries.Count; i++)
        {
            int index = i;
            var entry = entries[index];
            if (entry.button != null)
                entry.button.onClick.AddListener(() => ShowPage(index));
        }
    }

    public void ShowPage(int index)
    {
        if (index < 0 || index >= entries.Count)
        {
            Debug.LogWarning($"QuestDisplayUI: ShowPage index {index} is out of range.");
            return;
        }

        if (visualShell != null && visualShell.OwnsPreview)
        {
            visualShell.ShowLegacyPage(index);
            return;
        }

        if (fadeRoutine != null) StopCoroutine(fadeRoutine);
        fadeRoutine = StartCoroutine(FadeTransition(() =>
        {
            // Hide main menu
            mainMenuRoot.SetActive(false);

            // Hide all pages
            DeactivateAllPages();

            // Show the selected page
            var selected = entries[index]?.page;
            if (selected != null)
                selected.SetActive(true);
        }));
    }

    public void ShowMainMenuIfHidden()
    {
        if (visualShell != null && visualShell.OwnsPreview)
        {
            // A visible placeholder counts as an open page on the shared thumb-tap event.
            if (!visualShell.IsVisible && lastPreviewHideFrame != Time.frameCount) visualShell.ShowLauncher();
            return;
        }
        bool isShowing = false;

        for (int i = 0; i < entries.Count; i++)
        {
            var page = entries[i]?.page;
            if (page != null && page.activeSelf)
                isShowing = true;
        }

        //if no page show or no main menu
        if (!isShowing && !mainMenuRoot.activeSelf)
            ShowMainMenu();
    }
         

    public void ShowMainMenu()
    {
        if (visualShell != null && visualShell.OwnsPreview)
        {
            visualShell.ShowLauncher();
            return;
        }
        if (fadeRoutine != null) StopCoroutine(fadeRoutine);
        fadeRoutine = StartCoroutine(FadeTransition(() =>
        {
            DeactivateAllPages();
            mainMenuRoot.SetActive(true);
        }));
    }

    public void HideMainMenu()
    {
        if (visualShell != null && visualShell.OwnsPreview)
        {
            lastPreviewHideFrame = Time.frameCount;
            visualShell.Hide();
            return;
        }
        if (fadeRoutine != null) StopCoroutine(fadeRoutine);
        fadeRoutine = StartCoroutine(FadeTransition(() =>
        {
            DeactivateAllPages();
            mainMenuRoot.SetActive(false);
        }));
    }

    private void HideMainMenuInstant()
    {
        DeactivateAllPages();
        if (mainMenuRoot != null) mainMenuRoot.SetActive(false);
        if (canvasGroup != null) canvasGroup.alpha = 0f;
    }


    /// <summary>
    /// Immediate show (no fade), used only at Start.
    /// </summary>
    private void ShowMainMenuInstant()
    {
        DeactivateAllPages();
        if (mainMenuRoot != null) mainMenuRoot.SetActive(true);
        if (canvasGroup != null) canvasGroup.alpha = 1f;
    }

    private void DeactivateAllPages()
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var page = entries[i]?.page;
            if (page != null) page.SetActive(false);
        }
    }

    private IEnumerator FadeTransition(Action onMidTransition)
    {
        // Fade out
        if (canvasGroup != null)
        {
            yield return Fade(1f, 0f);
        }

        onMidTransition?.Invoke();

        // Fade in
        if (canvasGroup != null)
        {
            yield return Fade(0f, 1f);
        }
    }

    private IEnumerator Fade(float from, float to)
    {
        float t = 0f;
        canvasGroup.alpha = from;

        while (t < fadeDuration)
        {
            t += Time.deltaTime;
            canvasGroup.alpha = Mathf.Lerp(from, to, t / fadeDuration);
            yield return null;
        }

        canvasGroup.alpha = to;
    }
}
