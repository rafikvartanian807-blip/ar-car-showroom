using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

/// <summary>
/// Поиск пола, прицел, установка автомобиля и жесты:
/// один палец — перемещение, два пальца — поворот и масштаб.
/// Также отвечает за режим "вид из салона".
/// </summary>
public class ARPlacementController : MonoBehaviour
{
    public enum State { Idle, Scanning, ReadyToPlace, Placed, Interior }

    public ARRaycastManager raycastManager;
    public ARPlaneManager planeManager;
    public Camera arCamera;
    public CarController car;
    public GameObject reticle;

    [Tooltip("Масштаб автомобиля сразу после установки (1 = реальный размер).")]
    public float defaultScale = 0.25f;
    public float minScale = 0.05f;
    public float maxScale = 1.0f;

    [Tooltip("Скорость вращения в режиме показа, градусов в секунду.")]
    public float showSpeed = 28f;
    public bool ShowMode { get; private set; }
    [HideInInspector] public bool GesturesEnabled = true; // выключаются на время игры

    public State Current { get; private set; } = State.Idle;
    public float CarScale => car.transform.localScale.x;
    public event Action StateChanged;
    public event Action ScaleChanged;

    static readonly List<ARRaycastHit> hits = new List<ARRaycastHit>();
    static readonly List<RaycastResult> uiHits = new List<RaycastResult>();

    Pose reticlePose;
    bool reticleValid;

    // жесты
    bool dragging;
    Vector3 dragOffset;
    bool twoFinger;
    float startDistance, startAngle, startScale, startYaw;
    bool gestureBlockedByUI;

    // анимация появления
    float appearTimer = -1f;
    const float AppearDuration = 0.55f;

    // сохранённое положение на время вида из салона
    Vector3 savedPosition;
    Quaternion savedRotation;
    float savedScale;

    void OnEnable()
    {
        EnhancedTouchSupport.Enable();
    }

    void OnDisable()
    {
        EnhancedTouchSupport.Disable();
    }

    void Start()
    {
        car.gameObject.SetActive(false);
        reticle.SetActive(false);
        SetPlanesVisible(false);
    }

    void SetState(State state)
    {
        Current = state;
        StateChanged?.Invoke();
    }

    // ---------- Команды из интерфейса ----------

    public void BeginScanning()
    {
        car.gameObject.SetActive(false);
        dragging = twoFinger = false;
        SetPlanesVisible(true);
        SetState(State.Scanning);
    }

    public void SetShowMode(bool on)
    {
        ShowMode = on;
    }

    public void ResetPlacement()
    {
        ShowMode = false;
        if (Current == State.Interior) ExitInterior();
        BeginScanning();
    }

    public void SetRealScale()
    {
        SetScale(1f);
    }

    public void SetDefaultScale()
    {
        SetScale(defaultScale);
    }

    void SetScale(float scale)
    {
        car.transform.localScale = Vector3.one * Mathf.Clamp(scale, minScale, maxScale);
        ScaleChanged?.Invoke();
    }

    public void EnterInterior()
    {
        if (Current != State.Placed || appearTimer >= 0f) return;
        savedPosition = car.transform.position;
        savedRotation = car.transform.rotation;
        savedScale = CarScale;

        // Ставим машину в реальном размере так, чтобы место водителя совпало с телефоном.
        Vector3 forward = arCamera.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        Quaternion rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);

        car.transform.localScale = Vector3.one;
        car.transform.rotation = rotation;
        Vector3 eyeLocal = car.transform.InverseTransformPoint(car.eyeAnchor.position);
        car.transform.position = arCamera.transform.position - rotation * eyeLocal;

