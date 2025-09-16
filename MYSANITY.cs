// Author: Omni-Empress
// Idea by: S1ckBoy

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Photon.Pun;

namespace Empress.REPO.Insanity
{
    [BepInPlugin(GUID, Name, Version)]
    public class InsanityMod : BaseUnityPlugin
    {
        public const string GUID = "empress.repo.insanitymod";
        public const string Name = "Insanity & Hallucinations";
        public const string Version = "1.2.0";

        internal static ManualLogSource LogS;
        private Harmony _harmony;

        internal static ConfigEntry<float> CfgUI_PosX;
        internal static ConfigEntry<float> CfgUI_PosY;
        internal static ConfigEntry<float> CfgUI_Width;
        internal static ConfigEntry<float> CfgUI_Height;

        private void Awake()
        {
            LogS = Logger;
            _harmony = new Harmony(GUID);
            _harmony.PatchAll();

            CfgUI_PosX = Config.Bind("SanityUI", "PosX", 0f, "anchoredPosition.x (anchor 0.5,0)");
            CfgUI_PosY = Config.Bind("SanityUI", "PosY", 36f, "anchoredPosition.y (anchor 0.5,0)");
            CfgUI_Width = Config.Bind("SanityUI", "Width", 190f, "sizeDelta.x (width)");
            CfgUI_Height = Config.Bind("SanityUI", "Height", 14f, "sizeDelta.y (height)");

            var host = new GameObject("[InsanityControllerHost]");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.DontSave;

            host.AddComponent<InsanityController>();
            host.AddComponent<SanityUIBootstrap>();

            LogS.LogInfo($"{Name} {Version} loaded.");
        }

        private void OnDestroy()
        {
            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (System.Exception e) { InsanityMod.LogS?.LogDebug($"UnpatchSelf failed: {e}"); }
                _harmony = null;
            }
        }

        internal sealed class InsanityController : MonoBehaviour
        {
            public static InsanityController Instance { get; private set; }

            [Header("Core")]
            public float MaxInsanity = 100f;
            public float AloneIncreasePerSecond = 1.0f;
            public float EnemySeenBurst = 20f;
            public float FriendDecayPerFriendPerSecond = 2.0f;

            [Header("Enemy Pressure (continuous)")]
            public float EnemyProximityRadius = 16f;
            public float EnemyProximityPerEnemyPerSecond = 1.0f;
            public int EnemyProximityMaxStack = 5;

            [Header("Solo Fairness (Truck calm)")]
            public float TruckCalmPerSecond = 3.0f;

            [Header("Proximity & Vision")]
            public float FriendRadius = 10f;
            public float EnemyMaxRange = 40f;
            public float EnemyFOVDegrees = 70f;
            public float EnemySeenCooldown = 1.0f;

            [Header("Panic Shader Limits (added on top of defaults)")]
            public float VignetteAdd = 0.35f;
            public float GrainIntensityAdd = 0.5f;
            public float ChromaticAberrationAdd = 0.6f;
            public float LensDistortionTarget = -35f;

            [Header("Color Grade")]
            public bool EnableDesaturate = true;
            public float DesaturateAt = 100f;

            [Header("Hallucinations")]
            public bool EnableHallucinations = true;
            public float HallucinationStartAt = 35f;
            public float StrongHallucinationsAt = 75f;
            public float ExtremeHallucinationsAt = 90f;
            public float MinShadowSpawnInterval = 8f;
            public float MaxShadowSpawnInterval = 18f;
            public float ShadowLifetime = 2.2f;
            public float ShadowMinDistance = 6f;
            public float ShadowMaxDistance = 12f;

            [Header("Arming")]
            public bool ResetInsanityOnLevelLoad = true;

            [Header("Debug UI")]
            public bool ShowIMGUI = false;

            [Header("Glitch Cooldowns (sec)")]
            public float TinyGlitchCooldown = 1.2f;
            public float ShortGlitchCooldown = 3.0f;

            private float _insanity;
            private float _enemySeenCD;
            private float _shadowSpawnTimer;
            private float _glitchTimer;
            private bool _armed;
            private System.Random _rng;

            public float EnemyPressure01 { get; private set; }

            private PostProcessVolume _ppVolume;
            private Vignette _ppVignette;
            private Grain _ppGrain;
            private ChromaticAberration _ppChromatic;
            private LensDistortion _ppDistortion;
            private ColorGrading _ppColor;

