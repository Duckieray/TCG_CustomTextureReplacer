using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;

namespace CustomTextureReplacer
{
    [BepInPlugin("com.duckieray.cardshop.customtextures", "Custom Texture Replacer", "1.3.0")]
    public class CustomTextureReplacer : BaseUnityPlugin
    {
        private void Awake()
        {
            if (ReplacerController.Instance != null)
            {
                Logger.LogWarning("[CustomTextureReplacer] Controller already initialised");
                Destroy(this);
                return;
            }

            var controllerGO = new GameObject("CustomTextureReplacerController")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            DontDestroyOnLoad(controllerGO);

            var controller = controllerGO.AddComponent<ReplacerController>();
            controller.Initialise(this.Logger, this.Config);

            Logger.LogInfo("[CustomTextureReplacer] Controller created.");
        }
    }

    internal class ReplacerController : MonoBehaviour
    {
        internal static ReplacerController Instance { get; private set; }

        private const string HarmonyId = "com.duckieray.cardshop.customtextures.harmony";
        private const float ScanIntervalSeconds = 2f;

        private readonly Dictionary<string, Texture2D> _customTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _knownTextureIds = new HashSet<int>();
        private readonly HashSet<int> _collectionScratch = new HashSet<int>();
        private readonly List<Texture2D> _textureBuffer = new List<Texture2D>(512);
        private readonly List<string> _newTextureNames = new List<string>(64);

        private Sprite[] _spriteArray = Array.Empty<Sprite>();

        private ManualLogSource _logger;
        private ConfigEntry<bool> _logNewTextureNames;
        private ConfigEntry<bool> _logAssetLoads;

        private string _textureFolder = string.Empty;
        private string _dumpFile = string.Empty;
        private string _dumpTriggerFile = string.Empty;
        private string _refreshTriggerFile = string.Empty;
        private string _debugLogFile = string.Empty;

        private FileSystemWatcher _watcher;
        private Harmony _harmony;

        private volatile bool _reloadRequested;
        private bool _hasDumpedAfterSceneLoad;
        private bool _pendingDump;
        private bool _reapplyRequested;
        private bool _loggedHeartbeat;
        private float _nextScanTime;

        internal void Initialise(ManualLogSource logger, ConfigFile config)
        {
            if (Instance != null && Instance != this)
            {
                logger.LogWarning("[CustomTextureReplacer] Duplicate controller detected, destroying new instance.");
                Destroy(gameObject);
                return;
            }

            Instance = this;
            _logger = logger;

            _logNewTextureNames = config.Bind("Debug", "LogNewTextureNames", true, "Log the names of textures discovered during runtime scans.");
            _logAssetLoads = config.Bind("Debug", "LogAssetLoads", true, "Log texture and sprite loads coming from Resources/AssetBundle APIs.");

            _textureFolder = Path.Combine(Paths.PluginPath, "CustomTextures");
            Directory.CreateDirectory(_textureFolder);

            _dumpFile = Path.Combine(Paths.PluginPath, "TexturesList.txt");
            _dumpTriggerFile = Path.Combine(Paths.PluginPath, "CustomTextures.dump.now");
            _refreshTriggerFile = Path.Combine(Paths.PluginPath, "CustomTextures.refresh.now");
            _debugLogFile = Path.Combine(Paths.PluginPath, "CustomTextureReplacer.debug.log");

            SafeAppendDebug("Controller initialised.");

            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(ReplacerController).Assembly);

            SceneManager.sceneLoaded += OnSceneLoaded;
            InitialiseWatcher();

            ReloadCustomTextures();
            StartCoroutine(InitialDump());

            _nextScanTime = Time.realtimeSinceStartup + ScanIntervalSeconds;
        }

