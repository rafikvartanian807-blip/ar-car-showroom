using UnityEngine;

/// <summary>
/// Звук двигателя, синтезированный кодом (без аудиофайлов): сумма гармоник
/// с "пульсацией" от вспышек в цилиндрах. Высота тона растёт, когда крутятся колёса.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class EngineSound : MonoBehaviour
{
    public CarController car;
    public float idlePitch = 0.85f;
    public float revPitch = 1.65f;
    public float volume = 0.75f;

    [HideInInspector] public float throttle = -1f; // 0..1 из мини-игры; -1 = не используется

    public bool Running { get; private set; }

    AudioSource source;
    float startTimer;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.clip = CreateClip();
        source.loop = true;
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        source.volume = 0f;
    }

    void OnDisable()
    {
        // Машину убрали со сцены — двигатель глушим.
        Running = false;
        if (source != null) { source.Stop(); source.volume = 0f; }
    }

    public void SetRunning(bool on)
    {
        if (on == Running) return;
        Running = on;
        if (on)
        {
            startTimer = 0f;
            source.pitch = 0.45f;
            if (!source.isPlaying) source.Play();
        }
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (Running)
        {
            startTimer += dt;
            // Запуск: короткая "перегазовка", затем холостые.
            float blip = Mathf.Exp(-Mathf.Pow((startTimer - 0.55f) / 0.25f, 2f)) * 0.7f;
            float target = (car != null && car.DemoOn ? revPitch : idlePitch) + blip;
            if (throttle >= 0f) target = Mathf.Lerp(idlePitch, revPitch * 1.15f, throttle) + blip;
            source.pitch = Mathf.Lerp(source.pitch, target, 1f - Mathf.Exp(-3.5f * dt));
            source.volume = Mathf.MoveTowards(source.volume, volume, 2.5f * dt);
        }
        else if (source.isPlaying)
        {
            source.pitch = Mathf.Lerp(source.pitch, 0.35f, 1f - Mathf.Exp(-3f * dt));
            source.volume = Mathf.MoveTowards(source.volume, 0f, 1.2f * dt);
            if (source.volume <= 0.001f) source.Stop();
        }
    }

    static AudioClip CreateClip()
    {
        const int rate = 44100;
        const int seconds = 2;
        const float f0 = 27.5f; // целое число периодов за 2 секунды — петля без щелчка
        int count = rate * seconds;
        var data = new float[count];
        var random = new System.Random(458);

        // Гармоники: чётные (частота вспышек V8) звучат сильнее.
        float[] amp = { 0.30f, 1.00f, 0.35f, 0.85f, 0.25f, 0.55f, 0.18f, 0.40f, 0.12f, 0.26f, 0.08f, 0.18f, 0.05f, 0.12f };
        var phase = new float[amp.Length];
        for (int k = 0; k < phase.Length; k++) phase[k] = (float)(random.NextDouble() * Mathf.PI * 2f);

        // Шум делаем периодическим: "хвост" плавно переходит в начало петли.
        const int fade = 4000;
        var noiseBuf = new float[count + fade];
        float filtered = 0f;
        for (int i = 0; i < noiseBuf.Length; i++)
        {
            filtered = Mathf.Lerp(filtered, (float)(random.NextDouble() * 2.0 - 1.0), 0.12f);
            noiseBuf[i] = filtered;
        }

        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / rate;
            float s = 0f;
            for (int k = 0; k < amp.Length; k++)
                s += amp[k] * Mathf.Sin(2f * Mathf.PI * f0 * (k + 1) * t + phase[k]);

            // Шум выхлопа, пульсирующий с частотой вспышек.
            float noise = i < fade ? Mathf.Lerp(noiseBuf[count + i], noiseBuf[i], (float)i / fade) : noiseBuf[i];
            float pulse = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * f0 * 4f * t);
            s += noise * 2.2f * pulse * pulse;

            data[i] = s;
            peak = Mathf.Max(peak, Mathf.Abs(s));
        }

        // Нормализация с мягким ограничением.
        for (int i = 0; i < count; i++)
            data[i] = (float)System.Math.Tanh(data[i] / peak * 2.2f) * 0.8f;

        var clip = AudioClip.Create("Engine", count, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