            private float _vignetteDefault;
            private float _grainDefault;
            private float _chromaticDefault;
            private float _distortionDefault;
            private float _saturationDefault;

            private Camera _cam;

            public float CurrentInsanity => _insanity;
            public float NormalizedInsanity => MaxInsanity <= 0f ? 0f : _insanity / MaxInsanity;

            private void OnEnable()
            {
                SceneManager.sceneLoaded += OnSceneLoaded_Disarm;
            }

            private void OnDisable()
            {
                SceneManager.sceneLoaded -= OnSceneLoaded_Disarm;
            }

            private void OnSceneLoaded_Disarm(Scene scene, LoadSceneMode mode)
            {
                Disarm("scene load: " + scene.name);
                StartCoroutine(ArmWhenGameplayReady());
            }

            private void Start()
            {
                Instance = this;
                _rng = new System.Random();
                TryBindCamera();
                TryBindPostProcessing();
                ResetShadowTimer(true);
                StartCoroutine(ArmWhenGameplayReady());
            }

            private void OnDestroy()
            {
                if (Instance == this) Instance = null;
            }

            private IEnumerator ArmWhenGameplayReady()
            {
                while (GameDirector.instance == null ||
                       GameDirector.instance.currentState != GameDirector.gameState.Main)
                    yield return null;

                while (LevelGenerator.Instance == null) yield return null;

                while (GameDirector.instance.PlayerList == null ||
                       GameDirector.instance.PlayerList.Count == 0)
                    yield return null;

                while (PlayerController.instance == null ||
                       PlayerController.instance.playerAvatarScript == null)
                    yield return null;

                yield return new WaitForSeconds(0.15f);

                _armed = true;
            }

            private void Disarm(string why)
            {
                _armed = false;
                if (ResetInsanityOnLevelLoad) _insanity = 0f;

                if (_ppVignette != null) _ppVignette.intensity.value = _vignetteDefault;
                if (_ppGrain != null) _ppGrain.intensity.value = _grainDefault;
                if (_ppChromatic != null) _ppChromatic.intensity.value = _chromaticDefault;
                if (_ppDistortion != null) _ppDistortion.intensity.value = _distortionDefault;
                if (_ppColor != null) _ppColor.saturation.value = _saturationDefault;

                _enemySeenCD = 0f;
                _glitchTimer = 0f;
                EnemyPressure01 = 0f;
                ResetShadowTimer(true);
            }

            private void Update()
            {
                if (!_armed) return;

                var gd = GameDirector.instance;
                if (gd == null || gd.currentState != GameDirector.gameState.Main)
                    return;

                var pc = PlayerController.instance;
                var me = pc?.playerAvatarScript;
                if (pc == null || me == null)
                    return;

                if (_glitchTimer > 0f) _glitchTimer -= Time.deltaTime;

                if (_cam == null) TryBindCamera();

                int friendCount = CountNearbyFriends(me.transform.position, me);
                int enemyNearby = CountNearbyEnemies(me.transform.position);

                if (_enemySeenCD > 0f) _enemySeenCD -= Time.deltaTime;
                bool seenNow = (_enemySeenCD <= 0f && EnemyVisible(_cam, me));
                if (seenNow)
                {
                    AddInsanity(EnemySeenBurst);
                    _enemySeenCD = EnemySeenCooldown;
                    SafeGlitchTiny();
                }

                float pressureRaw = Mathf.Min(enemyNearby + (seenNow ? 1 : 0), EnemyProximityMaxStack);
                EnemyPressure01 = Mathf.Clamp01(pressureRaw / Mathf.Max(1, EnemyProximityMaxStack));

                float dt = Time.deltaTime;
                float delta = 0f;

                bool inTruck = false;
                try { inTruck = me.RoomVolumeCheck != null && me.RoomVolumeCheck.inTruck; } catch { }

                if (friendCount <= 0)
                {
                    if (inTruck)
                    {
                        delta += -Mathf.Abs(TruckCalmPerSecond) * dt;
                    }
                    else
                    {
                        delta += AloneIncreasePerSecond * dt;
                    }
                }
                else
                {
                    delta += -FriendDecayPerFriendPerSecond * friendCount * dt;
                }

                if (enemyNearby > 0)
                {
                    delta += EnemyProximityPerEnemyPerSecond * enemyNearby * dt;
                }

                AddInsanity(delta);

                ApplyPanicShader();

                if (EnableHallucinations)
                {
                    HallucinationTick(me.transform);
                }
            }