        private void OnDestroy()
        {
            SafeAppendDebug("Controller OnDestroy.");

            SceneManager.sceneLoaded -= OnSceneLoaded;

            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnTextureFileChanged;
                    _watcher.Changed -= OnTextureFileChanged;
                    _watcher.Deleted -= OnTextureFileChanged;
                    _watcher.Renamed -= OnTextureFileRenamed;
                    _watcher.Dispose();
                }
                catch { }
                _watcher = null;
            }

            if (_harmony != null)
            {
                try
                {
                    _harmony.UnpatchSelf();
                }
                catch { }
                _harmony = null;
            }

            foreach (var tex in _customTextures.Values)
            {
                if (tex != null)
                    Destroy(tex);
            }

            _customTextures.Clear();
            _knownTextureIds.Clear();
            _textureBuffer.Clear();
            Instance = null;
        }

        private void Update()
        {
            if (!_loggedHeartbeat)
            {
                _loggedHeartbeat = true;
                _logger.LogInfo("[CustomTextureReplacer] Update loop active. Use F8 or create 'CustomTextures.dump.now' to trigger dumps.");
                SafeAppendDebug("Update heartbeat.");
            }

            if (CheckAndConsumeFileTrigger(_dumpTriggerFile))
            {
                _logger.LogInfo($"[CustomTextureReplacer] Manual dump triggered via '{Path.GetFileName(_dumpTriggerFile)}'.");
                SafeAppendDebug("Dump trigger consumed.");
                DumpAllTextures();
            }

            if (CheckAndConsumeFileTrigger(_refreshTriggerFile))
            {
                _logger.LogInfo($"[CustomTextureReplacer] Manual refresh triggered via '{Path.GetFileName(_refreshTriggerFile)}'.");
                SafeAppendDebug("Refresh trigger consumed.");
                ReloadCustomTextures();
            }

            if (_reloadRequested)
            {
                _reloadRequested = false;
                SafeAppendDebug("Reload flag consumed.");
                ReloadCustomTextures();
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                _logger.LogInfo("[CustomTextureReplacer] Manual dump triggered (F8).");
                SafeAppendDebug("F8 pressed");
                DumpAllTextures();
            }

            if (Time.realtimeSinceStartup >= _nextScanTime)
            {
                _nextScanTime = Time.realtimeSinceStartup + ScanIntervalSeconds;
                if (DetectNewTextures())
                {
                    SafeAppendDebug("Periodic scan detected new textures.");
                    RequestReapply("PeriodicScan");
                }
            }

            if (_reapplyRequested)
            {
                _reapplyRequested = false;
                SafeAppendDebug("Reapply flag consumed.");
                ReplaceAllTextures();
            }

            if (_pendingDump)
            {
                _pendingDump = false;
                SafeAppendDebug("Pending dump executed.");
                DumpAllTextures();
            }
        }

        private void InitialiseWatcher()
        {
            _watcher = new FileSystemWatcher(_textureFolder, "*.png")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnTextureFileChanged;
            _watcher.Changed += OnTextureFileChanged;
            _watcher.Deleted += OnTextureFileChanged;
            _watcher.Renamed += OnTextureFileRenamed;
            SafeAppendDebug("FileSystemWatcher initialised.");
        }

        private void OnTextureFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!IsPng(e.FullPath))
                return;

            _logger.LogInfo($"[CustomTextureReplacer] Detected change for '{e.Name}'. Reloading custom textures.");
            SafeAppendDebug($"File change detected: {e.Name}");
            _reloadRequested = true;
        }

        private void OnTextureFileRenamed(object sender, RenamedEventArgs e)
        {
            if (!IsPng(e.FullPath) && !IsPng(e.OldFullPath))
                return;

            _logger.LogInfo($"[CustomTextureReplacer] Detected rename from '{e.OldName}' to '{e.Name}'. Reloading custom textures.");
            SafeAppendDebug($"File rename detected: {e.OldName} -> {e.Name}");
            _reloadRequested = true;
        }

        private static bool IsPng(string path)
        {
            return string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SafeAppendDebug($"Scene loaded: {scene.name}");
            _hasDumpedAfterSceneLoad = false;
            StartCoroutine(ApplyReplacementsNextFrame(scene.name));
        }

        private IEnumerator InitialDump()
        {
            yield return new WaitForSecondsRealtime(1f);
            _logger.LogInfo("[CustomTextureReplacer] Performing initial texture dump.");
            SafeAppendDebug("Initial dump coroutine running.");
            DumpAllTextures();
        }

        private IEnumerator ApplyReplacementsNextFrame(string sceneName)
        {
            yield return null;
            yield return null;

            _logger.LogInfo($"[CustomTextureReplacer] Scene '{sceneName}' loaded. Reapplying replacements.");
            SafeAppendDebug($"ApplyReplacementsNextFrame for {sceneName}");
            ReplaceAllTextures();

            if (!_hasDumpedAfterSceneLoad)
            {
                _hasDumpedAfterSceneLoad = true;
                SafeAppendDebug("Dumping after scene load.");
                DumpAllTextures();
            }
        }

        private void ReloadCustomTextures()
        {
            foreach (var tex in _customTextures.Values)
            {
                if (tex != null)
                    Destroy(tex);
            }

            _customTextures.Clear();

            foreach (var file in Directory.GetFiles(_textureFolder, "*.png", SearchOption.TopDirectoryOnly))
            {
                if (TryLoadTexture(file, out var texture))
                {
                    _customTextures[Path.GetFileNameWithoutExtension(file)] = texture;
                }
            }

            _logger.LogInfo($"[CustomTextureReplacer] Loaded {_customTextures.Count} custom textures from disk.");
            SafeAppendDebug($"ReloadCustomTextures complete: {_customTextures.Count} textures");
            RequestReapply("CustomTexturesReloaded");
        }

        private bool TryLoadTexture(string path, out Texture2D texture)
        {
            texture = null;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                var data = ms.ToArray();

                var newTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(newTexture, data, markNonReadable: false))
                {
                    Destroy(newTexture);
                    _logger.LogWarning($"[CustomTextureReplacer] Failed to decode '{path}'.");
                    SafeAppendDebug($"Failed to decode {Path.GetFileName(path)}");
                    return false;
                }

                newTexture.name = Path.GetFileNameWithoutExtension(path);
                newTexture.wrapMode = TextureWrapMode.Clamp;

                texture = newTexture;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Could not load '{path}': {ex.Message}");
                SafeAppendDebug($"Exception loading {Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
        }

        private bool DetectNewTextures()
        {
            CollectCandidateTextures(_textureBuffer);

            var foundNew = false;
            _newTextureNames.Clear();

            foreach (var tex in _textureBuffer)
            {
                if (tex == null)
                    continue;

                if (_knownTextureIds.Add(tex.GetInstanceID()))
                {
                    foundNew = true;

                    if (_logNewTextureNames.Value)
                    {
                        _newTextureNames.Add($"{tex.name} ({tex.width}x{tex.height})");
                    }
                }
            }

            if (foundNew && _logNewTextureNames.Value && _newTextureNames.Count > 0)
            {
                var preview = string.Join(", ", _newTextureNames.Take(10));
                if (_newTextureNames.Count > 10)
                    preview += ", ...";

                _logger.LogInfo($"[CustomTextureReplacer] Detected {_newTextureNames.Count} new textures: {preview}");
                SafeAppendDebug($"Detected {_newTextureNames.Count} new textures");
            }

            return foundNew;
        }

        private void ReplaceAllTextures()
        {
            if (_customTextures.Count == 0)
            {
                SafeAppendDebug("ReplaceAllTextures aborted (no custom textures)");
                return;
            }

            CollectCandidateTextures(_textureBuffer);

            var replaced = 0;
            var skipped = 0;

            foreach (var target in _textureBuffer)
            {
                if (target == null)
                    continue;

                if (!_customTextures.TryGetValue(target.name, out var replacement))
                    continue;

                if (replacement.width != target.width || replacement.height != target.height)
                {
                    try
                    {
                        // Resize replacement to target's dimensions using a RenderTexture
                        RenderTexture rt = RenderTexture.GetTemporary(target.width, target.height);
                        Graphics.Blit(replacement, rt);

                        RenderTexture.active = rt;
                        Texture2D resized = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                        resized.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                        resized.Apply();

                        RenderTexture.active = null;
                        RenderTexture.ReleaseTemporary(rt);

                        Graphics.CopyTexture(resized, target);
                        Destroy(resized);

                        replaced++;
                        _logger.LogInfo($"[CustomTextureReplacer] Resized + replaced '{target.name}' " +
                                        $"(custom {replacement.width}x{replacement.height} → game {target.width}x{target.height}).");
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        _logger.LogWarning($"[CustomTextureReplacer] Resize failed for '{target.name}': {ex.Message}");
                    }

                    continue;
                }

                try
                {
                    Graphics.CopyTexture(replacement, target);
                    replaced++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    _logger.LogWarning($"[CustomTextureReplacer] Failed to swap '{target.name}': {ex.Message}");
                    SafeAppendDebug($"Graphics.CopyTexture failed for {target.name}: {ex.Message}");
                }
            }

            if (replaced > 0 || skipped > 0)
            {
                _logger.LogInfo($"[CustomTextureReplacer] Applied replacements. Success: {replaced}, skipped: {skipped}.");
                SafeAppendDebug($"ReplaceAllTextures finished. Success={replaced} Skipped={skipped}");
            }
        }

        private void DumpAllTextures()
        {
            try
            {
                CollectCandidateTextures(_textureBuffer);
                _textureBuffer.Sort((a, b) => string.Compare(a?.name, b?.name, StringComparison.OrdinalIgnoreCase));

                _knownTextureIds.Clear();

                using var writer = new StreamWriter(_dumpFile, false);
                foreach (var tex in _textureBuffer)
                {
                    if (tex == null)
                        continue;

                    _knownTextureIds.Add(tex.GetInstanceID());
                    var line = $"{tex.name} ({tex.width}x{tex.height})";
                    writer.WriteLine(line);
                }

                _logger.LogInfo($"[CustomTextureReplacer] Dumped texture list to '{_dumpFile}' ({_knownTextureIds.Count} entries).");
                SafeAppendDebug($"DumpAllTextures wrote {_knownTextureIds.Count} entries");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Unable to dump textures: {ex.Message}");
                SafeAppendDebug($"DumpAllTextures exception: {ex.Message}");
            }
        }

        private void CollectCandidateTextures(List<Texture2D> destination)
        {
            destination.Clear();
            _collectionScratch.Clear();

            void TryAdd(Texture2D tex)
            {
                if (tex == null)
                    return;

                var id = tex.GetInstanceID();
                if (_collectionScratch.Add(id))
                    destination.Add(tex);
            }

            foreach (var tex in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                TryAdd(tex);
            }

            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                TryAdd(sprite?.texture);
            }

            try
            {
                foreach (var atlas in Resources.FindObjectsOfTypeAll<SpriteAtlas>())
                {
                    if (atlas == null)
                        continue;

                    var spriteCount = atlas.spriteCount;
                    if (spriteCount <= 0)
                        continue;

                    if (_spriteArray.Length < spriteCount)
                    {
                        Array.Resize(ref _spriteArray, spriteCount);
                    }

                    var received = atlas.GetSprites(_spriteArray);
                    for (var i = 0; i < received; i++)
                    {
                        TryAdd(_spriteArray[i]?.texture);
                    }
                }
            }
            catch (Exception ex)
            {
                SafeAppendDebug($"SpriteAtlas enumeration failed: {ex.Message}");
            }

            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null)
                    continue;

                foreach (var propertyName in material.GetTexturePropertyNames())
                {
                    if (material.GetTexture(propertyName) is Texture2D tex)
                    {
                        TryAdd(tex);
                    }
                }
            }
        }

        private void RequestReapply(string reason)
        {
            _reapplyRequested = true;
            _pendingDump = true;
            _logger.LogInfo($"[CustomTextureReplacer] Scheduled refresh due to {reason}.");
            SafeAppendDebug($"RequestReapply({reason})");
        }

        private bool CheckAndConsumeFileTrigger(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                if (!File.Exists(path))
                    return false;

                File.Delete(path);
                SafeAppendDebug($"Trigger consumed: {Path.GetFileName(path)}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Could not consume trigger '{path}': {ex.Message}");
                SafeAppendDebug($"Trigger error ({Path.GetFileName(path)}): {ex.Message}");
                return false;
            }
        }

        internal void HandleAssetLoad(UnityEngine.Object asset, string context, string detail)
        {
            if (asset == null)
                return;

            if (asset is Texture2D tex)
            {
                if (_logAssetLoads.Value)
                {
                    _logger.LogInfo($"[CustomTextureReplacer] {context}: Texture '{tex.name}' ({tex.width}x{tex.height}) {detail}");
                }

                SafeAppendDebug($"Asset load ({context}) texture {tex.name}");
                RequestReapply(context);
            }
            else if (asset is Sprite sprite)
            {
                if (_logAssetLoads.Value)
                {
                    _logger.LogInfo($"[CustomTextureReplacer] {context}: Sprite '{sprite.name}' -> texture '{sprite.texture?.name}' {detail}");
                }

                SafeAppendDebug($"Asset load ({context}) sprite {sprite.name}");
                RequestReapply(context);
            }
        }

        internal void HandleAssetLoad(IEnumerable<UnityEngine.Object> assets, string context)
        {
            if (assets == null)
                return;

            foreach (var asset in assets)
            {
                HandleAssetLoad(asset, context, string.Empty);
            }
        }

        private void SafeAppendDebug(string message)
        {
            if (string.IsNullOrEmpty(_debugLogFile))
                return;

            try
            {
                File.AppendAllText(_debugLogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch
            {
                // ignore file IO errors
            }
        }
    }

    internal static class ResourceLoadPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Resources), nameof(Resources.Load), new[] { typeof(string), typeof(Type) })]
        private static void ResourcesLoad(string path, Type systemTypeInstance, UnityEngine.Object __result)
        {
            ReplacerController.Instance?.HandleAssetLoad(__result, "Resources.Load", $"path '{path}', type '{systemTypeInstance?.Name}'");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Resources), nameof(Resources.LoadAll), new[] { typeof(string), typeof(Type) })]
        private static void ResourcesLoadAll(string path, Type systemTypeInstance, UnityEngine.Object[] __result)
        {
            if (__result == null)
                return;

            ReplacerController.Instance?.HandleAssetLoad(__result, "Resources.LoadAll");
        }
    }

    internal static class AssetBundleLoadPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAsset), new[] { typeof(string), typeof(Type) })]
        private static void LoadAsset(AssetBundle __instance, string name, Type type, UnityEngine.Object __result)
        {
            ReplacerController.Instance?.HandleAssetLoad(__result, "AssetBundle.LoadAsset", $"name '{name}', type '{type?.Name}' from bundle '{__instance?.name}'");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAllAssets), new[] { typeof(Type) })]
        private static void LoadAllAssets(AssetBundle __instance, Type type, UnityEngine.Object[] __result)
        {
            if (__result == null)
                return;

            ReplacerController.Instance?.HandleAssetLoad(__result, $"AssetBundle.LoadAllAssets({type?.Name})");
        }
    }
}


