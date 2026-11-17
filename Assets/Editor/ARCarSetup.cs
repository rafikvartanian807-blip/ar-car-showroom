using System;
using System.Collections.Generic;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;

/// <summary>
/// Собирает весь проект "AR Автосалон" с нуля: настройки Android и ARCore,
/// материалы, префаб автомобиля из модели ferrari.glb, сцену и интерфейс.
/// Меню: AR Car → Пересобрать сцену.
/// </summary>
public static class ARCarSetup
{
    const string ScenePath = "Assets/Scenes/ARCar.unity";
    const string ModelPath = "Assets/Models/ferrari.glb";
    const string CarPrefabPath = "Assets/Prefabs/Car.prefab";
    const string Tag = "[ARCarSetup] ";

    static Font font;
    static Sprite roundedSprite, circleSprite;

    static readonly Color Accent = new Color(0.90f, 0.16f, 0.18f, 0.95f);
    static readonly Color PanelColor = new Color(0.06f, 0.07f, 0.09f, 0.82f);
    static readonly Color ButtonColor = new Color(1f, 1f, 1f, 0.14f);

    [MenuItem("AR Car/Пересобрать сцену")]
    public static void Build()
    {
        try
        {
            ConfigurePlayer();
            ConfigureXR();
            foreach (var dir in new[] { "Assets/Textures", "Assets/Materials", "Assets/Prefabs", "Assets/Scenes" })
                Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();

            CreateSprites();
            var mats = CreateMaterials();
            var carPrefab = CreateCarPrefab(mats);
            var planePrefab = CreatePlanePrefab(mats["Plane"]);
            CreateScene(carPrefab, planePrefab, mats);
            AssetDatabase.SaveAssets();
            Debug.Log(Tag + "DONE");
        }
        catch (Exception e)
        {
            Debug.LogError(Tag + "FAILED: " + e);
        }
    }

    public static void OpenScene()
    {
        EditorSceneManager.OpenScene(ScenePath);
    }