            private void OnGUI()
            {
                if (!ShowIMGUI) return;
                GUILayout.BeginArea(new Rect(20, 20, 300, 100), GUI.skin.box);
                GUILayout.Label($"Insanity: {Mathf.RoundToInt(_insanity)} / {Mathf.RoundToInt(MaxInsanity)}");
                GUILayout.Label($"EnemyPressure: {EnemyPressure01:0.00}");
                GUILayout.EndArea();
            }

            private void AddInsanity(float delta)
            {
                _insanity = Mathf.Clamp(_insanity + delta, 0f, MaxInsanity);
            }

            private void TryBindCamera()
            {
                _cam = CameraUtils.Instance != null ? CameraUtils.Instance.MainCamera : Camera.main;
            }

            private void TryBindPostProcessing()
            {
                _ppVolume = PostProcessing.Instance != null ? PostProcessing.Instance.volume : null;
                if (_ppVolume == null || _ppVolume.profile == null) return;

                _ppVolume.profile.TryGetSettings<Vignette>(out _ppVignette);
                _ppVolume.profile.TryGetSettings<Grain>(out _ppGrain);
                _ppVolume.profile.TryGetSettings<ChromaticAberration>(out _ppChromatic);
                _ppVolume.profile.TryGetSettings<LensDistortion>(out _ppDistortion);
                _ppVolume.profile.TryGetSettings<ColorGrading>(out _ppColor);

                if (_ppVignette != null) _vignetteDefault = _ppVignette.intensity.value;
                if (_ppGrain != null) _grainDefault = _ppGrain.intensity.value;
                if (_ppChromatic != null) _chromaticDefault = _ppChromatic.intensity.value;
                if (_ppDistortion != null) _distortionDefault = _ppDistortion.intensity.value;
                if (_ppColor != null) _saturationDefault = _ppColor.saturation.value;
            }

            private void ApplyPanicShader()
            {
                if (_ppVolume == null)
                    TryBindPostProcessing();

                if (_ppVolume == null) return;

                float t = Mathf.InverseLerp(0f, MaxInsanity, _insanity);

                if (_ppVignette != null)
                    _ppVignette.intensity.value = Mathf.Clamp01(_vignetteDefault + VignetteAdd * t);

                if (_ppGrain != null)
                    _ppGrain.intensity.value = Mathf.Clamp01(_grainDefault + GrainIntensityAdd * t);

                if (_ppChromatic != null)
                    _ppChromatic.intensity.value = Mathf.Clamp01(_chromaticDefault + ChromaticAberrationAdd * t);

                if (_ppDistortion != null)
                    _ppDistortion.intensity.value = Mathf.Lerp(_distortionDefault, LensDistortionTarget, t);

                if (EnableDesaturate && _ppColor != null)
                {
                    float bwT = Mathf.Clamp01(_insanity / Mathf.Max(1f, DesaturateAt));
                    float targetSat = Mathf.Lerp(_saturationDefault, -100f, bwT);
                    _ppColor.saturation.value = targetSat;
                }
            }

            private int CountNearbyFriends(Vector3 myPos, PlayerAvatar me)
            {
                int count = 0;
                var list = GameDirector.instance.PlayerList;
                if (list == null) return 0;

                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    if (p == null || p == me) continue;
                    float d = Vector3.Distance(myPos, p.transform.position);
                    if (d <= FriendRadius) count++;
                }
                return count;
            }

            private int CountNearbyEnemies(Vector3 myPos)
            {
                int count = 0;
                var ed = EnemyDirector.instance;
                if (ed == null || ed.enemiesSpawned == null || ed.enemiesSpawned.Count == 0) return 0;

                float r = EnemyProximityRadius;
                float r2 = r * r;
                foreach (var enemyParent in ed.enemiesSpawned)
                {
                    if (enemyParent == null) continue;
                    Vector3 d = enemyParent.transform.position - myPos;
                    if (d.sqrMagnitude <= r2) count++;
                }
                return count;
            }

