using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Весь интерфейс приложения: стартовый экран, подсказки, панель конфигуратора,
/// информационные точки на автомобиле, вид из салона и снимок экрана.
/// Ссылки на элементы заполняет сборщик сцены (Assets/Editor/ARCarSetup.cs).
/// </summary>
public class AppUI : MonoBehaviour
{
    [Serializable]
    public class Hotspot
    {
        public Transform anchor;       // точка на автомобиле
        public RectTransform marker;   // кружок "i" на экране
        public Button button;
        public string title;
        [TextArea(3, 8)] public string body;
    }

    [Serializable]
    public class DimensionLabel
    {
        public Transform anchor;       // середина габаритной линии
        public RectTransform marker;   // подпись на экране
    }

    [Header("Сцена")]
    public ARPlacementController placement;
    public CarController car;
    public EngineSound engine;
    public DriveGame drive;
    public Camera arCamera;
    public Canvas canvas;

    [Header("Стартовый экран")]
    public GameObject startPanel;
    public Button startButton;

    [Header("Подсказки")]
    public GameObject hintBox;
    public Text hintText;
    public Text scaleText;
    public GameObject toastBox;
    public Text toastText;

    [Header("Верхние кнопки")]
    public GameObject topBar;
    public Button photoButton;
    public Button resetButton;
    public Button scaleButton;
    public Text scaleButtonLabel;

    [Header("Нижняя панель")]
    public GameObject bottomBar;
    public Button colorButton, finishButton, rimsButton, lightsButton;
    public Button demoButton, xrayButton, interiorButton, infoButton;
    public Button trimButton, engineButton, showButton, dimsButton;

    [Header("Выбор цвета, покрытия, дисков")]
    public GameObject colorPanel;
    public Button[] colorSwatches;
    public Color[] colors;
    public GameObject finishPanel;
    public Button[] finishButtons;
    public GameObject rimsPanel;
    public Button[] rimButtons;

    [Header("Отделка: кожа салона и суппорты")]
    public GameObject trimPanel;
    public Button[] leatherSwatches;
    public Color[] leatherColors;
    public Button[] caliperSwatches;
    public Color[] caliperColors;

    [Header("Габариты")]
    public DimensionLabel[] dimensionLabels;

    [Header("Салон")]
    public Button exitInteriorButton;

    [Header("Информационные точки")]
    public Hotspot[] hotspots;
    public GameObject infoCard;
    public Text infoTitle;
    public Text infoBody;
    public Button infoCloseButton;

    [Header("Цвета кнопок")]
    public Color normalColor = new Color(1f, 1f, 1f, 0.14f);
    public Color activeColor = new Color(0.90f, 0.16f, 0.18f, 0.95f);

    bool infoMode;
    float hintTimer;
    Coroutine toastRoutine;

    void Start()
    {
        startButton.onClick.AddListener(OnStart);
        photoButton.onClick.AddListener(() => StartCoroutine(TakePhoto()));
        resetButton.onClick.AddListener(OnReset);
        scaleButton.onClick.AddListener(OnScaleButton);

        colorButton.onClick.AddListener(() => TogglePanel(colorPanel));
        finishButton.onClick.AddListener(() => TogglePanel(finishPanel));
        rimsButton.onClick.AddListener(() => TogglePanel(rimsPanel));
        trimButton.onClick.AddListener(() => TogglePanel(trimPanel));
        engineButton.onClick.AddListener(() => { engine.SetRunning(!engine.Running); Refresh(); });
        showButton.onClick.AddListener(() => { placement.SetShowMode(!placement.ShowMode); Refresh(); });
        dimsButton.onClick.AddListener(() => { car.SetDimensions(!car.DimensionsOn); Refresh(); });
        for (int i = 0; i < leatherSwatches.Length; i++)
        {
            int index = i;
            leatherSwatches[i].onClick.AddListener(() => car.SetLeatherColor(leatherColors[index]));
        }
        for (int i = 0; i < caliperSwatches.Length; i++)
        {
            int index = i;
            caliperSwatches[i].onClick.AddListener(() => car.SetCaliperColor(caliperColors[index]));
        }
        lightsButton.onClick.AddListener(() => { car.SetLights(!car.LightsOn); Refresh(); });
        demoButton.onClick.AddListener(() => drive.Begin()); // кнопка "Вождение"
        xrayButton.onClick.AddListener(() => { car.SetXray(!car.XrayOn); Refresh(); });
        infoButton.onClick.AddListener(() => { infoMode = !infoMode; if (!infoMode) infoCard.SetActive(false); Refresh(); });
        interiorButton.onClick.AddListener(OnEnterInterior);
        exitInteriorButton.onClick.AddListener(() => placement.ExitInterior());
        infoCloseButton.onClick.AddListener(() => infoCard.SetActive(false));

        for (int i = 0; i < colorSwatches.Length; i++)
        {
            int index = i;
            colorSwatches[i].onClick.AddListener(() => { car.SetBodyColor(colors[index]); Refresh(); });
        }
        for (int i = 0; i < finishButtons.Length; i++)
        {
            int index = i;
            finishButtons[i].onClick.AddListener(() => { car.SetFinish((CarController.Finish)index); Refresh(); });
        }
        for (int i = 0; i < rimButtons.Length; i++)
        {
            int index = i;
            rimButtons[i].onClick.AddListener(() => { car.SetRimStyle((CarController.RimStyle)index); Refresh(); });
        }
        foreach (var hotspot in hotspots)
        {
            var h = hotspot;
            h.button.onClick.AddListener(() => ShowInfo(h));
        }

        placement.StateChanged += OnStateChanged;
        placement.ScaleChanged += UpdateScaleLabel;

        startPanel.SetActive(true);
        toastBox.SetActive(false);
        infoCard.SetActive(false);
        OnStateChanged();
    }

