using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Подстраивает виртуальный свет под реальное освещение комнаты:
/// ARCore оценивает яркость и оттенок кадра, а мы меняем яркость
/// направленного света и окружающего освещения.
/// </summary>
[RequireComponent(typeof(Light))]
public class ARLightEstimator : MonoBehaviour
{
    public ARCameraManager cameraManager;
    public float baseIntensity = 1.1f;

    Light sun;
    float targetIntensity = 1f;
    Color targetColor = Color.white;

    void Awake()
    {
        sun = GetComponent<Light>();
        targetIntensity = baseIntensity;
    }

    void OnEnable()
    {
        if (cameraManager != null) cameraManager.frameReceived += OnFrame;
    }

    void OnDisable()
    {
        if (cameraManager != null) cameraManager.frameReceived -= OnFrame;
    }

    void OnFrame(ARCameraFrameEventArgs args)
    {
        var estimate = args.lightEstimation;
        if (estimate.averageBrightness.HasValue)
        {
            // 0.5 — "нормальная" освещённость кадра.
            targetIntensity = baseIntensity * Mathf.Clamp(estimate.averageBrightness.Value * 2f, 0.35f, 1.6f);
        }
        if (estimate.colorCorrection.HasValue)
        {
            Color c = estimate.colorCorrection.Value;
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max > 0.001f)
                targetColor = new Color(c.r / max, c.g / max, c.b / max, 1f);
        }
    }

    void Update()
    {
        float t = 1f - Mathf.Exp(-4f * Time.deltaTime);
        sun.intensity = Mathf.Lerp(sun.intensity, targetIntensity, t);
        sun.color = Color.Lerp(sun.color, targetColor, t);
        RenderSettings.ambientIntensity = Mathf.Lerp(RenderSettings.ambientIntensity, targetIntensity / baseIntensity, t);
        RenderSettings.reflectionIntensity = Mathf.Clamp01(RenderSettings.ambientIntensity);
    }
}