            private bool EnemyVisible(Camera cam, PlayerAvatar me)
            {
                if (cam == null) return false;
                var ed = EnemyDirector.instance;
                if (ed == null || ed.enemiesSpawned == null || ed.enemiesSpawned.Count == 0) return false;

                Vector3 eyePos = cam.transform.position;
                Vector3 fwd = cam.transform.forward;
                float maxDot = Mathf.Cos(EnemyFOVDegrees * Mathf.Deg2Rad * 0.5f);

                foreach (var enemyParent in ed.enemiesSpawned)
                {
                    if (enemyParent == null) continue;
                    Vector3 target = enemyParent.transform.position + Vector3.up * 1.2f;
                    Vector3 dir = (target - eyePos);
                    float dist = dir.magnitude;
                    if (dist > EnemyMaxRange) continue;

                    Vector3 ndir = dir / (dist > 0.0001f ? dist : 1f);
                    float dot = Vector3.Dot(fwd, ndir);
                    if (dot < maxDot) continue;

                    if (Physics.Raycast(eyePos, ndir, out var hit, dist))
                    {
                        if (hit.transform != null && hit.transform.CompareTag("Enemy"))
                            return true;

                        if (Physics.SphereCast(eyePos, 0.15f, ndir, out var shit, dist))
                        {
                            if (shit.transform != null && shit.transform.CompareTag("Enemy"))
                                return true;
                        }
                        continue;
                    }
                    return true;
                }
                return false;
            }

            private void SafeGlitchTiny()
            {
                if (_glitchTimer > 0f) return;
                var cg = CameraGlitch.Instance;
                if (cg != null)
                {
                    try { cg.PlayTiny(); } catch { }
                }
                _glitchTimer = TinyGlitchCooldown;
            }

            private void SafeGlitchShort()
            {
                if (_glitchTimer > 0f) return;
                var cg = CameraGlitch.Instance;
                if (cg != null)
                {
                    try { cg.PlayShort(); } catch { }
                }
                _glitchTimer = ShortGlitchCooldown;
            }

            private void HallucinationTick(Transform me)
            {
                float t = _insanity;

                if (t >= StrongHallucinationsAt)
                {
                    if (UnityEngine.Random.value < 0.25f * Time.deltaTime)
                        SafeGlitchShort();
                }
                else if (t >= HallucinationStartAt)
                {
                    if (UnityEngine.Random.value < 0.12f * Time.deltaTime)
                        SafeGlitchTiny();
                }

                if (t >= HallucinationStartAt)
                {
                    _shadowSpawnTimer -= Time.deltaTime;
                    if (_shadowSpawnTimer <= 0f)
                    {
                        SpawnShadowHallucination(me);
                        ResetShadowTimer(false);
                    }
                }

                if (t >= StrongHallucinationsAt)
                {
                    if (UnityEngine.Random.value < 0.10f * Time.deltaTime)
                        SpawnPeripheryFlicker();
                }
                if (t >= ExtremeHallucinationsAt)
                {
                    if (UnityEngine.Random.value < 0.06f * Time.deltaTime)
                        SpawnFlashLight(me.position);

                    if (UnityEngine.Random.value < 0.04f * Time.deltaTime)
                        StartCoroutine(CameraRollNudge());
                }
            }

            private void ResetShadowTimer(bool first)
            {
                float n = Mathf.InverseLerp(HallucinationStartAt, MaxInsanity, _insanity);
                float min = Mathf.Lerp(MaxShadowSpawnInterval, MinShadowSpawnInterval, n * 0.5f);
                float max = Mathf.Lerp(MaxShadowSpawnInterval, MinShadowSpawnInterval, n);
                if (max < min) (min, max) = (max, min);
                _shadowSpawnTimer = UnityEngine.Random.Range(min, max);
            }