    // ---------- Кнопки ----------

    void OnStart()
    {
        startPanel.SetActive(false);
        placement.BeginScanning();
    }

    void OnReset()
    {
        infoMode = false;
        infoCard.SetActive(false);
        CloseSubPanels();
        engine.SetRunning(false);
        car.SetDimensions(false);
        car.SetDemo(false);
        placement.ResetPlacement();
    }

    void OnScaleButton()
    {
        if (placement.CarScale > 0.95f) placement.SetDefaultScale();
        else placement.SetRealScale();
    }

    void OnEnterInterior()
    {
        CloseSubPanels();
        infoCard.SetActive(false);
        if (car.XrayOn) car.SetXray(false);
        car.SetDimensions(false);
        placement.SetShowMode(false);
        placement.EnterInterior();
    }

    // Вызывается мини-игрой: прячем конфигуратор, пока идёт вождение.
    public void SetDriving(bool on)
    {
        CloseSubPanels();
        infoMode = false;
        infoCard.SetActive(false);
        car.SetDimensions(false);
        bool placed = placement.Current == ARPlacementController.State.Placed;
        bottomBar.SetActive(!on && placed);
        resetButton.gameObject.SetActive(!on && placed);
        scaleButton.gameObject.SetActive(!on && placed);
        scaleText.transform.parent.gameObject.SetActive(!on && placed);
        if (on) SetHint("Держите «Газ» и рулите стрелками.\nСоберите все монеты на время!", 6f);
        else hintBox.SetActive(false);
        Refresh();
    }

    void TogglePanel(GameObject panel)
    {
        bool open = !panel.activeSelf;
        CloseSubPanels();
        panel.SetActive(open);
        Refresh();
    }

    void CloseSubPanels()
    {
        colorPanel.SetActive(false);
        finishPanel.SetActive(false);
        rimsPanel.SetActive(false);
        trimPanel.SetActive(false);
    }

    void ShowInfo(Hotspot hotspot)
    {
        CloseSubPanels();
        infoTitle.text = hotspot.title;
        infoBody.text = hotspot.body;
        infoCard.SetActive(true);
        Refresh();
    }

    // ---------- Состояние ----------

    void OnStateChanged()
    {
        var state = placement.Current;
        bool placed = state == ARPlacementController.State.Placed;
        bool interior = state == ARPlacementController.State.Interior;

        bottomBar.SetActive(placed);
        topBar.SetActive(placed || state == ARPlacementController.State.Scanning || state == ARPlacementController.State.ReadyToPlace);
        photoButton.gameObject.SetActive(placed || interior);
        scaleButton.gameObject.SetActive(placed);
        resetButton.gameObject.SetActive(placed);
        scaleText.transform.parent.gameObject.SetActive(placed);
        exitInteriorButton.gameObject.SetActive(interior);
        if (interior) topBar.SetActive(true);
        if (!placed) { CloseSubPanels(); infoCard.SetActive(false); }

        switch (state)
        {
            case ARPlacementController.State.Scanning:
                SetHint("Медленно поводите телефоном, наведя камеру на пол", 0f);
                break;
            case ARPlacementController.State.ReadyToPlace:
                SetHint("Коснитесь экрана, чтобы поставить автомобиль", 0f);
                break;
            case ARPlacementController.State.Placed:
                SetHint("Один палец — двигать\nДва пальца — вращать и менять размер", 7f);
                break;
            case ARPlacementController.State.Interior:
                SetHint("Вы в салоне. Осматривайтесь, поворачивая телефон", 6f);
                break;
            default:
                hintBox.SetActive(false);
                break;
        }
        Refresh();
        UpdateScaleLabel();
    }

    void SetHint(string text, float seconds)
    {
        hintText.text = text;
        hintBox.SetActive(true);
        hintTimer = seconds; // 0 = показывать, пока не сменится состояние
    }

