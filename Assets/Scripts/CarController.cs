using UnityEngine;

/// <summary>
/// Управляет самим автомобилем: цвет и покрытие кузова, диски, отделка салона,
/// суппорты, фары, вращение колёс, поворот руля, "рентген" и габаритные линии.
/// Все ссылки заполняет сборщик сцены (Assets/Editor/ARCarSetup.cs).
/// </summary>
public class CarController : MonoBehaviour
{
    public enum Finish { Gloss, Matte, Metallic }
    public enum RimStyle { Silver, Black, Gold, BodyColor }

    [Header("Кузов")]
    public Renderer bodyRenderer;
    public Material bodyMaterial;      // непрозрачная краска
    public Material bodyXrayMaterial;  // полупрозрачный кузов для "рентгена"

    [Header("Диски")]
    public Renderer[] rimRenderers;
    public Material rimMaterial;

    [Header("Салон и суппорты")]
    public Renderer[] leatherRenderers;
    public Material leatherMaterial;
    public Renderer[] caliperRenderers;
    public Material caliperMaterial;

    [Header("Фары")]
    public Renderer[] headlightRenderers;
    public Renderer[] taillightRenderers;
    public Material headlightOff, headlightOn;
    public Material taillightOff, taillightOn;
    public GameObject headlightGlow;   // пятно света на земле перед машиной

    [Header("Колёса и руль")]
    public Transform[] frontWheels;
    public Transform[] rearWheels;
    public Transform steeringWheel;
    public Vector3 steeringAxis = Vector3.forward; // ось рулевой колонки в локальных координатах руля
    public float spinSign = 1f;                    // направление вращения колёс (зависит от ориентации модели)

    [Header("Прочее")]
    public Transform eyeAnchor;        // точка глаз водителя для вида из салона
    public GameObject shadowBlob;
    public GameObject dimensions;      // габаритные линии

    public Color BodyColor { get; private set; } = new Color(0.80f, 0.05f, 0.07f);
    public Finish CurrentFinish { get; private set; } = Finish.Gloss;
    public RimStyle CurrentRim { get; private set; } = RimStyle.Silver;
    public bool LightsOn { get; private set; }
    public bool DemoOn { get; private set; }
    public bool XrayOn { get; private set; }
    public bool DimensionsOn { get; private set; }

    // Ручное управление из мини-игры "Вождение".
    [HideInInspector] public bool ManualDrive;
    [HideInInspector] public float manualSpinSpeed; // градусов в секунду, со знаком
    [HideInInspector] public float manualSteer;     // угол поворота передних колёс

    Material bodyInstance, xrayInstance, rimInstance, leatherInstance, caliperInstance;
    Quaternion[] frontInitial, rearInitial;
    Quaternion steeringInitial;
    float spinAngle, demoTime, wheelSpeed, steerBlend;
    Vector3[] steerAxes; // вертикальная ось автомобиля в координатах родителя каждого переднего колеса

    // плавная смена цвета и покрытия
    Color shownColor;
    float shownMetallic = 0.15f, shownSmoothness = 0.93f;

    void Awake()
    {
        bodyInstance = new Material(bodyMaterial);
        xrayInstance = new Material(bodyXrayMaterial);
        rimInstance = new Material(rimMaterial);
        foreach (var r in rimRenderers)
            if (r != null) r.sharedMaterial = rimInstance;

        if (leatherMaterial != null)
        {
            leatherInstance = new Material(leatherMaterial);
            foreach (var r in leatherRenderers)
                if (r != null) r.sharedMaterial = leatherInstance;
        }
        if (caliperMaterial != null)
        {
            caliperInstance = new Material(caliperMaterial);
            foreach (var r in caliperRenderers)
                if (r != null) r.sharedMaterial = caliperInstance;
        }

        frontInitial = Capture(frontWheels);
        rearInitial = Capture(rearWheels);
        // В модели оси колёс повёрнуты, поэтому поворачиваем их вокруг вертикали автомобиля, а не своей оси Y.
        steerAxes = new Vector3[frontWheels.Length];
        for (int i = 0; i < frontWheels.Length; i++)
            steerAxes[i] = frontWheels[i].parent.InverseTransformDirection(transform.up).normalized;
        if (steeringWheel != null) steeringInitial = steeringWheel.localRotation;

        shownColor = BodyColor;
        ApplyBody();
        ApplyRims();
        ApplyLights();
        if (dimensions != null) dimensions.SetActive(false);
    }

    static Quaternion[] Capture(Transform[] list)
    {
        var result = new Quaternion[list.Length];
        for (int i = 0; i < list.Length; i++)
            result[i] = list[i].localRotation;
        return result;
    }

    // ---------- Кузов ----------

    public void SetBodyColor(Color color)
    {
        BodyColor = color;
    }

    public void SetFinish(Finish finish)
    {
        CurrentFinish = finish;
    }

    public void SetXray(bool on)
    {
        XrayOn = on;
        ApplyBody();
    }