            private void SpawnShadowHallucination(Transform me)
            {
                try
                {
                    var cam = _cam ?? Camera.main;
                    if (cam == null) return;

                    float dist = UnityEngine.Random.Range(ShadowMinDistance, ShadowMaxDistance);
                    float yaw = UnityEngine.Random.Range(-35f, 35f);
                    Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * cam.transform.forward;
                    Vector3 pos = cam.transform.position + forward.normalized * dist;
                    pos.y = me.position.y;
                    pos += Vector3.up * 0.1f;

                    var go = new GameObject("HallucinationShadow");
                    go.hideFlags = HideFlags.DontSave;
                    go.transform.position = pos;
                    go.transform.LookAt(new Vector3(cam.transform.position.x, go.transform.position.y, cam.transform.position.z));

                    void AddPrimitive(PrimitiveType type, Vector3 localPos, Vector3 localScale)
                    {
                        var p = GameObject.CreatePrimitive(type);
                        p.transform.SetParent(go.transform, false);
                        p.transform.localPosition = localPos;
                        p.transform.localScale = localScale;
                        var rend = p.GetComponent<Renderer>();
                        if (rend != null)
                        {
                            rend.material = new Material(Shader.Find("Standard"));
                            rend.material.SetColor("_Color", new Color(0f, 0f, 0f, 1f));
                            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                            rend.receiveShadows = false;
                            rend.material.EnableKeyword("_EMISSION");
                            rend.material.SetColor("_EmissionColor", Color.black);
                        }
                        var col = p.GetComponent<Collider>();
                        if (col != null) col.enabled = false;
                    }

                    AddPrimitive(PrimitiveType.Capsule, new Vector3(0, 1.0f, 0), new Vector3(0.35f, 1.5f, 0.35f));
                    AddPrimitive(PrimitiveType.Sphere, new Vector3(0, 2.3f, 0), new Vector3(0.55f, 0.55f, 0.55f));
                    AddPrimitive(PrimitiveType.Cube, new Vector3(0.55f, 1.2f, 0), new Vector3(0.6f, 0.15f, 0.15f));
                    AddPrimitive(PrimitiveType.Cube, new Vector3(-0.55f, 1.2f, 0), new Vector3(0.6f, 0.15f, 0.15f));

                    var fader = go.AddComponent<ShadowFader>();
                    fader.Lifetime = ShadowLifetime;
                }
                catch (Exception e)
                {
                    InsanityMod.LogS?.LogDebug($"Hallucination spawn failed: {e}");
                }
            }

            private void SpawnPeripheryFlicker()
            {
                try
                {
                    var cam = _cam ?? Camera.main;
                    if (cam == null) return;

                    float side = UnityEngine.Random.value < 0.5f ? -1f : 1f;
                    float dist = UnityEngine.Random.Range(3f, 6f);
                    Vector3 right = cam.transform.right * side;
                    Vector3 pos = cam.transform.position + cam.transform.forward * dist + right * UnityEngine.Random.Range(2f, 3.5f);
                    var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    go.name = "HallucinationPeriphery";
                    go.hideFlags = HideFlags.DontSave;
                    go.transform.position = pos;
                    go.transform.rotation = Quaternion.LookRotation((cam.transform.position - pos).normalized, Vector3.up);
                    var rend = go.GetComponent<Renderer>();
                    if (rend != null)
                    {
                        rend.material = new Material(Shader.Find("Unlit/Color"));
                        rend.material.color = new Color(0f, 0f, 0f, 0.85f);
                    }
                    var col = go.GetComponent<Collider>(); if (col != null) col.enabled = false;

                    var fader = go.AddComponent<ShadowFader>();
                    fader.Lifetime = UnityEngine.Random.Range(0.15f, 0.35f);
                }
                catch { }
            }

            private void SpawnFlashLight(Vector3 me)
            {
                try
                {
                    var cam = _cam ?? Camera.main;
                    if (cam == null) return;
                    Vector3 dir = Quaternion.Euler(0f, UnityEngine.Random.Range(-30f, 30f), 0f) * cam.transform.forward;
                    Vector3 pos = cam.transform.position + dir * UnityEngine.Random.Range(4f, 8f);
                    pos.y = me.y + UnityEngine.Random.Range(0.5f, 1.6f);

                    var go = new GameObject("HallucinationFlashLight");
                    go.hideFlags = HideFlags.DontSave;
                    var light = go.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.range = UnityEngine.Random.Range(4f, 7f);
                    light.intensity = 0f;
                    light.color = new Color(1f, UnityEngine.Random.Range(0.1f, 0.4f), 0.1f);

                    go.transform.position = pos;

                    go.AddComponent<FlashLightFader>().Init(0.12f, 0.4f);
                }
                catch { }
            }

            private IEnumerator CameraRollNudge()
            {
                var cam = _cam ?? Camera.main; if (cam == null) yield break;
                float dur = 0.25f;
                float half = dur * 0.5f;
                float t = 0f;
                float roll = UnityEngine.Random.Range(-3.5f, 3.5f);
                var start = cam.transform.localEulerAngles;
                while (t < dur)
                {
                    t += Time.deltaTime;
                    float n = t < half ? (t / half) : (1f - (t - half) / half);
                    var e = start; e.z = start.z + roll * n;
                    cam.transform.localEulerAngles = e;
                    yield return null;
                }
                cam.transform.localEulerAngles = start;
            }