        car.SetShadowVisible(false);
        dragging = twoFinger = false;
        SetState(State.Interior);
        ScaleChanged?.Invoke();
    }

    public void ExitInterior()
    {
        if (Current != State.Interior) return;
        car.transform.position = savedPosition;
        car.transform.rotation = savedRotation;
        car.transform.localScale = Vector3.one * savedScale;
        car.SetShadowVisible(true);
        SetState(State.Placed);
        ScaleChanged?.Invoke();
    }

    // ---------- Основной цикл ----------

    void Update()
    {
        switch (Current)
        {
            case State.Scanning:
            case State.ReadyToPlace:
                UpdateReticle();
                HandlePlacementTap();
                break;
            case State.Placed:
                if (appearTimer >= 0f) { UpdateAppear(); break; }
                if (!GesturesEnabled) break;
                HandleGestures();
                if (ShowMode && Touch.activeTouches.Count == 0)
                    car.transform.Rotate(0f, showSpeed * Time.deltaTime, 0f, Space.World);
                break;
        }
    }

    void UpdateReticle()
    {
        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        reticleValid = raycastManager.Raycast(center, hits, TrackableType.PlaneWithinPolygon);
        if (reticleValid)
        {
            reticlePose = hits[0].pose;
            reticle.transform.SetPositionAndRotation(reticlePose.position, reticlePose.rotation);
        }
        reticle.SetActive(reticleValid);

        if (reticleValid && Current == State.Scanning) SetState(State.ReadyToPlace);
        else if (!reticleValid && Current == State.ReadyToPlace) SetState(State.Scanning);
    }

    void HandlePlacementTap()
    {
        if (!reticleValid) return;
        foreach (var touch in Touch.activeTouches)
        {
            if (touch.phase != TouchPhase.Began) continue;
            if (IsOverUI(touch.screenPosition)) continue;
            PlaceCar();
            break;
        }
    }

    void PlaceCar()
    {
        // Машина смотрит "лицом" к пользователю.
        Vector3 toCamera = arCamera.transform.position - reticlePose.position;
        toCamera.y = 0f;
        if (toCamera.sqrMagnitude < 0.001f) toCamera = Vector3.forward;

        car.transform.SetPositionAndRotation(reticlePose.position, Quaternion.LookRotation(toCamera.normalized, Vector3.up));
        car.transform.localScale = Vector3.one * defaultScale * 0.02f;
        car.gameObject.SetActive(true);
        car.SetShadowVisible(true);
        appearTimer = 0f;
#if UNITY_ANDROID && !UNITY_EDITOR
        Handheld.Vibrate();
#endif

        reticle.SetActive(false);
        SetPlanesVisible(false);
        SetState(State.Placed);
        ScaleChanged?.Invoke();
    }

    // Машина "вырастает" из точки с лёгким перелётом.
    void UpdateAppear()
    {
        appearTimer += Time.deltaTime;
        float t = Mathf.Clamp01(appearTimer / AppearDuration);
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float eased = 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
        car.transform.localScale = Vector3.one * defaultScale * Mathf.Max(eased, 0.02f);
        if (t >= 1f)
        {
            appearTimer = -1f;
            car.transform.localScale = Vector3.one * defaultScale;
            ScaleChanged?.Invoke();
        }
    }

    void HandleGestures()
    {
        var touches = Touch.activeTouches;
        if (touches.Count == 0)
        {
            dragging = twoFinger = gestureBlockedByUI = false;
            return;
        }

        // Жест, начатый на кнопке интерфейса, игнорируем до отпускания пальцев.
        foreach (var touch in touches)
            if (touch.phase == TouchPhase.Began && IsOverUI(touch.screenPosition))
                gestureBlockedByUI = true;
        if (gestureBlockedByUI) return;

        if (touches.Count >= 2)
        {
            dragging = false;
            Vector2 a = touches[0].screenPosition, b = touches[1].screenPosition;
            float distance = Vector2.Distance(a, b);
            float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;

            if (!twoFinger)
            {
                twoFinger = true;
                startDistance = Mathf.Max(distance, 1f);
                startAngle = angle;
                startScale = CarScale;
                startYaw = car.transform.eulerAngles.y;
            }
            else
            {
                SetScale(startScale * distance / startDistance);
                float delta = Mathf.DeltaAngle(startAngle, angle);
                car.transform.rotation = Quaternion.Euler(0f, startYaw - delta, 0f);
            }
            return;
        }

        twoFinger = false;
        var finger = touches[0];
        if (!raycastManager.Raycast(finger.screenPosition, hits, TrackableType.PlaneWithinPolygon))
            return;
        Vector3 point = hits[0].pose.position;

        if (finger.phase == TouchPhase.Began || !dragging)
        {
            dragging = true;
            dragOffset = car.transform.position - point;
            // Перетаскивать можно, только если палец рядом с машиной.
            if (dragOffset.magnitude > 3.0f * CarScale + 0.3f) dragging = false;
        }
        else if (finger.phase == TouchPhase.Moved || finger.phase == TouchPhase.Stationary)
        {
            car.transform.position = point + dragOffset;
        }
    }

    static bool IsOverUI(Vector2 screenPosition)
    {
        if (EventSystem.current == null) return false;
        var data = new PointerEventData(EventSystem.current) { position = screenPosition };
        uiHits.Clear();
        EventSystem.current.RaycastAll(data, uiHits);
        return uiHits.Count > 0;
    }

    void SetPlanesVisible(bool visible)
    {
        if (planeManager == null) return;
        foreach (var plane in planeManager.trackables)
        {
            var meshRenderer = plane.GetComponent<MeshRenderer>();
            if (meshRenderer != null) meshRenderer.enabled = visible;
        }
        planesVisible = visible;
    }

    bool planesVisible;

    void LateUpdate()
    {
        // Новые плоскости появляются постоянно — поддерживаем нужную видимость.
        if (planeManager == null) return;
        foreach (var plane in planeManager.trackables)
        {
            var meshRenderer = plane.GetComponent<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.enabled != planesVisible)
                meshRenderer.enabled = planesVisible;
        }
    }
}