    void UpdateScaleLabel()
    {
        float scale = placement.CarScale;
        scaleText.text = "Масштаб " + Mathf.RoundToInt(scale * 100f) + "%";
        scaleButtonLabel.text = scale > 0.95f ? "Мини" : "1 : 1";
    }

    void Refresh()
    {
        Tint(colorButton, colorPanel.activeSelf);
        Tint(finishButton, finishPanel.activeSelf);
        Tint(rimsButton, rimsPanel.activeSelf);
        Tint(trimButton, trimPanel.activeSelf);
        Tint(engineButton, engine.Running);
        Tint(showButton, placement.ShowMode);
        Tint(dimsButton, car.DimensionsOn);
        Tint(lightsButton, car.LightsOn);
        Tint(demoButton, car.DemoOn);
        Tint(xrayButton, car.XrayOn);
        Tint(infoButton, infoMode);
        Tint(interiorButton, false);
        for (int i = 0; i < finishButtons.Length; i++)
            Tint(finishButtons[i], (int)car.CurrentFinish == i);
        for (int i = 0; i < rimButtons.Length; i++)
            Tint(rimButtons[i], (int)car.CurrentRim == i);
    }

    void Tint(Button button, bool active)
    {
        if (button != null && button.targetGraphic != null)
            button.targetGraphic.color = active ? activeColor : normalColor;
    }

    void Update()
    {
        if (hintTimer > 0f)
        {
            hintTimer -= Time.deltaTime;
            if (hintTimer <= 0f) hintBox.SetActive(false);
        }
        UpdateHotspots();
    }

    void UpdateHotspots()
    {
        bool showDims = car.DimensionsOn && placement.Current == ARPlacementController.State.Placed;
        foreach (var label in dimensionLabels)
        {
            bool visible = showDims;
            if (visible)
            {
                Vector3 screen = arCamera.WorldToScreenPoint(label.anchor.position);
                visible = screen.z > 0.05f;
                if (visible) label.marker.position = new Vector3(screen.x, screen.y, 0f);
            }
            if (label.marker.gameObject.activeSelf != visible)
                label.marker.gameObject.SetActive(visible);
        }

        bool show = infoMode && placement.Current == ARPlacementController.State.Placed;
        foreach (var hotspot in hotspots)
        {
            bool visible = show;
            if (visible)
            {
                Vector3 screen = arCamera.WorldToScreenPoint(hotspot.anchor.position);
                visible = screen.z > 0.05f;
                if (visible) hotspot.marker.position = new Vector3(screen.x, screen.y, 0f);
            }
            if (hotspot.marker.gameObject.activeSelf != visible)
                hotspot.marker.gameObject.SetActive(visible);
        }
    }

    // ---------- Фото ----------

    IEnumerator TakePhoto()
    {
        canvas.enabled = false;
        yield return new WaitForEndOfFrame();
        Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
        canvas.enabled = true;

        byte[] jpg = shot.EncodeToJPG(92);
        Destroy(shot);
        string fileName = "ARCar_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg";

        string error = SaveToGallery(jpg, fileName);
        ShowToast(error == null ? "Фото сохранено в галерею (Pictures/ARCar)" : "Не удалось сохранить фото: " + error);
    }

    static string SaveToGallery(byte[] jpg, string fileName)
    {
        try
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var resolver = activity.Call<AndroidJavaObject>("getContentResolver"))
            using (var values = new AndroidJavaObject("android.content.ContentValues"))
            using (var media = new AndroidJavaClass("android.provider.MediaStore$Images$Media"))
            using (var collection = media.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI"))
            {
                values.Call("put", "_display_name", fileName);
                values.Call("put", "mime_type", "image/jpeg");
                values.Call("put", "relative_path", "Pictures/ARCar");
                using (var item = resolver.Call<AndroidJavaObject>("insert", collection, values))
                {
                    if (item == null) return "нет доступа к галерее";
                    using (var stream = resolver.Call<AndroidJavaObject>("openOutputStream", item))
                    {
                        var signed = new sbyte[jpg.Length];
                        Buffer.BlockCopy(jpg, 0, signed, 0, jpg.Length);
                        stream.Call("write", signed);
                        stream.Call("flush");
                        stream.Call("close");
                    }
                }
            }
#else
            File.WriteAllBytes(Path.Combine(Application.persistentDataPath, fileName), jpg);
#endif
            return null;
        }
        catch (Exception e)
        {
            Debug.LogError("SaveToGallery: " + e);
            return e.Message;
        }
    }

    void ShowToast(string text)
    {
        if (toastRoutine != null) StopCoroutine(toastRoutine);
        toastRoutine = StartCoroutine(ToastRoutine(text));
    }

    IEnumerator ToastRoutine(string text)
    {
        toastText.text = text;
        toastBox.SetActive(true);
        yield return new WaitForSeconds(3f);
        toastBox.SetActive(false);
    }
}