            private sealed class ShadowFader : MonoBehaviour
            {
                public float Lifetime = 2f;
                private float _t;
                private readonly List<Renderer> _renders = new List<Renderer>();

                private void Start()
                {
                    GetComponentsInChildren<Renderer>(_renders);
                }

                private void Update()
                {
                    _t += Time.deltaTime;
                    float n = Mathf.Clamp01(_t / Lifetime);
                    float a = Mathf.SmoothStep(1f, 0f, n);

                    foreach (var r in _renders)
                    {
                        if (r == null || r.material == null) continue;
                        if (r.material.HasProperty("_Color"))
                        {
                            var c = r.material.color;
                            c.a = a;
                            r.material.color = c;
                        }
                    }

                    transform.position += new Vector3((UnityEngine.Random.value - 0.5f) * 0.01f, 0f, (UnityEngine.Random.value - 0.5f) * 0.01f);

                    if (_t >= Lifetime)
                        Destroy(gameObject);
                }
            }

            private sealed class FlashLightFader : MonoBehaviour
            {
                private float _rise;
                private float _fall;
                private float _t;
                private Light _l;

                public void Init(float rise, float fall)
                {
                    _rise = Mathf.Max(0.01f, rise);
                    _fall = Mathf.Max(0.01f, fall);
                }

                private void Awake() { _l = GetComponent<Light>(); }