    void ApplyBody()
    {
        bodyInstance.color = shownColor;
        bodyInstance.SetFloat("_Metallic", shownMetallic);
        bodyInstance.SetFloat("_Glossiness", shownSmoothness);

        var ghost = shownColor;
        ghost.a = 0.22f;
        xrayInstance.color = ghost;

        if (bodyRenderer != null)
            bodyRenderer.sharedMaterial = XrayOn ? xrayInstance : bodyInstance;
        if (CurrentRim == RimStyle.BodyColor) ApplyRims();
    }

    // ---------- Диски ----------

    public void SetRimStyle(RimStyle style)
    {
        CurrentRim = style;
        ApplyRims();
    }

    void ApplyRims()
    {
        Color color; float metallic = 0.9f, smoothness = 0.8f;
        switch (CurrentRim)
        {
            case RimStyle.Black: color = new Color(0.05f, 0.05f, 0.055f); metallic = 0.6f; smoothness = 0.65f; break;
            case RimStyle.Gold: color = new Color(0.95f, 0.72f, 0.25f); break;
            case RimStyle.BodyColor: color = shownColor; metallic = 0.4f; smoothness = 0.85f; break;
            default: color = new Color(0.78f, 0.79f, 0.82f); break;
        }
        rimInstance.color = color;
        rimInstance.SetFloat("_Metallic", metallic);
        rimInstance.SetFloat("_Glossiness", smoothness);
    }

    // ---------- Салон и суппорты ----------

    public void SetLeatherColor(Color color)
    {
        if (leatherInstance != null) leatherInstance.color = color;
    }

    public void SetCaliperColor(Color color)
    {
        if (caliperInstance != null) caliperInstance.color = color;
    }

    // ---------- Фары ----------

    public void SetLights(bool on)
    {
        LightsOn = on;
        ApplyLights();
    }

    void ApplyLights()
    {
        foreach (var r in headlightRenderers)
            if (r != null) r.sharedMaterial = LightsOn ? headlightOn : headlightOff;
        foreach (var r in taillightRenderers)
            if (r != null) r.sharedMaterial = LightsOn ? taillightOn : taillightOff;
        if (headlightGlow != null)
            headlightGlow.SetActive(LightsOn);
    }

    // ---------- Колёса, габариты, тень ----------

    public void SetDemo(bool on)
    {
        DemoOn = on;
    }

    public void SetDimensions(bool on)
    {
        DimensionsOn = on;
        if (dimensions != null) dimensions.SetActive(on);
    }

    public void SetShadowVisible(bool visible)
    {
        if (shadowBlob != null) shadowBlob.SetActive(visible);
    }

    void Update()
    {
        float dt = Time.deltaTime;

        // Плавный переход цвета и покрытия.
        float metallic, smoothness;
        switch (CurrentFinish)
        {
            case Finish.Matte: metallic = 0.0f; smoothness = 0.25f; break;
            case Finish.Metallic: metallic = 0.85f; smoothness = 0.78f; break;
            default: metallic = 0.15f; smoothness = 0.93f; break;
        }
        if (shownColor != BodyColor || !Mathf.Approximately(shownMetallic, metallic) || !Mathf.Approximately(shownSmoothness, smoothness))
        {
            float t = 1f - Mathf.Exp(-7f * dt);
            shownColor = Color.Lerp(shownColor, BodyColor, t);
            shownMetallic = Mathf.Lerp(shownMetallic, metallic, t);
            shownSmoothness = Mathf.Lerp(shownSmoothness, smoothness, t);
            if (Mathf.Abs(shownColor.r - BodyColor.r) + Mathf.Abs(shownColor.g - BodyColor.g) + Mathf.Abs(shownColor.b - BodyColor.b) < 0.004f)
                shownColor = BodyColor;
            if (Mathf.Abs(shownMetallic - metallic) < 0.004f) shownMetallic = metallic;
            if (Mathf.Abs(shownSmoothness - smoothness) < 0.004f) shownSmoothness = smoothness;
            ApplyBody();
        }

        // Колёса плавно разгоняются и останавливаются, руль возвращается в центр.
        wheelSpeed = Mathf.MoveTowards(wheelSpeed, DemoOn ? 360f : 0f, 300f * dt);
        steerBlend = Mathf.MoveTowards(steerBlend, DemoOn ? 1f : 0f, 1.5f * dt);
        if (DemoOn) demoTime += dt;
        spinAngle = Mathf.Repeat(spinAngle + wheelSpeed * dt, 360f);
        float steer = Mathf.Sin(demoTime * 1.2f) * 28f * steerBlend;
        if (ManualDrive)
        {
            wheelSpeed = manualSpinSpeed;
            steer = manualSteer;
            spinAngle = Mathf.Repeat(spinAngle + wheelSpeed * dt, 360f);
        }

        var spin = Quaternion.Euler(spinAngle * spinSign, 0f, 0f);
        for (int i = 0; i < frontWheels.Length; i++)
            frontWheels[i].localRotation = Quaternion.AngleAxis(steer, steerAxes[i]) * frontInitial[i] * spin;
        for (int i = 0; i < rearWheels.Length; i++)
            rearWheels[i].localRotation = rearInitial[i] * spin;

        if (steeringWheel != null)
            steeringWheel.localRotation = steeringInitial * Quaternion.AngleAxis(-steer * 3f, steeringAxis);
    }
}
