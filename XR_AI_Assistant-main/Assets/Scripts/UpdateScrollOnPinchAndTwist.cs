using UnityEngine;
using UnityEngine.UI;

public class UpdateScrollOnPinchAndTwist : MonoBehaviour
{
    [Header("References")]
    public PinchAndTwistCustomEvent pinchTwist; // The gesture event script
    public ScrollRect scrollRect;               // The Scroll View to drive

    [Header("Tuning")]
    [Tooltip("How much to scroll per unit of twist. Use negative values to invert scroll direction.")]
    public float sensitivity = -1.0f;

    [Tooltip("Ignore tiny changes to prevent jitter")]
    public float deadzone = 0.0025f;

    private float _lastValue;
    private bool _active;

    void Reset()
    {
        scrollRect = GetComponentInChildren<ScrollRect>();
    }

    void OnEnable()
    {
        if (!pinchTwist) return;
        pinchTwist.OnStartPinchAndTwist.AddListener(OnStart);
        pinchTwist.OnEndPinchAndTwist.AddListener(OnEnd);
        pinchTwist.OnPinchAndTwist.AddListener(OnTwistValue);
    }

    void OnDisable()
    {
        if (!pinchTwist) return;
        pinchTwist.OnStartPinchAndTwist.RemoveListener(OnStart);
        pinchTwist.OnEndPinchAndTwist.RemoveListener(OnEnd);
        pinchTwist.OnPinchAndTwist.RemoveListener(OnTwistValue);
    }

    void OnStart()
    {
        _active = true;
        _lastValue = 0f; // gesture restarts at 0 on start
    }

    void OnEnd()
    {
        _active = false;
    }

    void OnTwistValue(float currentValue)
    {
        if (!_active || scrollRect == null) return;

        float delta = currentValue - _lastValue;
        _lastValue = currentValue;

        if (Mathf.Abs(delta) < deadzone) return;

        // verticalNormalizedPosition goes from 0 (bottom) to 1 (top)
        float newV = Mathf.Clamp01(scrollRect.verticalNormalizedPosition + delta * sensitivity);
        scrollRect.verticalNormalizedPosition = newV;
    }
}