    public static void BuildApk()
    {
        Directory.CreateDirectory("Builds");
        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = "Builds/ARCar.apk",
            target = BuildTarget.Android,
            options = BuildOptions.None
        };
        var report = BuildPipeline.BuildPlayer(options);
        Debug.Log(Tag + "APK result=" + report.summary.result + ", errors=" + report.summary.totalErrors);
    }

    // =====================================================================
    // Настройки проекта
    // =====================================================================

    static void ConfigurePlayer()
    {
        PlayerSettings.companyName = "ARCourse";
        PlayerSettings.productName = "AR Car";
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.arcourse.arcar");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
        PlayerSettings.colorSpace = ColorSpace.Linear;

        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
        if (assets != null && assets.Length > 0)
        {
            var so = new SerializedObject(assets[0]);
            var prop = so.FindProperty("activeInputHandler");
            if (prop != null && prop.intValue != 1)
            {
                prop.intValue = 1; // Input System Package (New)
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            Debug.Log(Tag + "activeInputHandler=" + (prop != null ? prop.intValue.ToString() : "not found"));
        }
    }

    static void ConfigureXR()
    {
        const string key = "com.unity.xr.management.loader_settings";
        XRGeneralSettingsPerBuildTarget perTarget;
        EditorBuildSettings.TryGetConfigObject(key, out perTarget);
        if (perTarget == null)
        {
            Directory.CreateDirectory("Assets/XR");
            AssetDatabase.Refresh();
            const string path = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
            perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(path);
            if (perTarget == null)
            {
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, path);
            }
            EditorBuildSettings.AddConfigObject(key, perTarget, true);
        }

        const BuildTargetGroup android = BuildTargetGroup.Android;
        if (!perTarget.HasSettingsForBuildTarget(android))
            perTarget.CreateDefaultSettingsForBuildTarget(android);
        if (!perTarget.HasManagerSettingsForBuildTarget(android))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(android);

        var general = perTarget.SettingsForBuildTarget(android);
        general.InitManagerOnStart = true;
        XRManagerSettings manager = general.Manager;

        bool hasLoader = false;
        foreach (var l in manager.activeLoaders)
            if (l != null && l.GetType().FullName == "UnityEngine.XR.ARCore.ARCoreLoader")
                hasLoader = true;

        bool ok = hasLoader || XRPackageMetadataStore.AssignLoader(manager, "UnityEngine.XR.ARCore.ARCoreLoader", android);
        EditorUtility.SetDirty(general);
        EditorUtility.SetDirty(manager);
        EditorUtility.SetDirty(perTarget);
        AssetDatabase.SaveAssets();
        Debug.Log(Tag + "ARCore loader assigned=" + ok + ", loaders=" + manager.activeLoaders.Count);
        if (!ok) throw new Exception("Не удалось включить ARCore в XR Plug-in Management");
    }

    // =====================================================================
    // Текстуры и спрайты (рисуются кодом)
    // =====================================================================

    static float SS(float e0, float e1, float x)
    {
        float t = Mathf.Clamp01((x - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }

    static string SavePng(string path, int size, Func<float, float, Color> pixel)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = pixel((x + 0.5f) / size, (y + 0.5f) / size);
        tex.SetPixels(pixels);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        return path;
    }

    static Sprite ImportSprite(string path, int border)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.spriteBorder = new Vector4(border, border, border, border);
        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        importer.SetTextureSettings(settings);
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    static Texture2D ImportTexture(string path)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Default;
        importer.alphaIsTransparency = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static void CreateSprites()
    {
        // Скруглённый прямоугольник 64x64, радиус 22 пикселя.
        const float r = 22f / 64f;
        roundedSprite = ImportSprite(SavePng("Assets/Textures/UIRounded.png", 64, (u, v) =>
        {
            float dx = Mathf.Max(Mathf.Abs(u - 0.5f) - (0.5f - r), 0f);
            float dy = Mathf.Max(Mathf.Abs(v - 0.5f) - (0.5f - r), 0f);
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            return new Color(1, 1, 1, SS(r, r - 0.03f, d));
        }), 24);

        circleSprite = ImportSprite(SavePng("Assets/Textures/UICircle.png", 128, (u, v) =>
        {
            float d = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f));
            return new Color(1, 1, 1, SS(0.5f, 0.48f, d));
        }), 0);

        // Прицел: кольцо с точкой в центре.
        SavePng("Assets/Textures/Reticle.png", 256, (u, v) =>
        {
            float d = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f));
            float ring = SS(0.36f, 0.39f, d) * SS(0.48f, 0.45f, d);
            float dot = SS(0.07f, 0.05f, d);
            return new Color(1, 1, 1, Mathf.Max(ring, dot));
        });

        // Мягкая тень-пятно под машиной.
        SavePng("Assets/Textures/ShadowBlob.png", 256, (u, v) =>
        {
            float dx = Mathf.Max(Mathf.Abs(u - 0.5f) - 0.22f, 0f) / 0.28f;
            float dy = Mathf.Max(Mathf.Abs(v - 0.5f) - 0.30f, 0f) / 0.20f;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = SS(1f, 0f, d);
            return new Color(0, 0, 0, a * a);
        });

        // Пятно света от фар.
        SavePng("Assets/Textures/Glow.png", 256, (u, v) =>
        {
            float dx = (u - 0.5f) / 0.5f;
            float dy = (v - 0.15f) / 0.85f; // ярче у машины, гаснет вдаль
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = SS(1f, 0f, d);
            a *= SS(0f, 0.12f, v);
            return new Color(1, 1, 1, a * a);
        });
    }

    // =====================================================================
    // Материалы
    // =====================================================================

    static Material Mat(string name, Shader shader)
    {
        string path = "Assets/Materials/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Цвета в таблице заданы в линейном пространстве (как в glTF) — переводим в sRGB.
    static Material Pbr(string name, float r, float g, float b, float metallic, float smoothness)
    {
        var mat = Mat(name, Shader.Find("Standard"));
        mat.color = new Color(r, g, b, 1f).gamma;
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Glossiness", smoothness);
        return mat;
    }

    static void MakeTransparent(Material mat)
    {
        mat.SetFloat("_Mode", 3f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.SetInt("_SrcBlend", (int)BlendMode.One);
        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.DisableKeyword("_ALPHABLEND_ON");
        mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = (int)RenderQueue.Transparent;
    }

    static void MakeEmissive(Material mat, Color emission)
    {
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", emission);
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
    }

    static Material Overlay(string name, string shaderName, string texturePath, Color color)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null) throw new Exception("Шейдер не найден: " + shaderName);
        var mat = Mat(name, shader);
        mat.mainTexture = texturePath != null ? ImportTexture(texturePath) : null;
        mat.color = color;
        return mat;
    }

    static Dictionary<string, Material> CreateMaterials()
    {
        var m = new Dictionary<string, Material>();

        // Материалы модели (имена совпадают с именами материалов в ferrari.glb).
        m["Leather_red"] = Pbr("Leather_red", 0.239f, 0.094f, 0.094f, 0f, 0.35f);
        m["plastic_gray"] = Pbr("plastic_gray", 0.227f, 0.227f, 0.227f, 0f, 0.4f);
        m["metal_gray"] = Pbr("metal_gray", 0.50f, 0.50f, 0.50f, 0.9f, 0.7f);
        m["Turn_Signal_LED"] = Pbr("Turn_Signal_LED", 0.76f, 0.114f, 0.114f, 0f, 0.6f);
        m["Leather"] = Pbr("Leather", 0.157f, 0.157f, 0.169f, 0f, 0.35f);
        m["Interior_dark"] = Pbr("Interior_dark", 0.137f, 0.137f, 0.133f, 0f, 0.3f);
        m["Tires"] = Pbr("Tires", 0.039f, 0.039f, 0.039f, 0f, 0.25f);
        m["metal_chrome"] = Pbr("metal_chrome", 0.78f, 0.78f, 0.78f, 1f, 0.92f);
        m["Carpet"] = Pbr("Carpet", 0.02f, 0.02f, 0.022f, 0f, 0.1f);
        m["Carbon_Fiber"] = Pbr("Carbon_Fiber", 0.012f, 0.012f, 0.014f, 0.3f, 0.75f);
        m["Interior_light"] = Pbr("Interior_light", 0.5f, 0.5f, 0.5f, 0f, 0.4f);
        m["_0098_DodgerBlue"] = Pbr("DodgerBlue", 0.118f, 0.565f, 1f, 0f, 0.6f);
        m["Ferrari_Yellow"] = Pbr("Ferrari_Yellow", 0.973f, 0.8f, 0f, 0f, 0.7f);
        m["Brakes"] = Pbr("Brakes", 0.73f, 0.0f, 0.0f, 0.1f, 0.6f);

        var glass = Pbr("Glass", 0.02f, 0.025f, 0.03f, 0f, 0.96f);
        var gc = glass.color; gc.a = 0.38f; glass.color = gc;
        MakeTransparent(glass);
        m["Glass_Gray"] = glass;

        // Кузов: обычная краска и полупрозрачный "рентген".
        var body = Mat("Body", Shader.Find("Standard"));
        body.color = new Color(0.80f, 0.05f, 0.07f);
        body.SetFloat("_Metallic", 0.15f);
        body.SetFloat("_Glossiness", 0.93f);
        m["Body"] = body;

        var xray = Mat("BodyXray", Shader.Find("Standard"));
        xray.color = new Color(0.80f, 0.05f, 0.07f, 0.22f);
        xray.SetFloat("_Metallic", 0.1f);
        xray.SetFloat("_Glossiness", 0.9f);
        MakeTransparent(xray);
        m["BodyXray"] = xray;

        m["Rim"] = Pbr("Rim", 0.58f, 0.59f, 0.63f, 0.9f, 0.8f);

        // Фары: выключенные и включённые (светящиеся).
        m["HeadlightOff"] = Pbr("HeadlightOff", 0.55f, 0.55f, 0.58f, 0.6f, 0.95f);
        var headOn = Pbr("HeadlightOn", 1f, 1f, 1f, 0f, 0.9f);
        MakeEmissive(headOn, new Color(1f, 0.97f, 0.88f) * 3.0f);
        m["HeadlightOn"] = headOn;
        m["TaillightOff"] = Pbr("TaillightOff", 0.45f, 0.0f, 0.0f, 0.1f, 0.92f);
        var tailOn = Pbr("TaillightOn", 1f, 0.05f, 0.03f, 0f, 0.9f);
        MakeEmissive(tailOn, new Color(1f, 0.04f, 0.02f) * 2.5f);
        m["TaillightOn"] = tailOn;

        // Служебные.
        m["Shadow"] = Overlay("ShadowBlob", "ARCar/UnlitAlpha", "Assets/Textures/ShadowBlob.png", new Color(0, 0, 0, 0.7f));
        m["Glow"] = Overlay("HeadlightGlow", "ARCar/UnlitAdditive", "Assets/Textures/Glow.png", new Color(1f, 0.96f, 0.82f, 0.45f));
        m["Reticle"] = Overlay("Reticle", "ARCar/UnlitAlpha", "Assets/Textures/Reticle.png", new Color(1f, 1f, 1f, 0.95f));
        m["Plane"] = Overlay("Plane", "ARCar/UnlitAlpha", null, new Color(0.25f, 0.8f, 1f, 0.16f));
        m["DimLine"] = Overlay("DimLine", "ARCar/UnlitAlpha", null, new Color(1f, 0.85f, 0.1f, 1f));
        return m;
    }

    // =====================================================================
    // Префаб автомобиля
    // =====================================================================

    static Transform Find(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    static Transform Need(Transform root, string name)
    {
        var t = Find(root, name);
        if (t == null) throw new Exception("В модели не найден узел: " + name);
        return t;
    }

    static Bounds TotalBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        var b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    static GameObject FlatQuad(string name, Transform parent, Material material, Vector3 localPos, float width, float length)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = localPos;
        quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // лежит на земле, "верх" текстуры смотрит вперёд
        quad.transform.localScale = new Vector3(width, length, 1f);
        var mr = quad.GetComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return quad;
    }

    static void Bar(Transform parent, Material material, Vector3 center, Vector3 size)
    {
        var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.name = "Bar";
        UnityEngine.Object.DestroyImmediate(bar.GetComponent<Collider>());
        bar.transform.SetParent(parent, false);
        bar.transform.localPosition = center;
        bar.transform.localScale = size;
        var mr = bar.GetComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }

    static Button Swatch(string name, Transform parent, Color color)
    {
        var ring = UIObject(name, parent).AddComponent<Image>();
        ring.sprite = circleSprite;
        ring.color = new Color(1f, 1f, 1f, 0.85f);
        var fill = UIObject("Fill", ring.transform).AddComponent<Image>();
        fill.sprite = circleSprite;
        fill.color = color;
        fill.raycastTarget = false;
        Rect(fill.gameObject, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-10f, -10f));
        var button = ring.gameObject.AddComponent<Button>();
        button.targetGraphic = ring;
        button.transition = Selectable.Transition.None;
        return button;
    }

    static Transform Anchor(string name, Transform parent, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        return go.transform;
    }

    static GameObject CreateCarPrefab(Dictionary<string, Material> mats)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (source == null) throw new Exception("Модель не импортирована: " + ModelPath);

        var root = new GameObject("Car");
        var model = UnityEngine.Object.Instantiate(source);
        model.name = "Model";
        model.transform.SetParent(root.transform, false);

        // Разворачиваем модель "носом" вдоль +Z и ставим колёсами на землю по центру.
        var fl = Need(model.transform, "wheel_fl");
        var rl = Need(model.transform, "wheel_rl");
        if (fl.position.z < rl.position.z)
            model.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        var bounds = TotalBounds(model);
        model.transform.localPosition -= new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        bounds = TotalBounds(model);
        Debug.Log(Tag + "Car size (m): " + bounds.size.ToString("F2") + ", renderers=" + model.GetComponentsInChildren<Renderer>(true).Length);

        // Материалы: по имени исходного материала, с исключениями по имени детали.
        var bodyRenderer = Need(model.transform, "body").GetComponent<Renderer>();
        var rims = new List<Renderer>();
        var headlights = new List<Renderer>();
        var taillights = new List<Renderer>();
        var leather = new List<Renderer>();
        var calipers = new List<Renderer>();
        var missing = new HashSet<string>();
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            string node = r.name;
            string matName = r.sharedMaterial != null ? r.sharedMaterial.name : "";

            if (r == bodyRenderer) r.sharedMaterial = mats["Body"];
            else if (node.StartsWith("rim_")) { r.sharedMaterial = mats["Rim"]; rims.Add(r); }
            else if (node == "lights") { r.sharedMaterial = mats["HeadlightOff"]; headlights.Add(r); }
            else if (node == "lights_red") { r.sharedMaterial = mats["TaillightOff"]; taillights.Add(r); }
            else if (matName == "Taillight_Glass") { r.sharedMaterial = mats["Brakes"]; if (node == "brakes") calipers.Add(r); }
            else if (matName == "Leather") { r.sharedMaterial = mats["Leather"]; leather.Add(r); }
            else if (matName == "Projector_Glass") r.sharedMaterial = mats["HeadlightOff"];
            else if (matName == "Body_Color") r.sharedMaterial = mats["Body"];
            else if (mats.ContainsKey(matName)) r.sharedMaterial = mats[matName];
            else missing.Add(node + ":" + matName);
        }
        if (missing.Count > 0)
            Debug.LogWarning(Tag + "Материалы не сопоставлены: " + string.Join(", ", missing));

        var car = root.AddComponent<CarController>();
        car.bodyRenderer = bodyRenderer;
        car.bodyMaterial = mats["Body"];
        car.bodyXrayMaterial = mats["BodyXray"];
        car.rimRenderers = rims.ToArray();
        car.rimMaterial = mats["Rim"];
        car.leatherRenderers = leather.ToArray();
        car.leatherMaterial = mats["Leather"];
        car.caliperRenderers = calipers.ToArray();
        car.caliperMaterial = mats["Brakes"];
        car.headlightRenderers = headlights.ToArray();
        car.taillightRenderers = taillights.ToArray();
        car.headlightOff = mats["HeadlightOff"];
        car.headlightOn = mats["HeadlightOn"];
        car.taillightOff = mats["TaillightOff"];
        car.taillightOn = mats["TaillightOn"];

        var fr = Need(model.transform, "wheel_fr");
        var rr = Need(model.transform, "wheel_rr");
        car.frontWheels = new[] { fl, fr };
        car.rearWheels = new[] { rl, rr };
        car.spinSign = Vector3.Dot(fl.right, root.transform.right) >= 0f ? 1f : -1f;

        var steering = Need(model.transform, "steering_wheel");
        car.steeringWheel = steering;
        car.steeringAxis = SteeringAxis(steering);

        // Тень и свет фар.
        car.shadowBlob = FlatQuad("ShadowBlob", root.transform, mats["Shadow"],
            new Vector3(0f, 0.006f, 0f), bounds.size.x + 0.7f, bounds.size.z + 0.9f);
        car.headlightGlow = FlatQuad("HeadlightGlow", root.transform, mats["Glow"],
            new Vector3(0f, 0.012f, bounds.max.z + 1.7f), 3.2f, 4.2f);
        car.headlightGlow.SetActive(false);

        // Точка глаз водителя: чуть позади и выше руля.
        Vector3 sw = root.transform.InverseTransformPoint(steering.position);
        car.eyeAnchor = Anchor("EyeAnchor", root.transform, new Vector3(sw.x, sw.y + 0.27f, sw.z - 0.60f));

        // Якоря информационных точек.
        var anchors = new GameObject("InfoAnchors").transform;
        anchors.SetParent(root.transform, false);
        var lightsBounds = headlights.Count > 0 ? headlights[0].bounds : bounds;
        Vector3 flPos = root.transform.InverseTransformPoint(fl.position);
        Vector3 rlPos = root.transform.InverseTransformPoint(rl.position);
        Anchor("Engine", anchors, new Vector3(0f, 0.98f, rlPos.z + 0.20f));
        Anchor("Wheels", anchors, new Vector3(flPos.x + Mathf.Sign(flPos.x) * 0.18f, flPos.y, flPos.z));
        Anchor("Interior", anchors, new Vector3(sw.x, sw.y + 0.22f, sw.z));
        Anchor("Lights", anchors, new Vector3(lightsBounds.max.x * 0.85f, lightsBounds.center.y + 0.05f, lightsBounds.center.z));
        Anchor("Body", anchors, new Vector3(0f, 0.86f, bounds.max.z * 0.55f));

        // Габаритные линии (жёлтые) с засечками на концах.
        var dims = new GameObject("Dimensions").transform;
        dims.SetParent(root.transform, false);
        const float th = 0.025f, tick = 0.22f, halfWidth = 0.97f;
        float sideX = bounds.extents.x + 0.28f, frontZ = bounds.max.z + 0.32f, top = bounds.max.y;
        Bar(dims, mats["DimLine"], new Vector3(-sideX, 0.02f, bounds.center.z), new Vector3(th, th, bounds.size.z));
        Bar(dims, mats["DimLine"], new Vector3(-sideX, 0.02f, bounds.min.z), new Vector3(tick, th, th));
        Bar(dims, mats["DimLine"], new Vector3(-sideX, 0.02f, bounds.max.z), new Vector3(tick, th, th));
        Bar(dims, mats["DimLine"], new Vector3(0f, 0.02f, frontZ), new Vector3(halfWidth * 2f, th, th));
        Bar(dims, mats["DimLine"], new Vector3(-halfWidth, 0.02f, frontZ), new Vector3(th, th, tick));
        Bar(dims, mats["DimLine"], new Vector3(halfWidth, 0.02f, frontZ), new Vector3(th, th, tick));
        Bar(dims, mats["DimLine"], new Vector3(sideX, top * 0.5f, bounds.min.z), new Vector3(th, top, th));
        Bar(dims, mats["DimLine"], new Vector3(sideX, 0.02f, bounds.min.z), new Vector3(tick, th, th));
        Bar(dims, mats["DimLine"], new Vector3(sideX, top, bounds.min.z), new Vector3(tick, th, th));
        Anchor("DimLength", dims, new Vector3(-sideX, 0.02f, bounds.center.z));
        Anchor("DimWidth", dims, new Vector3(0f, 0.02f, frontZ));
        Anchor("DimHeight", dims, new Vector3(sideX, top * 0.5f, bounds.min.z));
        car.dimensions = dims.gameObject;

        // Звук двигателя (синтезируется кодом).
        root.AddComponent<AudioSource>().playOnAwake = false;
        root.AddComponent<EngineSound>().car = car;

        Debug.Log(Tag + "leather=" + leather.Count + " calipers=" + calipers.Count);
        Debug.Log(Tag + "rims=" + rims.Count + " headlights=" + headlights.Count + " taillights=" + taillights.Count +
                  " spinSign=" + car.spinSign + " steeringAxis=" + car.steeringAxis.ToString("F2") +
                  " eye=" + car.eyeAnchor.localPosition.ToString("F2"));

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, CarPrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        return prefab;
    }

    // Ось рулевой колонки = нормаль к плоскости обода руля (перебором угла наклона).
    static Vector3 SteeringAxis(Transform steering)
    {
        try
        {
            var rim = Find(steering, "steering_leather");
            var mesh = rim.GetComponent<MeshFilter>().sharedMesh;
            var verts = mesh.vertices;
            var pts = new List<Vector3>();
            int step = Mathf.Max(1, verts.Length / 2000);
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < verts.Length; i += step)
            {
                var p = steering.InverseTransformPoint(rim.TransformPoint(verts[i]));
                pts.Add(p);
                mean += p;
            }
            mean /= pts.Count;

            float bestVar = float.MaxValue;
            Vector3 best = Vector3.forward;
            for (int deg = 0; deg < 180; deg++)
            {
                float a = deg * Mathf.Deg2Rad;
                var n = new Vector3(0f, Mathf.Sin(a), Mathf.Cos(a));
                float variance = 0f;
                foreach (var p in pts)
                {
                    float d = Vector3.Dot(p - mean, n);
                    variance += d * d;
                }
                if (variance < bestVar) { bestVar = variance; best = n; }
            }
            return best;
        }
        catch (Exception e)
        {
            Debug.LogWarning(Tag + "Ось руля не вычислена: " + e.Message);
            return new Vector3(0f, 0.42f, 0.91f);
        }
    }

    static GameObject CreatePlanePrefab(Material material)
    {
        var go = new GameObject("ARPlane", typeof(ARPlane), typeof(ARPlaneMeshVisualizer), typeof(MeshFilter), typeof(MeshRenderer));
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ARPlane.prefab");
        UnityEngine.Object.DestroyImmediate(go);
        return prefab;
    }

    // =====================================================================
    // Сцена
    // =====================================================================

    static void CreateScene(GameObject carPrefab, GameObject planePrefab, Dictionary<string, Material> mats)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Окружение: небо нужно только для отражений на кузове (на экране его не видно).
        RenderSettings.skybox = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;

        new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager));

        // XR Origin → Camera Offset → Main Camera
        var originGo = new GameObject("XR Origin", typeof(XROrigin));
        var offsetGo = new GameObject("Camera Offset");
        offsetGo.transform.SetParent(originGo.transform, false);
        var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener),
            typeof(ARCameraManager), typeof(ARCameraBackground), typeof(TrackedPoseDriver));
        cameraGo.transform.SetParent(offsetGo.transform, false);
        cameraGo.tag = "MainCamera";
        var cam = cameraGo.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.nearClipPlane = 0.03f;
        cam.farClipPlane = 60f;

        var tpd = cameraGo.GetComponent<TrackedPoseDriver>();
        var positionAction = new InputAction("Position", binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
        positionAction.AddBinding("<HandheldARInputDevice>/devicePosition");
        var rotationAction = new InputAction("Rotation", binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
        rotationAction.AddBinding("<HandheldARInputDevice>/deviceRotation");
        tpd.positionInput = new InputActionProperty(positionAction);
        tpd.rotationInput = new InputActionProperty(rotationAction);

        var camManager = cameraGo.GetComponent<ARCameraManager>();
        camManager.requestedLightEstimation = LightEstimation.AmbientIntensity | LightEstimation.AmbientColor;

        var origin = originGo.GetComponent<XROrigin>();
        origin.CameraFloorOffsetObject = offsetGo;
        origin.Camera = cam;

        var raycastManager = originGo.AddComponent<ARRaycastManager>();
        var planeManager = originGo.AddComponent<ARPlaneManager>();
        planeManager.planePrefab = planePrefab;
        planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;

        // Свет
        var lightGo = new GameObject("Directional Light", typeof(Light));
        lightGo.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
        var sun = lightGo.GetComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        sun.shadows = LightShadows.None;
        var estimation = lightGo.AddComponent<ARLightEstimator>();
        estimation.cameraManager = camManager;

        // Автомобиль и прицел
        var carGo = (GameObject)PrefabUtility.InstantiatePrefab(carPrefab);
        var car = carGo.GetComponent<CarController>();

        var reticle = new GameObject("Reticle");
        FlatQuad("Ring", reticle.transform, mats["Reticle"], new Vector3(0f, 0.004f, 0f), 0.45f, 0.45f);

        // Логика приложения
        var appGo = new GameObject("App");
        var placement = appGo.AddComponent<ARPlacementController>();
        placement.raycastManager = raycastManager;
        placement.planeManager = planeManager;
        placement.arCamera = cam;
        placement.car = car;
        placement.reticle = reticle;

        var ui = appGo.AddComponent<AppUI>();
        ui.placement = placement;
        ui.car = car;
        ui.engine = carGo.GetComponent<EngineSound>();
        ui.arCamera = cam;
        var drive = appGo.AddComponent<DriveGame>();
        drive.placement = placement;
        drive.car = car;
        drive.engine = ui.engine;
        drive.ui = ui;
        drive.coinPrefab = CreateCoinPrefab();
        ui.drive = drive;
        BuildUI(ui, car, drive);

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log(Tag + "Scene saved=" + saved);

        if (!Application.isBatchMode) return;
        try { RenderPreviews(carGo, car, ui); }
        catch (Exception e) { Debug.LogWarning(Tag + "Preview failed: " + e); }
    }

    // =====================================================================
    // Интерфейс
    // =====================================================================

    static RectTransform Rect(GameObject go, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        var rt = go.GetComponent<RectTransform>();
        if (rt == null) rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    static GameObject UIObject(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static Image Panel(string name, Transform parent, Color color)
    {
        var go = UIObject(name, parent);
        var image = go.AddComponent<Image>();
        image.sprite = roundedSprite;
        image.type = Image.Type.Sliced;
        image.color = color;
        return image;
    }

    static Text Label(string name, Transform parent, string text, int size, TextAnchor anchor, Color color, FontStyle style = FontStyle.Normal)
    {
        var go = UIObject(name, parent);
        var label = go.AddComponent<Text>();
        label.font = font;
        label.text = text;
        label.fontSize = size;
        label.alignment = anchor;
        label.color = color;
        label.fontStyle = style;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        Rect(go, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        return label;
    }

    static Button Btn(string name, Transform parent, string text, int fontSize, Color color, out Text label)
    {
        var image = Panel(name, parent, color);
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.None; // цвет кнопок меняет AppUI
        label = Label("Label", image.transform, text, fontSize, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        return button;
    }

    static Button Btn(string name, Transform parent, string text, int fontSize, Color color)
    {
        return Btn(name, parent, text, fontSize, color, out _);
    }

    static GameObject SubPanel(string name, Transform parent, float height)
    {
        var panel = Panel(name, parent, PanelColor);
        Rect(panel.gameObject, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 490f), new Vector2(-60f, height));
        var layout = panel.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(24, 24, 20, 20);
        layout.spacing = 16;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;
        panel.gameObject.SetActive(false);
        return panel.gameObject;
    }

    static GameObject CreateCoinPrefab()
    {
        var coinMat = Pbr("Coin", 1f, 0.62f, 0.05f, 1f, 0.85f);
        MakeEmissive(coinMat, new Color(0.9f, 0.55f, 0.05f) * 0.6f);
        var root = new GameObject("Coin");
        var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        disc.name = "Disc";
        UnityEngine.Object.DestroyImmediate(disc.GetComponent<Collider>());
        disc.transform.SetParent(root.transform, false);
        disc.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // монета стоит на ребре
        disc.transform.localScale = new Vector3(0.75f, 0.04f, 0.75f);
        var mr = disc.GetComponent<MeshRenderer>();
        mr.sharedMaterial = coinMat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, "Assets/Prefabs/Coin.prefab");
        UnityEngine.Object.DestroyImmediate(root);
        return prefab;
    }

    static HoldButton Hold(string name, Transform parent, string text, int fontSize, Color color, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        var image = Panel(name, parent, color);
        Rect(image.gameObject, anchor, anchor, anchor, pos, size);
        Label("Label", image.transform, text, fontSize, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        return image.gameObject.AddComponent<HoldButton>();
    }

    static void BuildUI(AppUI ui, CarController car, DriveGame drive)
    {
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) throw new Exception("Не найден встроенный шрифт LegacyRuntime.ttf");

        // EventSystem для новой системы ввода
        var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        es.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();

        var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.matchWidthOrHeight = 0f;
        ui.canvas = canvas;
        Transform root = canvasGo.transform;

        // ---------- Информационные точки (под остальным интерфейсом) ----------
        var hotspotRoot = UIObject("Hotspots", root);
        Rect(hotspotRoot, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        var anchors = car.transform.Find("InfoAnchors");
        var info = new[]
        {
            new[] { "Engine", "Двигатель",
                "Атмосферный V8 объёмом 4,5 л расположен за сиденьями (среднемоторная компоновка).\n\n" +
                "• Мощность: 570 л. с. при 9000 об/мин\n• Крутящий момент: 540 Н·м\n" +
                "• Коробка: 7-ступенчатый робот с двумя сцеплениями\n• Привод: задний" },
            new[] { "Wheels", "Колёса и тормоза",
                "20-дюймовые кованые диски.\n\n• Шины спереди: 235/35 ZR20\n• Шины сзади: 295/35 ZR20\n" +
                "• Тормоза: карбон-керамические диски\n\nЦвет дисков можно сменить кнопкой «Диски»." },
            new[] { "Interior", "Салон",
                "Двухместный салон, ориентированный на водителя: основные органы управления вынесены на руль, как в болиде Формулы-1.\n\n" +
                "Нажмите «Салон», чтобы сесть за руль и осмотреться." },
            new[] { "Lights", "Фары",
                "Биксеноновые фары с вертикальными блоками светодиодных ходовых огней.\n\n" +
                "Кнопка «Фары» включает и выключает свет." },
            new[] { "Body", "Кузов и динамика",
                "Алюминиевый кузов со складной жёсткой крышей, дизайн ателье Pininfarina.\n\n• Длина × ширина × высота: 4527 × 1937 × 1211 мм\n" +
                "• Колёсная база: 2650 мм\n• Сухая масса: 1430 кг\n• Разгон 0–100 км/ч: 3,4 с\n• Максимальная скорость: 320 км/ч" },
        };
        var hotspots = new List<AppUI.Hotspot>();
        foreach (var item in info)
        {
            var markerImage = UIObject("Hotspot " + item[0], hotspotRoot.transform).AddComponent<Image>();
            markerImage.sprite = circleSprite;
            markerImage.color = Accent;
            var rt = Rect(markerImage.gameObject, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(92f, 92f));
            var button = markerImage.gameObject.AddComponent<Button>();
            button.targetGraphic = markerImage;
            Label("Label", markerImage.transform, "i", 56, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            markerImage.gameObject.SetActive(false);
            hotspots.Add(new AppUI.Hotspot
            {
                anchor = anchors.Find(item[0]), marker = rt, button = button, title = item[1], body = item[2]
            });
        }
        ui.hotspots = hotspots.ToArray();

        // ---------- Верхняя панель ----------
        var topBar = UIObject("TopBar", root);
        Rect(topBar, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -90f), new Vector2(-60f, 110f));
        ui.topBar = topBar;

        ui.resetButton = Btn("ResetButton", topBar.transform, "Заново", 38, ButtonColor);
        Rect(ui.resetButton.gameObject, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(230f, 0f));
        ui.resetButton.targetGraphic.color = PanelColor;

        ui.exitInteriorButton = Btn("ExitInteriorButton", topBar.transform, "Выйти из салона", 38, Accent);
        Rect(ui.exitInteriorButton.gameObject, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(380f, 0f));

        ui.photoButton = Btn("PhotoButton", topBar.transform, "Фото", 38, ButtonColor);
        Rect(ui.photoButton.gameObject, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(200f, 0f));
        ui.photoButton.targetGraphic.color = PanelColor;

        Text scaleLabel;
        ui.scaleButton = Btn("ScaleButton", topBar.transform, "1 : 1", 38, ButtonColor, out scaleLabel);
        Rect(ui.scaleButton.gameObject, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(-216f, 0f), new Vector2(200f, 0f));
        ui.scaleButton.targetGraphic.color = PanelColor;
        ui.scaleButtonLabel = scaleLabel;

        var scaleBox = Panel("ScaleBox", topBar.transform, PanelColor);
        Rect(scaleBox.gameObject, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-85f, 0f), new Vector2(-710f, 0f));
        scaleBox.raycastTarget = false;
        ui.scaleText = Label("ScaleText", scaleBox.transform, "Масштаб 25%", 34, TextAnchor.MiddleCenter, Color.white);
        // ---------- Подсказка ----------
        var hintBox = Panel("HintBox", root, PanelColor);
        Rect(hintBox.gameObject, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -225f), new Vector2(-60f, 150f));
        hintBox.raycastTarget = false;
        ui.hintBox = hintBox.gameObject;
        ui.hintText = Label("HintText", hintBox.transform, "", 38, TextAnchor.MiddleCenter, Color.white);
        var hintRt = ui.hintText.rectTransform;
        hintRt.offsetMin = new Vector2(24f, 10f);
        hintRt.offsetMax = new Vector2(-24f, -10f);

        // ---------- Нижняя панель (2 ряда по 4 кнопки) ----------
        var bottomBar = Panel("BottomBar", root, PanelColor);
        Rect(bottomBar.gameObject, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 50f), new Vector2(-60f, 420f));
        ui.bottomBar = bottomBar.gameObject;
        var grid = bottomBar.gameObject.AddComponent<GridLayoutGroup>();
        grid.padding = new RectOffset(18, 18, 18, 18);
        grid.spacing = new Vector2(12f, 12f);
        grid.cellSize = new Vector2(237f, 120f);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 4;
        grid.childAlignment = TextAnchor.MiddleCenter;

        ui.colorButton = Btn("ColorButton", bottomBar.transform, "Цвет", 38, ButtonColor);
        ui.finishButton = Btn("FinishButton", bottomBar.transform, "Покрытие", 38, ButtonColor);
        ui.rimsButton = Btn("RimsButton", bottomBar.transform, "Диски", 38, ButtonColor);
        ui.trimButton = Btn("TrimButton", bottomBar.transform, "Отделка", 38, ButtonColor);
        ui.lightsButton = Btn("LightsButton", bottomBar.transform, "Фары", 38, ButtonColor);
        ui.demoButton = Btn("DriveButton", bottomBar.transform, "Вождение", 36, ButtonColor);
        ui.engineButton = Btn("EngineButton", bottomBar.transform, "Двигатель", 36, ButtonColor);
        ui.showButton = Btn("ShowButton", bottomBar.transform, "Показ", 38, ButtonColor);
        ui.xrayButton = Btn("XrayButton", bottomBar.transform, "Рентген", 38, ButtonColor);
        ui.dimsButton = Btn("DimsButton", bottomBar.transform, "Габариты", 36, ButtonColor);
        ui.interiorButton = Btn("InteriorButton", bottomBar.transform, "Салон", 38, ButtonColor);
        ui.infoButton = Btn("InfoButton", bottomBar.transform, "Инфо", 38, ButtonColor);

        // ---------- Выбор цвета ----------
        ui.colorPanel = SubPanel("ColorPanel", root, 160f);
        var colorLayout = ui.colorPanel.GetComponent<HorizontalLayoutGroup>();
        colorLayout.childControlWidth = false;
        colorLayout.childControlHeight = false;
        colorLayout.childForceExpandWidth = false;
        colorLayout.childForceExpandHeight = false;
        colorLayout.spacing = 18;
        ui.colors = new[]
        {
            new Color(0.80f, 0.05f, 0.07f), // красный Rosso
            new Color(0.98f, 0.80f, 0.05f), // жёлтый Giallo
            new Color(0.96f, 0.96f, 0.96f), // белый
            new Color(0.03f, 0.03f, 0.035f), // чёрный
            new Color(0.55f, 0.57f, 0.60f), // серебристый
            new Color(0.05f, 0.20f, 0.65f), // синий
            new Color(0.04f, 0.35f, 0.18f), // зелёный
            new Color(0.95f, 0.42f, 0.05f), // оранжевый
        };
        var swatches = new List<Button>();
        for (int i = 0; i < ui.colors.Length; i++)
        {
            var ringImage = UIObject("Swatch" + i, ui.colorPanel.transform).AddComponent<Image>();
            ringImage.sprite = circleSprite;
            ringImage.color = new Color(1f, 1f, 1f, 0.85f);
            Rect(ringImage.gameObject, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(102f, 102f));
            var fill = UIObject("Fill", ringImage.transform).AddComponent<Image>();
            fill.sprite = circleSprite;
            fill.color = ui.colors[i];
            fill.raycastTarget = false;
            Rect(fill.gameObject, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-10f, -10f));
            var button = ringImage.gameObject.AddComponent<Button>();
            button.targetGraphic = ringImage;
            button.transition = Selectable.Transition.None;
            swatches.Add(button);
        }
        ui.colorSwatches = swatches.ToArray();

        // ---------- Покрытие ----------
        ui.finishPanel = SubPanel("FinishPanel", root, 150f);
        ui.finishButtons = new[]
        {
            Btn("Gloss", ui.finishPanel.transform, "Глянец", 36, ButtonColor),
            Btn("Matte", ui.finishPanel.transform, "Матовый", 36, ButtonColor),
            Btn("Metallic", ui.finishPanel.transform, "Металлик", 36, ButtonColor),
        };

        // ---------- Диски ----------
        ui.rimsPanel = SubPanel("RimsPanel", root, 150f);
        ui.rimButtons = new[]
        {
            Btn("Silver", ui.rimsPanel.transform, "Серебро", 34, ButtonColor),
            Btn("Black", ui.rimsPanel.transform, "Чёрные", 34, ButtonColor),
            Btn("Gold", ui.rimsPanel.transform, "Золото", 34, ButtonColor),
            Btn("BodyColor", ui.rimsPanel.transform, "В цвет", 34, ButtonColor),
        };

        // ---------- Отделка: кожа салона и суппорты ----------
        var trim = Panel("TrimPanel", root, PanelColor);
        Rect(trim.gameObject, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 490f), new Vector2(-60f, 150f));
        ui.trimPanel = trim.gameObject;
        ui.leatherColors = new[]
        {
            new Color(0.06f, 0.06f, 0.07f), new Color(0.50f, 0.06f, 0.06f), new Color(0.76f, 0.62f, 0.45f),
            new Color(0.36f, 0.19f, 0.09f), new Color(0.88f, 0.85f, 0.78f),
        };
        ui.caliperColors = new[]
        {
            new Color(0.75f, 0.03f, 0.03f), new Color(0.98f, 0.80f, 0.05f), new Color(0.04f, 0.04f, 0.045f),
            new Color(0.05f, 0.25f, 0.75f), new Color(0.70f, 0.70f, 0.72f),
        };
        var rowNames = new[] { "Кожа салона", "Суппорты" };
        var rowColors = new[] { ui.leatherColors, ui.caliperColors };
        var rowButtons = new List<Button>[] { new List<Button>(), new List<Button>() };
        // Суппорты в этой модели снаружи не видны, поэтому показываем только ряд "Салон".
        for (int row = 0; row < 1; row++)
        {
            float y = 0f;
            var rowLabel = Label(rowNames[row], trim.transform, rowNames[row], 36, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            Rect(rowLabel.gameObject, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(40f, y), new Vector2(260f, 100f));
            for (int i = 0; i < rowColors[row].Length; i++)
            {
                var swatch = Swatch(rowNames[row] + i, trim.transform, rowColors[row][i]);
                Rect(swatch.gameObject, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(330f + i * 126f, y), new Vector2(100f, 100f));
                rowButtons[row].Add(swatch);
            }
        }
        ui.leatherSwatches = rowButtons[0].ToArray();
        ui.caliperSwatches = rowButtons[1].ToArray();
        trim.gameObject.SetActive(false);

        // ---------- Подписи габаритов ----------
        var dimInfo = new[] { new[] { "DimLength", "Длина 4527 мм" }, new[] { "DimWidth", "Ширина 1937 мм" }, new[] { "DimHeight", "Высота 1211 мм" } };
        var dimLabels = new List<AppUI.DimensionLabel>();
        foreach (var d in dimInfo)
        {
            var pill = Panel("Label " + d[0], hotspotRoot.transform, new Color(0.06f, 0.07f, 0.09f, 0.9f));
            pill.raycastTarget = false;
            var pillRt = Rect(pill.gameObject, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(330f, 70f));
            Label("Text", pill.transform, d[1], 32, TextAnchor.MiddleCenter, new Color(1f, 0.88f, 0.2f), FontStyle.Bold);
            pill.gameObject.SetActive(false);
            dimLabels.Add(new AppUI.DimensionLabel { anchor = car.dimensions.transform.Find(d[0]), marker = pillRt });
        }
        ui.dimensionLabels = dimLabels.ToArray();

        // ---------- Карточка с информацией ----------
        var card = Panel("InfoCard", root, new Color(0.06f, 0.07f, 0.09f, 0.94f));
        Rect(card.gameObject, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 490f), new Vector2(-60f, 560f));
        ui.infoCard = card.gameObject;
        ui.infoTitle = Label("Title", card.transform, "Заголовок", 52, TextAnchor.UpperLeft, Color.white, FontStyle.Bold);
        ui.infoTitle.rectTransform.offsetMin = new Vector2(40f, 0f);
        ui.infoTitle.rectTransform.offsetMax = new Vector2(-130f, -34f);
        ui.infoBody = Label("Body", card.transform, "Текст", 36, TextAnchor.UpperLeft, new Color(0.9f, 0.92f, 0.95f));
        ui.infoBody.rectTransform.offsetMin = new Vector2(40f, 30f);
        ui.infoBody.rectTransform.offsetMax = new Vector2(-40f, -120f);
        ui.infoBody.lineSpacing = 1.1f;
        ui.infoCloseButton = Btn("Close", card.transform, "×", 60, ButtonColor);
        Rect(ui.infoCloseButton.gameObject, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-22f, -22f), new Vector2(90f, 90f));
        card.gameObject.SetActive(false);

        // ---------- Мини-игра "Вождение" ----------
        var driveHud = UIObject("DriveHUD", root);
        Rect(driveHud, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        drive.hud = driveHud;

        drive.exitButton = Btn("ExitDriveButton", driveHud.transform, "Выйти из игры", 38, Accent);
        Rect(drive.exitButton.gameObject, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -90f), new Vector2(380f, 110f));

        var scoreBox = Panel("ScoreBox", driveHud.transform, PanelColor);
        scoreBox.raycastTarget = false;
        Rect(scoreBox.gameObject, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -395f), new Vector2(-60f, 130f));
        drive.scoreText = Label("ScoreText", scoreBox.transform, "Монеты 0 / 8", 36, TextAnchor.MiddleCenter, new Color(1f, 0.88f, 0.2f), FontStyle.Bold);

        var pad = new Color(0.06f, 0.07f, 0.09f, 0.78f);
        drive.leftButton = Hold("LeftButton", driveHud.transform, "<", 110, pad, new Vector2(0f, 0f), new Vector2(40f, 90f), new Vector2(210f, 210f));
        drive.rightButton = Hold("RightButton", driveHud.transform, ">", 110, pad, new Vector2(0f, 0f), new Vector2(270f, 90f), new Vector2(210f, 210f));
        drive.gasButton = Hold("GasButton", driveHud.transform, "Газ", 52, Accent, new Vector2(1f, 0f), new Vector2(-40f, 90f), new Vector2(230f, 230f));
        drive.brakeButton = Hold("BrakeButton", driveHud.transform, "Тормоз\nназад", 34, pad, new Vector2(1f, 0f), new Vector2(-290f, 90f), new Vector2(210f, 210f));

        driveHud.SetActive(false);

        // ---------- Всплывающее сообщение ----------
        var toast = Panel("Toast", root, new Color(0.06f, 0.07f, 0.09f, 0.94f));
        Rect(toast.gameObject, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-140f, 150f));
        toast.raycastTarget = false;
        ui.toastBox = toast.gameObject;
        ui.toastText = Label("ToastText", toast.transform, "", 36, TextAnchor.MiddleCenter, Color.white);
        toast.gameObject.SetActive(false);

        // ---------- Стартовый экран ----------
        var start = UIObject("StartPanel", root).AddComponent<Image>();
        start.color = new Color(0.04f, 0.05f, 0.07f, 0.93f);
        Rect(start.gameObject, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        ui.startPanel = start.gameObject;

        var title = Label("Title", start.transform, "AR Автосалон", 104, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        Rect(title.gameObject, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -330f), new Vector2(-80f, 140f));
        var subtitle = Label("Subtitle", start.transform, "Ferrari 458 Spider в дополненной реальности", 42, TextAnchor.MiddleCenter, new Color(0.95f, 0.45f, 0.45f));
        Rect(subtitle.gameObject, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -480f), new Vector2(-80f, 70f));

        var features = Label("Features", start.transform,
            "•  Поставьте автомобиль на пол у себя в комнате\n" +
            "•  Двигайте, вращайте, увеличивайте до реального размера\n" +
            "•  Выбирайте цвет кузова, покрытие, диски и отделку\n" +
            "•  Включайте фары и заводите двигатель\n" +
            "•  Прокатитесь: соберите все монеты на время\n" +
            "•  Загляните под кузов в режиме «Рентген»\n" +
            "•  Сядьте за руль и осмотрите салон\n" +
            "•  Изучайте характеристики по точкам на кузове\n" +
            "•  Сохраняйте фото в галерею",
            38, TextAnchor.UpperLeft, new Color(0.88f, 0.9f, 0.93f));
        features.lineSpacing = 1.35f;
        Rect(features.gameObject, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -640f), new Vector2(-180f, 640f));

        ui.startButton = Btn("StartButton", start.transform, "Начать", 56, Accent);
        Rect(ui.startButton.gameObject, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 260f), new Vector2(560f, 150f));

    }

    // =====================================================================
    // Проверочные картинки (Previews/*.png) — чтобы увидеть результат без телефона
    // =====================================================================

    static void RenderPreviews(GameObject carGo, CarController car, AppUI ui)
    {
        Directory.CreateDirectory("Previews");
        var camGo = new GameObject("PreviewCamera", typeof(Camera));
        var cam = camGo.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.55f, 0.56f, 0.58f);
        cam.nearClipPlane = 0.03f;
        cam.farClipPlane = 100f;
        cam.fieldOfView = 55f;

        // Пол, чтобы были видны тень и свет фар.
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.transform.position = new Vector3(0f, -0.002f, 0f);
        floor.transform.localScale = Vector3.one * 4f;

        // В редакторе Awake() не вызывается, поэтому скрываем вручную то, что в игре изначально выключено.
        car.dimensions.SetActive(false);
        ui.drive.hud.SetActive(false);

        // 1. Снаружи, три четверти спереди
        cam.transform.position = new Vector3(3.6f, 1.7f, 4.6f);
        cam.transform.LookAt(new Vector3(0f, 0.55f, 0f));
        Shot(cam, "Previews/car_front.png", 1280, 800);

        // 2. Сзади с включёнными фарами
        foreach (var r in car.headlightRenderers) r.sharedMaterial = car.headlightOn;
        foreach (var r in car.taillightRenderers) r.sharedMaterial = car.taillightOn;
        car.headlightGlow.SetActive(true);
        cam.transform.position = new Vector3(-3.4f, 1.6f, -4.8f);
        cam.transform.LookAt(new Vector3(0f, 0.55f, 0f));
        Shot(cam, "Previews/car_rear_lights.png", 1280, 800);
        cam.transform.position = new Vector3(2.2f, 2.4f, 6.5f);
        cam.transform.LookAt(new Vector3(0f, 0.3f, 2.5f));
        Shot(cam, "Previews/car_headlights.png", 1280, 800);

        // 3. Рентген
        car.bodyRenderer.sharedMaterial = car.bodyXrayMaterial;
        cam.transform.position = new Vector3(3.6f, 2.2f, -4.2f);
        cam.transform.LookAt(new Vector3(0f, 0.55f, 0f));
        Shot(cam, "Previews/car_xray.png", 1280, 800);
        car.bodyRenderer.sharedMaterial = car.bodyMaterial;

        // Проверка поворота колёс: вид сверху, колёса повёрнуты "вправо" (+30°) тем же способом, что в игре.
        var spinRot = Quaternion.Euler(40f * car.spinSign, 0f, 0f);
        var wasRot = new[] { car.frontWheels[0].localRotation, car.frontWheels[1].localRotation };
        for (int i = 0; i < 2; i++)
        {
            Vector3 axis = car.frontWheels[i].parent.InverseTransformDirection(carGo.transform.up).normalized;
            car.frontWheels[i].localRotation = Quaternion.AngleAxis(30f, axis) * wasRot[i] * spinRot;
        }
        cam.fieldOfView = 40f;
        cam.transform.position = new Vector3(0f, 9f, 0.3f);
        cam.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        Shot(cam, "Previews/steer_top.png", 800, 1000);
        cam.transform.position = new Vector3(0.6f, 1.0f, 6.5f);
        cam.transform.LookAt(new Vector3(0f, 0.35f, 1.3f));
        Shot(cam, "Previews/steer_front.png", 1200, 700);
        for (int i = 0; i < 2; i++) car.frontWheels[i].localRotation = wasRot[i];

        // 4. Вид из салона (портрет, как на телефоне)
        cam.fieldOfView = 65f;
        cam.transform.position = car.eyeAnchor.position;
        cam.transform.rotation = Quaternion.LookRotation(carGo.transform.forward, Vector3.up);
        Shot(cam, "Previews/interior.png", 720, 1280);
        cam.transform.rotation = Quaternion.LookRotation(carGo.transform.forward + carGo.transform.right * 1.2f - Vector3.up * 0.3f, Vector3.up);
        Shot(cam, "Previews/interior_right.png", 720, 1280);
        // Проверка отделки: бежевая кожа и жёлтые суппорты (цвета материалов потом возвращаем).
        Color leatherWas = car.leatherMaterial.color, caliperWas = car.caliperMaterial.color;
        car.leatherMaterial.color = new Color(0.76f, 0.62f, 0.45f);
        car.caliperMaterial.color = new Color(0.98f, 0.80f, 0.05f);
        cam.transform.rotation = Quaternion.LookRotation(carGo.transform.forward + carGo.transform.right * 1.5f - Vector3.up * 0.7f, Vector3.up);
        Shot(cam, "Previews/interior_beige.png", 720, 1280);
        cam.fieldOfView = 40f;
        cam.transform.position = new Vector3(-2.6f, 0.6f, 2.6f);
        cam.transform.LookAt(new Vector3(-0.9f, 0.35f, 1.3f));
        Shot(cam, "Previews/caliper_yellow.png", 1024, 768);
        car.leatherMaterial.color = leatherWas;
        car.caliperMaterial.color = caliperWas;

        // 5. Интерфейс поверх вида на машину
        cam.fieldOfView = 60f;
        cam.transform.position = new Vector3(4.2f, 2.9f, 7.6f);
        cam.transform.LookAt(new Vector3(0f, 0.2f, -0.6f));
        cam.aspect = 1080f / 1920f; // как у снимков ниже — иначе метки на экране съезжают
        var canvas = ui.canvas;
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = cam;
        canvas.planeDistance = 0.5f;

        ui.startPanel.SetActive(true);
        ui.topBar.SetActive(false); ui.bottomBar.SetActive(false); ui.hintBox.SetActive(false);
        Shot(cam, "Previews/ui_start.png", 1080, 1920);
        ui.topBar.SetActive(true); ui.bottomBar.SetActive(true); ui.hintBox.SetActive(true);

        ui.startPanel.SetActive(false);
        ui.hintText.text = "Один палец — двигать\nДва пальца — вращать и менять размер";
        ui.exitInteriorButton.gameObject.SetActive(false);
        ui.colorPanel.SetActive(true);
        Shot(cam, "Previews/ui_colors.png", 1080, 1920);

        ui.colorPanel.SetActive(false);
        ui.rimsPanel.SetActive(true);
        Shot(cam, "Previews/ui_rims.png", 1080, 1920);

        ui.rimsPanel.SetActive(false);
        ui.trimPanel.SetActive(true);
        Shot(cam, "Previews/ui_trim.png", 1080, 1920);
        ui.trimPanel.SetActive(false);

        ui.hintBox.SetActive(false);
        car.dimensions.SetActive(true);
        foreach (var d in ui.dimensionLabels)
        {
            d.marker.gameObject.SetActive(true);
            Vector3 vp = cam.WorldToViewportPoint(d.anchor.position);
            d.marker.anchorMin = d.marker.anchorMax = new Vector2(vp.x, vp.y);
            d.marker.anchoredPosition = Vector2.zero;
        }
        Shot(cam, "Previews/ui_dims.png", 1080, 1920);
        car.dimensions.SetActive(false);
        foreach (var d in ui.dimensionLabels) d.marker.gameObject.SetActive(false);

        // Режим вождения: в игре AppUI.SetDriving прячет эти элементы.
        ui.bottomBar.SetActive(false);
        ui.resetButton.gameObject.SetActive(false);
        ui.scaleButton.gameObject.SetActive(false);
        ui.scaleText.transform.parent.gameObject.SetActive(false);
        ui.drive.hud.SetActive(true);
        Shot(cam, "Previews/ui_drive.png", 1080, 1920);
        ui.drive.hud.SetActive(false);
        ui.bottomBar.SetActive(true);
        ui.resetButton.gameObject.SetActive(true);
        ui.scaleButton.gameObject.SetActive(true);
        ui.scaleText.transform.parent.gameObject.SetActive(true);
        foreach (var d in ui.dimensionLabels) d.marker.gameObject.SetActive(false);
        ui.hintBox.SetActive(false);
        ui.infoTitle.text = ui.hotspots[4].title;
        ui.infoBody.text = ui.hotspots[4].body;
        ui.infoCard.SetActive(true);
        foreach (var h in ui.hotspots)
        {
            h.marker.gameObject.SetActive(true);
            Vector3 vp = cam.WorldToViewportPoint(h.anchor.position);
            h.marker.anchorMin = h.marker.anchorMax = new Vector2(vp.x, vp.y);
            h.marker.anchoredPosition = Vector2.zero;
        }
        Shot(cam, "Previews/ui_info.png", 1080, 1920);

        Debug.Log(Tag + "Previews rendered");
        // Сцена уже сохранена; изменения выше в файл не попадают.
        UnityEngine.Object.DestroyImmediate(camGo);
        UnityEngine.Object.DestroyImmediate(floor);
    }

    static void Shot(Camera cam, string path, int width, int height)
    {
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        rt.antiAliasing = 4;
        cam.targetTexture = rt;
        Canvas.ForceUpdateCanvases();
        cam.Render();
        Canvas.ForceUpdateCanvases();
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new UnityEngine.Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        cam.targetTexture = null;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);
    }
}
