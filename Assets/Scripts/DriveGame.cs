using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Мини-игра "Вождение": машиной управляют кнопками на экране,
/// нужно собрать все монеты на полу за минимальное время.
/// </summary>
public class DriveGame : MonoBehaviour
{
    [Header("Сцена")]
    public ARPlacementController placement;
    public CarController car;
    public EngineSound engine;
    public AppUI ui;
    public GameObject coinPrefab;

    [Header("Интерфейс")]
    public GameObject hud;
    public Text scoreText;
    public HoldButton leftButton, rightButton, gasButton, brakeButton;
    public Button exitButton;

    [Header("Параметры (в метрах реального автомобиля)")]
    public int coinCount = 8;
    public float maxSpeed = 7f;
    public float maxReverse = 3f;
    public float acceleration = 5f;
    public float braking = 10f;
    public float drag = 2.5f;
    public float maxSteer = 32f;
    public float wheelBase = 2.65f;
    public float wheelRadius = 0.36f;

    public bool Active { get; private set; }

    readonly List<Transform> coins = new List<Transform>();
    Transform coinRoot;
    float speed, steer, timer, respawnTimer;
    int collected;
    bool timing, finished, engineWasRunning;

    void Start()
    {
        exitButton.onClick.AddListener(End);
        hud.SetActive(false);
    }

    public void Begin()
    {
        if (Active || placement.Current != ARPlacementController.State.Placed) return;
        Active = true;
        speed = steer = 0f;
        placement.GesturesEnabled = false;
        placement.SetShowMode(false);
        ui.SetDriving(true);
        hud.SetActive(true);

        engineWasRunning = engine.Running;
        engine.SetRunning(true);
        car.ManualDrive = true;
        SpawnCoins();
    }

    public void End()
    {
        if (!Active) return;
        Active = false;
        car.ManualDrive = false;
        car.manualSpinSpeed = 0f;
        car.manualSteer = 0f;
        engine.throttle = -1f;
        if (!engineWasRunning) engine.SetRunning(false);
        ClearCoins();
        hud.SetActive(false);
        placement.GesturesEnabled = true;
        ui.SetDriving(false);
    }

    void SpawnCoins()
    {
        ClearCoins();
        float scale = placement.CarScale;
        coinRoot = new GameObject("Coins").transform;
        coinRoot.position = car.transform.position;
        coinRoot.localScale = Vector3.one * scale;

        float offset = Random.Range(0f, 360f);
        for (int i = 0; i < coinCount; i++)
        {
            float angle = (offset + i * 360f / coinCount + Random.Range(-16f, 16f)) * Mathf.Deg2Rad;
            float radius = Random.Range(4.5f, 10.5f);
            var coin = Instantiate(coinPrefab, coinRoot).transform;
            coin.localPosition = new Vector3(Mathf.Cos(angle) * radius, 0.6f, Mathf.Sin(angle) * radius);
            coin.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            coins.Add(coin);
        }
        collected = 0;
        timer = 0f;
        timing = false;
        finished = false;
        UpdateScore();
    }

    void ClearCoins()
    {
        coins.Clear();
        if (coinRoot != null) Destroy(coinRoot.gameObject);
        coinRoot = null;
    }

    void Update()
    {
        if (!Active) return;
        // Если машину убрали (кнопка "Заново") — выходим из игры.
        if (placement.Current != ARPlacementController.State.Placed) { End(); return; }

        float dt = Time.deltaTime;
        float steerInput = (rightButton.Held ? 1f : 0f) - (leftButton.Held ? 1f : 0f);

        if (gasButton.Held)
            speed = Mathf.MoveTowards(speed, maxSpeed, (speed < 0f ? braking : acceleration) * dt);
        else if (brakeButton.Held)
            speed = Mathf.MoveTowards(speed, -maxReverse, (speed > 0f ? braking : acceleration * 0.7f) * dt);
        else
            speed = Mathf.MoveTowards(speed, 0f, drag * dt);

        steer = Mathf.MoveTowards(steer, steerInput * maxSteer, 110f * dt);

        // Простая "велосипедная" модель: поворот зависит от скорости и угла колёс.
        float scale = placement.CarScale;
        float yawRate = speed / wheelBase * Mathf.Tan(steer * Mathf.Deg2Rad) * Mathf.Rad2Deg;
        car.transform.Rotate(0f, yawRate * dt, 0f, Space.World);
        car.transform.position += car.transform.forward * (speed * scale * dt);

        car.manualSpinSpeed = speed / wheelRadius * Mathf.Rad2Deg;
        car.manualSteer = steer;
        engine.throttle = Mathf.Clamp01(Mathf.Abs(speed) / maxSpeed);

        if (!finished)
        {
            if (!timing && Mathf.Abs(speed) > 0.05f) timing = true;
            if (timing) timer += dt;
            CollectCoins(scale);
            UpdateScore();
        }
        else
        {
            respawnTimer -= dt;
            if (respawnTimer <= 0f) SpawnCoins();
        }

        foreach (var coin in coins)
            if (coin != null && coin.gameObject.activeSelf)
                coin.Rotate(0f, 150f * dt, 0f, Space.World);
    }

    void CollectCoins(float scale)
    {
        Vector3 carPos = car.transform.position;
        float reach = 1.9f * scale;
        foreach (var coin in coins)
        {
            if (coin == null || !coin.gameObject.activeSelf) continue;
            Vector3 d = coin.position - carPos;
            d.y = 0f;
            if (d.magnitude < reach)
            {
                coin.gameObject.SetActive(false);
                collected++;
            }
        }

        if (collected >= coins.Count && coins.Count > 0)
        {
            finished = true;
            respawnTimer = 4f;
            float best = PlayerPrefs.GetFloat("DriveBest", 0f);
            bool record = best <= 0f || timer < best;
            if (record)
            {
                PlayerPrefs.SetFloat("DriveBest", timer);
                PlayerPrefs.Save();
            }
            scoreText.text = "Все монеты собраны за " + timer.ToString("0.0") + " с" + (record ? "\nНовый рекорд!" : "\nРекорд: " + best.ToString("0.0") + " с");
#if UNITY_ANDROID && !UNITY_EDITOR
            Handheld.Vibrate();
#endif
        }
    }

    void UpdateScore()
    {
        float best = PlayerPrefs.GetFloat("DriveBest", 0f);
        scoreText.text = "Монеты " + collected + " / " + coins.Count + "     Время " + timer.ToString("0.0") + " с" +
                         (best > 0f ? "\nРекорд: " + best.ToString("0.0") + " с" : "\nСоберите все монеты");
    }
}