                private void Update()
                {
                    _t += Time.deltaTime;
                    if (_l == null) { Destroy(gameObject); return; }

                    if (_t <= _rise)
                    {
                        _l.intensity = Mathf.Lerp(0f, 3.2f, _t / _rise);
                    }
                    else
                    {
                        float d = (_t - _rise) / _fall;
                        _l.intensity = Mathf.Lerp(3.2f, 0f, d);
                    }

                    if (_t >= _rise + _fall)
                        Destroy(gameObject);
                }
            }
        }
    }

    internal sealed class SanityUIBootstrap : MonoBehaviour
    {
        private bool _spawned;
        private GameObject _uiRoot;
        private RectTransform _lastParent;

        private void Awake()
        {
            DontDestroyOnLoad(this);
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            StartCoroutine(Watcher());
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            KillUI();
            _spawned = false;
        }

        private IEnumerator Watcher()
        {
            var wait = new WaitForSeconds(0.5f);
            while (true)
            {
                TryEnsureUI();
                yield return wait;
            }
        }

        private void TryEnsureUI()
        {
            if (IsInMenuOrNotReady())
            {
                if (_spawned) { KillUI(); _spawned = false; }
                return;
            }

            if (InsanityMod.InsanityController.Instance == null) return;

            var hud = HUDCanvas.instance;
            if (hud == null) return;
            var parent = hud.GetComponent<RectTransform>();
            if (parent == null) return;

            if (_uiRoot != null)
            {
                if (_uiRoot.transform == null)
                {
                    _uiRoot = null;
                    _spawned = false;
                }
                else if (_uiRoot.transform.parent != parent)
                {
                    _uiRoot.transform.SetParent(parent, false);
                    _lastParent = parent;
                    return;
                }
            }

            if (_spawned && _uiRoot != null) return;

            _uiRoot = CreateSanityUI(parent);
            _lastParent = parent;
            _spawned = _uiRoot != null;
        }

        private bool IsInMenuOrNotReady()
        {
            try { if (SemiFunc.MenuLevel()) return true; } catch { }
            if (LevelGenerator.Instance == null) return true;
            if (GameDirector.instance == null) return true;
            if (GameDirector.instance.currentState != GameDirector.gameState.Main) return true;
            return false;
        }

        private void KillUI()
        {
            try { if (_uiRoot != null) UnityEngine.Object.Destroy(_uiRoot); } catch { }
            _uiRoot = null;
            _lastParent = null;
        }

        private GameObject CreateSanityUI(RectTransform parent)
        {
            var go = new GameObject("SanityUI", typeof(RectTransform), typeof(CanvasGroup));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;

            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(InsanityMod.CfgUI_PosX.Value, InsanityMod.CfgUI_PosY.Value);
            rt.sizeDelta = new Vector2(InsanityMod.CfgUI_Width.Value, InsanityMod.CfgUI_Height.Value);

            var group = go.GetComponent<CanvasGroup>();
            group.alpha = 1f;
            group.interactable = false;
            group.blocksRaycasts = false;

            var bg = new GameObject("BG", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;
            var bgImg = bg.GetComponent<Image>();
            bgImg.raycastTarget = false;
            bgImg.color = new Color(0f, 0f, 0f, 0.45f);

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(go.transform, false);
            var fillRt = (RectTransform)fill.transform;
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(1f, 1f);
            fillRt.offsetMin = new Vector2(2f, 2f);
            fillRt.offsetMax = new Vector2(-2f, -2f);
            var fillImg = fill.GetComponent<Image>();
            fillImg.raycastTarget = false;
            fillImg.type = Image.Type.Filled;
            fillImg.fillMethod = Image.FillMethod.Horizontal;
            fillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImg.fillAmount = 0f;
            fillImg.color = new Color(0.2f, 0.95f, 0.2f, 0.95f);

            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            labelGo.transform.SetParent(go.transform, false);
            var labelRt = (RectTransform)labelGo.transform;
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(0f, 1f);
            labelRt.pivot = new Vector2(0f, 0.5f);
            labelRt.sizeDelta = new Vector2(90f, 0f);
            labelRt.anchoredPosition = new Vector2(-95f, 0f);
            var label = labelGo.GetComponent<TextMeshProUGUI>();
            label.raycastTarget = false;
            label.fontSize = 14f;
            label.text = "SANITY";
            label.alignment = TextAlignmentOptions.MidlineRight;
            label.color = Color.white;

            var pctGo = new GameObject("Percent", typeof(RectTransform), typeof(TextMeshProUGUI));
            pctGo.transform.SetParent(go.transform, false);
            var pctRt = (RectTransform)pctGo.transform;
            pctRt.anchorMin = new Vector2(1f, 0f);
            pctRt.anchorMax = new Vector2(1f, 1f);
            pctRt.pivot = new Vector2(1f, 0.5f);
            pctRt.sizeDelta = new Vector2(40f, 0f);
            pctRt.anchoredPosition = new Vector2(50f, 0f);
            var pct = pctGo.GetComponent<TextMeshProUGUI>();
            pct.raycastTarget = false;
            pct.fontSize = 12f;
            pct.alignment = TextAlignmentOptions.MidlineLeft;
            pct.color = Color.white;

            var driver = go.AddComponent<SanityBarUI>();
            driver.group = group;
            driver.fill = fillImg;
            driver.percent = pct;

            return go;
        }
    }

    internal sealed class SanityBarUI : MonoBehaviour
    {
        public CanvasGroup group;
        public Image fill;
        public TextMeshProUGUI percent;

        private float _vis;
        private RectTransform _rt;
        private float _pulsePhase;

        private void Awake()
        {
            _rt = GetComponent<RectTransform>();
        }

        private void Update()
        {
            var ctrl = InsanityMod.InsanityController.Instance;
            if (ctrl == null || fill == null || percent == null)
                return;

            float n = Mathf.Clamp01(ctrl.NormalizedInsanity);
            fill.fillAmount = n;
            percent.text = Mathf.RoundToInt(ctrl.CurrentInsanity).ToString();

            Color c1 = Color.Lerp(new Color(0.2f, 0.95f, 0.2f), new Color(1.0f, 0.86f, 0.1f), Mathf.SmoothStep(0f, 1f, n * 1.2f));
            Color c2 = Color.Lerp(c1, new Color(0.95f, 0.15f, 0.15f), Mathf.SmoothStep(0f, 1f, Mathf.Max(0f, n - 0.5f) * 2f));
            fill.color = c2;

            float pressure = Mathf.Clamp01(ctrl.EnemyPressure01);
            _pulsePhase += Time.deltaTime * Mathf.Lerp(1.0f, 6.0f, pressure);
            float sx = 1f + Mathf.Sin(_pulsePhase) * 0.03f * n;
            if (_rt != null) _rt.localScale = new Vector3(sx, 1f, 1f);

            bool hudHidden = HUD.instance != null && HUD.instance.hidden;
            float target = hudHidden ? 0f : 1f;

            if (!hudHidden && n <= 0.001f) target = 0.35f;

            _vis = Mathf.MoveTowards(_vis, target, Time.deltaTime * 6f);
            if (group != null) group.alpha = _vis;
        }
    }
}