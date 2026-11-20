using UnityEngine;

public class MicroGestureUINavigatorInput : MonoBehaviour
{
    public OVRMicrogestureEventSource microGesture;
    public bool debugKeyboardInput = true;
    private UINavigator uiNavigator;
    private QuestDisplayUI displayUI;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        uiNavigator = GetComponent<UINavigator>();
        displayUI = FindFirstObjectByType<QuestDisplayUI>();
    }

    // Update is called once per frame
    void Update()
    {
        if(debugKeyboardInput && !DisplayTextInputFocus.IsEditing)
        {
            if(Input.GetKeyDown(KeyCode.U))
            {
                uiNavigator.MoveUp();
            }
            if (Input.GetKeyDown(KeyCode.H))
            {
                uiNavigator.MoveLeft();
            }
            if (Input.GetKeyDown(KeyCode.K))
            {
                uiNavigator.MoveRight();
            }
            if (Input.GetKeyDown(KeyCode.J))
            {
                uiNavigator.MoveDown();
            }
            if (Input.GetKeyDown(KeyCode.Space))
            {
                if (displayUI != null && displayUI.visualShell != null && displayUI.visualShell.OwnsPreview && !displayUI.visualShell.IsVisible)
                    displayUI.ShowMainMenuIfHidden();
                else uiNavigator.ClickSelected();
            }
            if (Input.GetKeyDown(KeyCode.Escape) && displayUI != null && displayUI.visualShell != null && displayUI.visualShell.OwnsPreview)
            {
                if (!string.IsNullOrEmpty(displayUI.visualShell.CurrentDetail)) displayUI.visualShell.Back();
                else displayUI.HideMainMenu();
            }
        }
    }

    protected virtual void OnEnable()
    {
        microGesture.WhenGestureRecognized += HandleGesture;
    }

    protected virtual void OnDisable()
    {
        microGesture.WhenGestureRecognized -= HandleGesture;
    }

    private void HandleGesture(OVRHand.MicrogestureType gesture)
    {
        var features = displayUI != null && displayUI.visualShell != null ? displayUI.visualShell.FeatureScreens : null;
        if (features != null && features.IsAdjustingMediaWithTwist) return;
        if (gesture == OVRHand.MicrogestureType.SwipeRight)
        {
            uiNavigator.MoveRight();
        }
        else if (gesture == OVRHand.MicrogestureType.SwipeLeft)
        {
            uiNavigator.MoveLeft();
        }
        else if (gesture == OVRHand.MicrogestureType.SwipeForward)
        {
            uiNavigator.MoveUp();
        }
        else if (gesture == OVRHand.MicrogestureType.SwipeBackward)
        {
            uiNavigator.MoveDown();
        }
        else if (gesture == OVRHand.MicrogestureType.ThumbTap)
        {
            uiNavigator.ClickSelected();
        }
    }
}
