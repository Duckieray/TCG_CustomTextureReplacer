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
using UnityEngine.Experimental.Rendering;
using System.Reflection;
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

        private enum FolderPriorityMode
        {
            LastModified,
            PreferredFolder,
            FolderOrder
        }

        private struct TextureCandidate
        {
            public string Path;
            public DateTime TimestampUtc;
            public int FolderIndex;
            public long EventOrder;
        }

        private const string HarmonyId = "com.duckieray.cardshop.customtextures.harmony";
        private const float ScanIntervalSeconds = 2f;
        private static readonly Type UIImageType = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI");
        private static readonly PropertyInfo UIImageSpriteProperty = UIImageType?.GetProperty("sprite", BindingFlags.Instance | BindingFlags.Public);

        private static readonly string[] RendererTextureProperties = new[] { "_MainTex", "_BaseMap", "_BaseColorMap", "_BaseTex", "_DiffuseTex", "_EmissionMap", "_AlbedoTex" };

        private readonly Dictionary<string, Texture2D> _customTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _knownTextureIds = new HashSet<int>();
        private readonly HashSet<int> _customTextureIds = new HashSet<int>();
        private readonly HashSet<int> _collectionScratch = new HashSet<int>();
        private readonly HashSet<int> _spriteScratch = new HashSet<int>();
        private readonly List<Texture2D> _textureBuffer = new List<Texture2D>(512);
        private readonly List<Sprite> _spriteBuffer = new List<Sprite>(512);
        private readonly List<string> _newTextureNames = new List<string>(64);
        private readonly Dictionary<Texture, Texture> _textureOverrides = new Dictionary<Texture, Texture>();
        private readonly Dictionary<string, Texture> _textureOverridesByName = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Sprite, Sprite> _spriteOverrides = new Dictionary<Sprite, Sprite>();
        private readonly Dictionary<string, Sprite> _spriteOverridesByName = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Sprite, Texture2D> _spriteOverrideTextures = new Dictionary<Sprite, Texture2D>();
        private readonly HashSet<Texture2D> _generatedTextures = new HashSet<Texture2D>();
        private readonly HashSet<Sprite> _generatedSprites = new HashSet<Sprite>();
        private bool _overridesDirty;
        private readonly MaterialPropertyBlock _propertyBlock = new MaterialPropertyBlock();
        private readonly Dictionary<string, DateTime> _fileEventTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _fileEventOrders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private long _fileEventCounter;

        private Sprite[] _spriteArray = Array.Empty<Sprite>();

        private ManualLogSource _logger;
        private ConfigEntry<bool> _logNewTextureNames;
        private ConfigEntry<bool> _logAssetLoads;

        private readonly List<string> _textureFolders = new List<string>();
        private string _dumpFile = string.Empty;
        private string _dumpTriggerFile = string.Empty;
        private string _refreshTriggerFile = string.Empty;
        private string _spriteDumpFile = string.Empty;
        private string _spriteDumpTriggerFile = string.Empty;
        private string _debugLogFile = string.Empty;
        private string _exportFolder = string.Empty;

        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private ConfigEntry<FolderPriorityMode> _folderPriorityMode;
        private ConfigEntry<string> _preferredFolderHint;
        private string[] _preferredFolderTokens = Array.Empty<string>();
        private static readonly char[] PreferredFolderSeparators = new[] { ';', ',', '|' };

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
            _folderPriorityMode = config.Bind("General", "FolderPriorityMode", FolderPriorityMode.LastModified, "Determines which texture file wins when duplicates exist across folders. Options: LastModified, PreferredFolder, FolderOrder.");
            _preferredFolderHint = config.Bind("General", "PreferredFolderHint", string.Empty, "When FolderPriorityMode=PreferredFolder, provide folder names or path fragments (separated by ';' ',' or '|') to prioritise.");

            UpdatePreferredFolderTokens();

            _folderPriorityMode.SettingChanged += (_, __) =>
            {
                SafeAppendDebug("FolderPriorityMode changed via config.");
                _reloadRequested = true;
            };

            _preferredFolderHint.SettingChanged += (_, __) =>
            {
                UpdatePreferredFolderTokens();
                SafeAppendDebug("PreferredFolderHint changed via config.");
                _reloadRequested = true;
            };

            DiscoverTextureFolders(logDetails: true);

            _dumpFile = Path.Combine(Paths.PluginPath, "TexturesList.txt");
            _dumpTriggerFile = Path.Combine(Paths.PluginPath, "CustomTextures.dump.now");
            _refreshTriggerFile = Path.Combine(Paths.PluginPath, "CustomTextures.refresh.now");
            _spriteDumpFile = Path.Combine(Paths.PluginPath, "SpritesList.txt");
            _spriteDumpTriggerFile = Path.Combine(Paths.PluginPath, "SpritesList.dump.now");
            _debugLogFile = Path.Combine(Paths.PluginPath, "CustomTextureReplacer.debug.log");
            _exportFolder = Path.Combine(Paths.PluginPath, "ExportedTextures");
            Directory.CreateDirectory(_exportFolder);

            SafeAppendDebug("Controller initialised.");

            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(ReplacerController).Assembly);

            SceneManager.sceneLoaded += OnSceneLoaded;
            ReloadCustomTextures();
            StartCoroutine(InitialDump());

            _nextScanTime = Time.realtimeSinceStartup + ScanIntervalSeconds;
        }

        private void OnDestroy()
        {
            SafeAppendDebug("Controller OnDestroy.");

            SceneManager.sceneLoaded -= OnSceneLoaded;

            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= OnTextureFileChanged;
                    watcher.Changed -= OnTextureFileChanged;
                    watcher.Deleted -= OnTextureFileChanged;
                    watcher.Renamed -= OnTextureFileRenamed;
                    watcher.Dispose();
                }
                catch { }
            }
            _watchers.Clear();

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
            _customTextureIds.Clear();
            _knownTextureIds.Clear();
            _textureBuffer.Clear();
            _spriteBuffer.Clear();
            _collectionScratch.Clear();
            _spriteScratch.Clear();

            foreach (var tex in _generatedTextures)
            {
                if (tex != null)
                    Destroy(tex);
            }
            _generatedTextures.Clear();

            foreach (var sprite in _generatedSprites)
            {
                if (sprite != null)
                    Destroy(sprite);
            }
            _generatedSprites.Clear();

            _textureOverrides.Clear();
            _textureOverridesByName.Clear();
            _spriteOverrides.Clear();
            _spriteOverridesByName.Clear();
            _spriteOverrideTextures.Clear();
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
                DumpAllSprites();
            }

            if (CheckAndConsumeFileTrigger(_refreshTriggerFile))
            {
                _logger.LogInfo($"[CustomTextureReplacer] Manual refresh triggered via '{Path.GetFileName(_refreshTriggerFile)}'.");
                SafeAppendDebug("Refresh trigger consumed.");
                ReloadCustomTextures();
            }

            if (CheckAndConsumeFileTrigger(_spriteDumpTriggerFile))
            {
                _logger.LogInfo($"[CustomTextureReplacer] Manual sprite dump triggered via '{Path.GetFileName(_spriteDumpTriggerFile)}'.");
                SafeAppendDebug("Sprite dump trigger consumed.");
                DumpAllSprites();
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
                DumpAllSprites();
            }

            ProcessExtractionRequests();

            if (_overridesDirty)
            {
                _overridesDirty = false;
                ApplyTextureOverridesToMaterials();
                ApplyTextureOverridesToRenderers();
                ApplySpriteOverridesToComponents();
            }
        }

        private void RefreshWatchers()
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= OnTextureFileChanged;
                    watcher.Changed -= OnTextureFileChanged;
                    watcher.Deleted -= OnTextureFileChanged;
                    watcher.Renamed -= OnTextureFileRenamed;
                    watcher.Dispose();
                }
                catch { }
            }

            _watchers.Clear();

            foreach (var folder in _textureFolders)
            {
                try
                {
                    Directory.CreateDirectory(folder);

                    var watcher = new FileSystemWatcher(folder, "*.png")
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true
                    };

                    watcher.Created += OnTextureFileChanged;
                    watcher.Changed += OnTextureFileChanged;
                    watcher.Deleted += OnTextureFileChanged;
                    watcher.Renamed += OnTextureFileRenamed;
                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"[CustomTextureReplacer] Could not watch '{folder}': {ex.Message}");
                }
            }

            SafeAppendDebug($"FileSystemWatcher initialised for {_textureFolders.Count} folder(s).");
        }

        private bool DiscoverTextureFolders(bool logDetails)
        {
            var discovered = new List<string>();

            try
            {
                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath;
                if (!string.IsNullOrEmpty(assemblyDir))
                {
                    TryAddTextureFolder(discovered, Path.Combine(assemblyDir, "CustomTextures"), logDetails);

                    var parent = Directory.GetParent(assemblyDir)?.FullName;
                    if (!string.IsNullOrEmpty(parent))
                    {
                        foreach (var child in Directory.GetDirectories(parent))
                        {
                            TryAddTextureFolder(discovered, Path.Combine(child, "CustomTextures"), logDetails);
                        }

                        TryAddTextureFolder(discovered, Path.Combine(parent, "CustomTextures"), logDetails);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[CustomTextureReplacer] Unable to discover texture folders: {ex.Message}");
            }

            if (discovered.Count == 0)
            {
                var fallbackRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath;
                var fallback = Path.Combine(fallbackRoot, "CustomTextures");
                Directory.CreateDirectory(fallback);
                discovered.Add(Path.GetFullPath(fallback));
                if (logDetails)
                    _logger?.LogInfo($"[CustomTextureReplacer] No texture folders found; using default: {discovered[0]}");
            }

            bool changed = !_textureFolders.SequenceEqual(discovered, StringComparer.OrdinalIgnoreCase);
            if (changed)
            {
                _textureFolders.Clear();
                _textureFolders.AddRange(discovered);

                if (!logDetails)
                {
                    foreach (var folder in _textureFolders)
                    {
                        _logger?.LogInfo($"[CustomTextureReplacer] Texture folder: {folder}");
                    }
                }
            }

            return changed;
        }

        private void UpdatePreferredFolderTokens()
        {
            var raw = _preferredFolderHint?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                _preferredFolderTokens = Array.Empty<string>();
                return;
            }

            _preferredFolderTokens = raw
                .Split(PreferredFolderSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim())
                .Where(token => token.Length > 0)
                .Select(token => token.Replace('\\', '/').ToLowerInvariant())
                .ToArray();
        }

        private bool TryAddTextureFolder(List<string> list, string candidate, bool logDetails)
        {
            if (string.IsNullOrEmpty(candidate))
                return false;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch
            {
                return false;
            }

            if (!Directory.Exists(fullPath))
                return false;

            if (list.Any(entry => string.Equals(entry, fullPath, StringComparison.OrdinalIgnoreCase)))
                return false;

            list.Add(fullPath);

            if (logDetails)
                _logger?.LogInfo($"[CustomTextureReplacer] Found texture folder: {fullPath}");

            return true;
        }

        private int GetPreferredScore(string path)
        {
            if (_preferredFolderTokens.Length == 0)
                return int.MaxValue;

            var normalised = path.Replace('\\', '/').ToLowerInvariant();
            for (int i = 0; i < _preferredFolderTokens.Length; i++)
            {
                if (normalised.Contains(_preferredFolderTokens[i]))
                    return i;
            }

            return int.MaxValue;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private void RecordFileEvent(string path)
        {
            var key = NormalizePath(path);
            if (string.IsNullOrEmpty(key))
                return;

            _fileEventCounter++;
            _fileEventOrders[key] = _fileEventCounter;

            try
            {
                if (File.Exists(key))
                {
                    _fileEventTimes[key] = DateTime.UtcNow;
                }
                else
                {
                    _fileEventTimes.Remove(key);
                }
            }
            catch
            {
                _fileEventTimes[key] = DateTime.UtcNow;
            }
        }

        private void RemoveFileEvent(string path)
        {
            var key = NormalizePath(path);
            if (string.IsNullOrEmpty(key))
                return;

            _fileEventTimes.Remove(key);
            _fileEventOrders.Remove(key);
        }

        private bool IsBetterCandidate(in TextureCandidate candidate, in TextureCandidate existing)
        {
            switch (_folderPriorityMode.Value)
            {
                case FolderPriorityMode.LastModified:
                    {
                        if (candidate.EventOrder != existing.EventOrder)
                            return candidate.EventOrder > existing.EventOrder;

                        var cmp = candidate.TimestampUtc.CompareTo(existing.TimestampUtc);
                        if (cmp != 0)
                            return cmp > 0;
                        return candidate.FolderIndex > existing.FolderIndex;
                    }
                case FolderPriorityMode.FolderOrder:
                    {
                        if (candidate.EventOrder != existing.EventOrder)
                            return candidate.EventOrder > existing.EventOrder;

                        if (candidate.FolderIndex != existing.FolderIndex)
                            return candidate.FolderIndex < existing.FolderIndex;
                        return candidate.TimestampUtc > existing.TimestampUtc;
                    }
                case FolderPriorityMode.PreferredFolder:
                    {
                        if (candidate.EventOrder != existing.EventOrder)
                            return candidate.EventOrder > existing.EventOrder;

                        var candidateScore = GetPreferredScore(candidate.Path);
                        var existingScore = GetPreferredScore(existing.Path);
                        if (candidateScore != existingScore)
                            return candidateScore < existingScore;

                        var cmp = candidate.TimestampUtc.CompareTo(existing.TimestampUtc);
                        if (cmp != 0)
                            return cmp > 0;

                        return candidate.FolderIndex > existing.FolderIndex;
                    }
                default:
                    return false;
            }
        }

        private void LoadCustomTextures()
        {
            var candidates = new Dictionary<string, TextureCandidate>(StringComparer.OrdinalIgnoreCase);

            for (int folderIndex = 0; folderIndex < _textureFolders.Count; folderIndex++)
            {
                var folder = _textureFolders[folderIndex];

                try
                {
                    if (!Directory.Exists(folder))
                        continue;

                    foreach (var file in Directory.GetFiles(folder, "*.png", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrEmpty(name))
                            continue;

                        var normalisedPath = NormalizePath(file);
                        var info = new FileInfo(normalisedPath);
                        if (!info.Exists)
                            continue;

                        var timestampTicks = Math.Max(info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
                        var timestamp = new DateTime(timestampTicks, DateTimeKind.Utc);

                        if (_fileEventTimes.TryGetValue(normalisedPath, out var eventTimestamp) && eventTimestamp > timestamp)
                        {
                            timestamp = eventTimestamp;
                        }

                        _fileEventOrders.TryGetValue(normalisedPath, out var eventOrder);

                        var candidate = new TextureCandidate
                        {
                            Path = normalisedPath,
                            TimestampUtc = timestamp,
                            FolderIndex = folderIndex,
                            EventOrder = eventOrder
                        };

                        if (candidates.TryGetValue(name, out var existing))
                        {
                            if (IsBetterCandidate(candidate, existing))
                            {
                                candidates[name] = candidate;
                            }
                        }
                        else
                        {
                            candidates[name] = candidate;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"[CustomTextureReplacer] Failed to enumerate '{folder}': {ex.Message}");
                }
            }

            foreach (var pair in candidates)
            {
                var candidate = pair.Value;
                var path = candidate.Path;
                if (TryLoadTexture(path, out var texture) && texture != null)
                {
                    _customTextures[pair.Key] = texture;
                    _customTextureIds.Add(texture.GetInstanceID());
                }

                _fileEventTimes[path] = candidate.TimestampUtc;
                if (candidate.EventOrder > 0)
                {
                    _fileEventOrders[path] = candidate.EventOrder;
                }
            }
        }

        private void OnTextureFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!IsPng(e.FullPath))
                return;

            if (e.ChangeType == WatcherChangeTypes.Deleted)
            {
                RemoveFileEvent(e.FullPath);
            }
            else
            {
                RecordFileEvent(e.FullPath);
            }

            _logger.LogInfo($"[CustomTextureReplacer] Detected change for '{e.Name}'. Reloading custom textures.");
            SafeAppendDebug($"File change detected: {e.Name}");
            _reloadRequested = true;
        }

        private void OnTextureFileRenamed(object sender, RenamedEventArgs e)
        {
            if (!IsPng(e.FullPath) && !IsPng(e.OldFullPath))
                return;

            RemoveFileEvent(e.OldFullPath);
            RecordFileEvent(e.FullPath);

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
            DumpAllSprites();
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
                {
                    _customTextureIds.Remove(tex.GetInstanceID());
                    Destroy(tex);
                }
            }

            _customTextures.Clear();
            _customTextureIds.Clear();

            bool foldersChanged = DiscoverTextureFolders(logDetails: false);
            if (foldersChanged || _watchers.Count == 0)
            {
                RefreshWatchers();
            }

            LoadCustomTextures();

            _logger.LogInfo($"[CustomTextureReplacer] Loaded {_customTextures.Count} custom textures from disk.");
            SafeAppendDebug($"ReloadCustomTextures complete: {_customTextures.Count} textures");
            RequestReapply("CustomTexturesReloaded");
            _overridesDirty = true;
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

            if (foundNew)
                _overridesDirty = true;

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

                if (ReferenceEquals(replacement, target))
                    continue;

                var resized = replacement.width != target.width || replacement.height != target.height;

                if (!TryGetSliceDimensions(target, 0, 0, target.width, target.height, out var copyWidth, out var copyHeight, out var clipped, out var reason))
                {
                    if (!string.IsNullOrEmpty(reason))
                    {
                        _logger.LogWarning(reason);
                        SafeAppendDebug(reason);
                    }

                    if (TryCreateTextureOverride(target, replacement, target.width, target.height))
                    {
                        replaced++;
                        continue;
                    }

                    skipped++;
                    continue;
                }

                if (TryBlitToTexture(replacement, target, copyWidth, copyHeight, out var blitMessage, out var usedFallback))
                {
                    replaced++;

                    if (resized)
                    {
                        _logger.LogInfo($"[CustomTextureReplacer] Resized + replaced '{target.name}' (custom {replacement.width}x{replacement.height} -> game {target.width}x{target.height}).");
                    }
                    else
                    {
                        _logger.LogInfo($"[CustomTextureReplacer] Replaced '{target.name}' (custom {replacement.width}x{replacement.height}).");
                    }

                    if (clipped)
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Replacement for '{target.name}' was clipped to {copyWidth}x{copyHeight}.");
                        SafeAppendDebug($"Clipped texture copy for {target.name} to {copyWidth}x{copyHeight}");
                    }

                    if (!string.IsNullOrEmpty(blitMessage))
                    {
                        _logger.LogWarning(blitMessage);
                        SafeAppendDebug(blitMessage);
                    }

                    if (usedFallback)
                    {
                        TryCreateTextureOverride(target, replacement, target.width, target.height);
                    }
                }
                else if (TryCreateTextureOverride(target, replacement, target.width, target.height))
                {
                    replaced++;
                }
                else
                {
                    skipped++;
                }
            }

            CollectCandidateSprites(_spriteBuffer);

            foreach (var sprite in _spriteBuffer)
            {
                if (sprite == null)
                    continue;

                if (!_customTextures.TryGetValue(sprite.name, out var replacement))
                    continue;

                var atlasTexture = sprite.texture;
                if (atlasTexture == null)
                    continue;

                if (ReferenceEquals(replacement, atlasTexture))
                    continue;

                if (_customTextureIds.Contains(atlasTexture.GetInstanceID()))
                    continue;

                var rect = sprite.textureRect;
                var width = Mathf.RoundToInt(rect.width);
                var height = Mathf.RoundToInt(rect.height);
                var x = Mathf.RoundToInt(rect.x);
                var y = Mathf.RoundToInt(rect.y);

                if (width <= 0 || height <= 0)
                    continue;

                if (!TryGetTextureSize(atlasTexture, out var atlasWidth, out var atlasHeight))
                    continue;

                if (!TryGetSliceDimensions(atlasTexture, x, y, width, height, out var copyWidth, out var copyHeight, out var clipped, out var reason))
                {
                    if (!string.IsNullOrEmpty(reason))
                    {
                        _logger.LogWarning(reason);
                        SafeAppendDebug(reason);
                    }

                     if (TryCreateSpriteOverride(sprite, replacement, width, height))
                    {
                        replaced++;
                        continue;
                    }

                    skipped++;
                    continue;
                }

                if (TryBlitToTexture(replacement, atlasTexture, copyWidth, copyHeight, out var blitMessage, out var usedFallback, x, y))
                {
                    replaced++;

                    if (copyWidth != width || copyHeight != height)
                    {
                        _logger.LogInfo($"[CustomTextureReplacer] Resized + clipped sprite '{sprite.name}' in atlas '{atlasTexture.name}' ({replacement.width}x{replacement.height} -> region {copyWidth}x{copyHeight} at {x},{y}).");
                    }
                    else if (width != replacement.width || height != replacement.height)
                    {
                        _logger.LogInfo($"[CustomTextureReplacer] Resized + replaced sprite '{sprite.name}' in atlas '{atlasTexture.name}' ({replacement.width}x{replacement.height} -> region {copyWidth}x{copyHeight} at {x},{y}).");
                    }
                    else
                    {
                        _logger.LogInfo($"[CustomTextureReplacer] Replaced sprite '{sprite.name}' in atlas '{atlasTexture.name}' (region {copyWidth}x{copyHeight} at {x},{y}).");
                    }

                    if (clipped)
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Sprite '{sprite.name}' in atlas '{atlasTexture.name}' was clipped to {copyWidth}x{copyHeight}.");
                        SafeAppendDebug($"Clipped sprite copy for {sprite.name} to {copyWidth}x{copyHeight}");
                    }

                    if (!string.IsNullOrEmpty(blitMessage))
                    {
                        _logger.LogWarning(blitMessage);
                        SafeAppendDebug(blitMessage);
                    }

                    if (usedFallback)
                    {
                        TryCreateSpriteOverride(sprite, replacement, width, height);
                    }
                }
                else if (TryCreateSpriteOverride(sprite, replacement, width, height))
                {
                    replaced++;
                }
                else
                {
                    skipped++;
                }
            }

            ApplyTextureOverridesToMaterials();
            ApplySpriteOverridesToComponents();

            if (replaced > 0 || skipped > 0)
            {
                _logger.LogInfo($"[CustomTextureReplacer] Applied replacements. Success: {replaced}, skipped: {skipped}.");
                SafeAppendDebug($"ReplaceAllTextures finished. Success={replaced} Skipped={skipped}");
            }
        }
        private bool TryBlitToTexture(Texture source, Texture destination, int width, int height, out string extraMessage, out bool usedFallback, int dstX = 0, int dstY = 0)
        {
            extraMessage = string.Empty;
            usedFallback = false;
            RenderTexture rt = null;
            Texture2D temp = null;
            RenderTexture previousRt = null;
            bool clippedFormat = false;

            try
            {
                rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, rt);

                TextureFormat tempFormat = TextureFormat.RGBA32;
                bool mipChain = false;
                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;

                if (destination is Texture2D destTex)
                {
                    tempFormat = destTex.format;

                    if (!ValidateTextureFormat(tempFormat, width, height))
                    {
                        tempFormat = TextureFormat.RGBA32;
                        clippedFormat = true;
                    }
                }

                previousRt = RenderTexture.active;
                RenderTexture.active = rt;

                temp = new Texture2D(width, height, tempFormat, mipChain, linear)
                {
                    name = $"{destination?.name}_TempCopy"
                };
                temp.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                temp.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                RenderTexture.active = previousRt;
                previousRt = null;

                bool success = false;

                try
                {
                    Graphics.CopyTexture(temp, 0, 0, 0, 0, width, height, destination, 0, 0, dstX, dstY);
                    success = true;
                }
                catch (Exception copyEx)
                {
                    var fullCopy = dstX == 0 && dstY == 0 &&
                                   destination.width == width &&
                                   destination.height == height;

                    if (fullCopy)
                    {
                        try
                        {
                            Graphics.ConvertTexture(temp, destination);
                            extraMessage = $"[CustomTextureReplacer] ConvertTexture used for '{destination?.name}' after CopyTexture fallback: {copyEx.Message}";
                            usedFallback = true;
                            success = true;
                        }
                        catch (Exception convertEx)
                        {
                            extraMessage = $"[CustomTextureReplacer] ConvertTexture failed for '{destination?.name}': {convertEx.Message}";
                            success = false;
                        }
                    }
                    else
                    {
                        extraMessage = $"[CustomTextureReplacer] CopyTexture failed for '{destination?.name}': {copyEx.Message}";
                        success = false;
                    }
                }

                if (!success && clippedFormat && string.IsNullOrEmpty(extraMessage))
                {
                    extraMessage = $"[CustomTextureReplacer] Unable to blit into '{destination?.name}' due to format mismatch.";
                    usedFallback = true;
                }

                return success;
            }
            finally
            {
                if (clippedFormat && string.IsNullOrEmpty(extraMessage))
                {
                    extraMessage = $"[CustomTextureReplacer] Fallback format copy used for '{destination?.name}', possible truncation.";
                    usedFallback = true;
                }

                if (previousRt != null)
                    RenderTexture.active = previousRt;

                if (temp != null)
                    Destroy(temp);

                if (rt != null)
                    RenderTexture.ReleaseTemporary(rt);
            }
        }

        private bool TryCopyIntoTexture(Texture source, Texture2D destination, int width, int height, string label)
        {
            if (source == null || destination == null)
                return false;

            RenderTexture rt = null;
            var previous = RenderTexture.active;

            try
            {
                rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, rt);

                RenderTexture.active = rt;
                destination.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                destination.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                RenderTexture.active = previous;
                SafeAppendDebug($"Override texture refreshed for {label}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Failed to refresh override for '{label}': {ex.Message}");
                SafeAppendDebug($"Override refresh failed for {label}: {ex.Message}");
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null)
                    RenderTexture.ReleaseTemporary(rt);
            }
        }

        private bool TryCreateTextureOverride(Texture target, Texture2D replacement, int width, int height)
        {
            if (!(target is Texture2D targetTex) || replacement == null)
                return false;

            try
            {
                if (_textureOverrides.TryGetValue(targetTex, out var existingOverride))
                {
                    if (existingOverride is Texture2D tex2D && tex2D.width == width && tex2D.height == height)
                    {
                        if (TryCopyIntoTexture(replacement, tex2D, width, height, targetTex.name))
                        {
                            _textureOverrides[targetTex] = tex2D;
                            _textureOverridesByName[targetTex.name] = tex2D;
                            _overridesDirty = true;
                            return true;
                        }

                        RemoveTextureOverride(targetTex);
                    }
                }

                if (_textureOverridesByName.TryGetValue(targetTex.name, out var nameOverride))
                {
                    if (nameOverride is Texture2D tex2D && tex2D.width == width && tex2D.height == height)
                    {
                        if (TryCopyIntoTexture(replacement, tex2D, width, height, targetTex.name))
                        {
                            _textureOverrides[targetTex] = tex2D;
                            _textureOverridesByName[targetTex.name] = tex2D;
                            _overridesDirty = true;
                            return true;
                        }

                        RemoveTextureOverride(targetTex);
                    }
                }

                RemoveTextureOverride(targetTex);

                var newTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    name = $"{targetTex.name}_Custom",
                    wrapMode = targetTex.wrapMode,
                    filterMode = targetTex.filterMode,
                    anisoLevel = targetTex.anisoLevel
                };

                RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(replacement, rt);

                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                newTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                newTexture.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);

                _generatedTextures.Add(newTexture);
                _textureOverrides[targetTex] = newTexture;
                _textureOverridesByName[targetTex.name] = newTexture;

                // Link other instances with the same name to the override.
                foreach (var tex in Resources.FindObjectsOfTypeAll<Texture2D>())
                {
                    if (tex == null)
                        continue;

                    if (ReferenceEquals(tex, targetTex))
                        continue;

                    if (string.Equals(tex.name, targetTex.name, StringComparison.OrdinalIgnoreCase))
                    {
                        _textureOverrides[tex] = newTexture;
                    }
                }

                _logger.LogInfo($"[CustomTextureReplacer] Using runtime texture override for '{targetTex.name}'.");
                _logger.LogDebug($"[CustomTextureReplacer] Override '{targetTex.name}' uses custom '{replacement.name}' ({replacement.width}x{replacement.height}).");
                SafeAppendDebug($"Texture override created for {targetTex.name}");
                _overridesDirty = true;
                ApplyTextureOverridesToMaterials(targetTex);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Failed to create texture override for '{target?.name}': {ex.Message}");
                SafeAppendDebug($"Texture override failed for {target?.name}: {ex.Message}");
                return false;
            }
        }

        private bool TryCreateSpriteOverride(Sprite originalSprite, Texture2D replacement, int width, int height)
        {
            if (originalSprite == null || replacement == null)
                return false;

            try
            {
                if (_spriteOverridesByName.TryGetValue(originalSprite.name, out var existing) && existing != null)
                {
                    var existingTexture = existing.texture;
                    if (existingTexture != null && existingTexture.width == width && existingTexture.height == height)
                    {
                        if (existingTexture is Texture2D spriteTex && TryCopyIntoTexture(replacement, spriteTex, width, height, originalSprite.name))
                        {
                            _spriteOverrides[originalSprite] = existing;
                            _spriteOverridesByName[originalSprite.name] = existing;
                            _spriteOverrideTextures[existing] = spriteTex;
                            _overridesDirty = true;
                            return true;
                        }

                        RemoveSpriteOverride(originalSprite.name);
                    }
                }

                RemoveSpriteOverride(originalSprite.name);

                RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(replacement, rt);

                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D spriteTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    name = $"{originalSprite.name}_Custom"
                };
                spriteTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                spriteTexture.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);

                _generatedTextures.Add(spriteTexture);

                var rect = originalSprite.rect;
                Vector2 pivotNormalized = rect.size.sqrMagnitude > 0f
                    ? new Vector2(originalSprite.pivot.x / rect.width, originalSprite.pivot.y / rect.height)
                    : new Vector2(0.5f, 0.5f);

                var newSprite = Sprite.Create(spriteTexture, new Rect(0, 0, width, height), pivotNormalized, originalSprite.pixelsPerUnit, 0, SpriteMeshType.FullRect, originalSprite.border, false);
                newSprite.name = originalSprite.name;

                _generatedSprites.Add(newSprite);
                _spriteOverrideTextures[newSprite] = spriteTexture;

                _spriteOverrides[originalSprite] = newSprite;
                _spriteOverridesByName[originalSprite.name] = newSprite;
                _spriteOverrides[newSprite] = newSprite;

                foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sprite == null)
                        continue;

                    if (ReferenceEquals(sprite, originalSprite))
                        continue;

                    if (string.Equals(sprite.name, originalSprite.name, StringComparison.OrdinalIgnoreCase))
                    {
                        _spriteOverrides[sprite] = newSprite;
                    }
                }

                _logger.LogInfo($"[CustomTextureReplacer] Created runtime sprite override for '{originalSprite.name}'.");
                _logger.LogDebug($"[CustomTextureReplacer] Sprite override '{originalSprite.name}' uses custom '{replacement.name}' ({replacement.width}x{replacement.height}).");
                SafeAppendDebug($"Sprite override created for {originalSprite.name}");
                _overridesDirty = true;
                ApplySpriteOverridesToComponents();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Failed to create sprite override for '{originalSprite?.name}': {ex.Message}");
                SafeAppendDebug($"Sprite override failed for {originalSprite?.name}: {ex.Message}");
                return false;
            }
        }

        private void RemoveTextureOverride(Texture target)
        {
            if (target == null)
                return;

            if (_textureOverrides.TryGetValue(target, out var existing))
            {
                _textureOverrides.Remove(target);
                if (existing is Texture2D tex && _generatedTextures.Remove(tex))
                {
                    Destroy(tex);
                }
                if (_textureOverridesByName.TryGetValue(target.name, out var mapped) && ReferenceEquals(mapped, existing))
                {
                    _textureOverridesByName.Remove(target.name);
                }
                _overridesDirty = true;
            }
            else if (_textureOverridesByName.TryGetValue(target.name, out var existingByName) && existingByName is Texture2D texByName && _generatedTextures.Remove(texByName))
            {
                _textureOverridesByName.Remove(target.name);
                Destroy(texByName);
                _overridesDirty = true;
            }
        }

        private void RemoveSpriteOverride(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName))
                return;

            if (_spriteOverridesByName.TryGetValue(spriteName, out var existing))
            {
                var keysToRemove = _spriteOverrides.Where(kvp => ReferenceEquals(kvp.Value, existing) || string.Equals(kvp.Key?.name, spriteName, StringComparison.OrdinalIgnoreCase)).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    _spriteOverrides.Remove(key);
                }

                _spriteOverridesByName.Remove(spriteName);

                if (_spriteOverrideTextures.TryGetValue(existing, out var tex))
                {
                    _spriteOverrideTextures.Remove(existing);
                    if (tex != null && _generatedTextures.Remove(tex))
                        Destroy(tex);
                }

                if (_generatedSprites.Remove(existing))
                    Destroy(existing);

                _overridesDirty = true;
            }
        }

        private Texture GetReplacementTexture(Texture original)
        {
            if (original == null)
                return null;

            if (_textureOverrides.TryGetValue(original, out var direct))
                return direct;

            if (_textureOverridesByName.TryGetValue(original.name, out var byName))
                return byName;

            return null;
        }

        private Sprite GetReplacementSprite(Sprite original)
        {
            if (original == null)
                return null;

            if (_spriteOverrides.TryGetValue(original, out var direct))
                return direct;

            if (_spriteOverridesByName.TryGetValue(original.name, out var byName))
                return byName;

            return null;
        }

        private void ApplyTextureOverridesToMaterials(Texture targetHint = null)
        {
            if (_textureOverrides.Count == 0 && _textureOverridesByName.Count == 0)
                return;

            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null)
                    continue;

                foreach (var propertyName in material.GetTexturePropertyNames())
                {
                    var current = material.GetTexture(propertyName);
                    if (current == null)
                        continue;

                    if (targetHint != null && !ReferenceEquals(current, targetHint) && !string.Equals(current.name, targetHint.name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var replacement = GetReplacementTexture(current);
                    if (replacement != null && !ReferenceEquals(current, replacement))
                    {
                        material.SetTexture(propertyName, replacement);
                    }
                }
            }
        }

        private void ApplyTextureOverridesToRenderers()
        {
            if (_textureOverrides.Count == 0 && _textureOverridesByName.Count == 0)
                return;

            foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null)
                    continue;

                bool blockChanged = false;
                renderer.GetPropertyBlock(_propertyBlock);

                foreach (var property in RendererTextureProperties)
                {
                    Texture current = null;
                    try
                    {
                        current = _propertyBlock.GetTexture(property);
                    }
                    catch
                    {
                        current = null;
                    }

                    if (current == null)
                        continue;

                    var replacement = GetReplacementTexture(current);
                    if (replacement != null && !ReferenceEquals(current, replacement))
                    {
                        _propertyBlock.SetTexture(property, replacement);
                        blockChanged = true;
                    }
                }

                if (blockChanged)
                {
                    renderer.SetPropertyBlock(_propertyBlock);
                }
            }
        }

        private void ApplySpriteOverridesToComponents()
        {
            if (_spriteOverridesByName.Count == 0 && _spriteOverrides.Count == 0)
                return;

            foreach (var renderer in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
            {
                if (renderer == null)
                    continue;

                var replacement = GetReplacementSprite(renderer.sprite);
                if (replacement != null && !ReferenceEquals(renderer.sprite, replacement))
                {
                    renderer.sprite = replacement;
                }
            }

            if (UIImageType != null && UIImageSpriteProperty != null)
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(UIImageType))
                {
                    if (obj == null)
                        continue;

                    var currentSprite = UIImageSpriteProperty.GetValue(obj) as Sprite;
                    var replacement = GetReplacementSprite(currentSprite);
                    if (replacement != null && !ReferenceEquals(currentSprite, replacement))
                    {
                        UIImageSpriteProperty.SetValue(obj, replacement);
                    }
                }
            }
        }

        private bool ValidateTextureFormat(TextureFormat format, int width, int height)
        {
            // Reject compressed formats that can't be instanced at runtime with arbitrary sizes.
            switch (format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC7:
                    return false;
                default:
                    return true;
            }
        }

        private bool TryGetTextureSize(Texture texture, out int width, out int height)
        {
            width = 0;
            height = 0;

            switch (texture)
            {
                case Texture2D tex2D:
                    width = tex2D.width;
                    height = tex2D.height;
                    return true;
                case RenderTexture rt:
                    width = rt.width;
                    height = rt.height;
                    return true;
                default:
                    if (texture != null)
                    {
                        width = texture.width;
                        height = texture.height;
                        return width > 0 && height > 0;
                    }
                    return false;
            }
        }

        private bool TryGetGraphicsFormat(Texture texture, out GraphicsFormat format)
        {
            format = GraphicsFormat.None;

            switch (texture)
            {
                case Texture2D tex2D:
                    format = tex2D.graphicsFormat;
                    return format != GraphicsFormat.None;
                case RenderTexture rt:
                    format = rt.graphicsFormat;
                    return format != GraphicsFormat.None;
                default:
                    return false;
            }
        }

        private bool TryGetSliceDimensions(Texture texture, int dstX, int dstY, int width, int height, out int sliceWidth, out int sliceHeight, out bool clipped, out string failureReason)
        {
            sliceWidth = 0;
            sliceHeight = 0;
            clipped = false;
            failureReason = string.Empty;

            if (!TryGetTextureSize(texture, out var textureWidth, out var textureHeight))
                return false;

            var availableWidth = textureWidth - dstX;
            var availableHeight = textureHeight - dstY;

            if (availableWidth <= 0 || availableHeight <= 0)
                return false;

            sliceWidth = Mathf.Min(width, availableWidth);
            sliceHeight = Mathf.Min(height, availableHeight);

            if (sliceWidth <= 0 || sliceHeight <= 0)
                return false;

            if (TryGetGraphicsFormat(texture, out var format) && GraphicsFormatUtility.IsCompressedFormat(format))
            {
                failureReason = $"[CustomTextureReplacer] Texture '{texture?.name ?? "<unnamed>"}' uses compressed format ({format}), switching to runtime override.";
                return false;
            }

            clipped = sliceWidth != width || sliceHeight != height;
            return true;
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

        private void DumpAllSprites()
        {
            try
            {
                CollectCandidateSprites(_spriteBuffer);
                _spriteBuffer.Sort((a, b) => string.Compare(a?.name, b?.name, StringComparison.OrdinalIgnoreCase));

                using var writer = new StreamWriter(_spriteDumpFile, false);
                foreach (var sprite in _spriteBuffer)
                {
                    if (sprite == null)
                        continue;

                    var texture = sprite.texture;
                    var textureName = texture != null ? texture.name : "<null>";
                    var rect = sprite.textureRect;
                    var line = $"{sprite.name} -> {textureName} ({Mathf.RoundToInt(rect.width)}x{Mathf.RoundToInt(rect.height)} at {Mathf.RoundToInt(rect.x)},{Mathf.RoundToInt(rect.y)})";
                    writer.WriteLine(line);
                }

                _logger.LogInfo($"[CustomTextureReplacer] Dumped sprite list to '{_spriteDumpFile}' ({_spriteBuffer.Count} entries).");
                SafeAppendDebug($"DumpAllSprites wrote {_spriteBuffer.Count} entries");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Unable to dump sprites: {ex.Message}");
                SafeAppendDebug($"DumpAllSprites exception: {ex.Message}");
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

                if (_generatedTextures.Contains(tex))
                    return;

                var id = tex.GetInstanceID();
                if (_customTextureIds.Contains(id))
                    return;

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

        private void CollectCandidateSprites(List<Sprite> destination)
        {
            destination.Clear();
            _spriteScratch.Clear();

            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite == null)
                    continue;

                if (_generatedSprites.Contains(sprite))
                    continue;

                if (_spriteScratch.Add(sprite.GetInstanceID()))
                    destination.Add(sprite);
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

        private void ProcessExtractionRequests()
        {
            if (string.IsNullOrEmpty(_exportFolder))
                return;

            try
            {
                var files = Directory.GetFiles(Paths.PluginPath, "CustomTextures.extract.*.now", SearchOption.TopDirectoryOnly);
                if (files.Length == 0)
                    return;

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    string textureName = ExtractTextureNameFromTrigger(fileName);
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Could not remove extraction trigger '{fileName}': {ex.Message}");
                        continue;
                    }

                    if (string.IsNullOrEmpty(textureName))
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Extraction trigger '{fileName}' did not specify a texture name.");
                        continue;
                    }

                    ExportTexture(textureName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Unable to process extraction triggers: {ex.Message}");
            }
        }

        private static string ExtractTextureNameFromTrigger(string fileName)
        {
            const string prefix = "CustomTextures.extract.";
            const string suffix = ".now";

            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var inner = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
            return inner.Replace("%20", " ").Trim();
        }

        private void ExportTexture(string textureName)
        {
            if (string.IsNullOrEmpty(textureName))
                return;

            CollectCandidateTextures(_textureBuffer);

            var exportTargets = new List<(string name, Texture2D texture)>();

            foreach (var tex in _textureBuffer)
            {
                if (tex == null)
                    continue;

                if (!string.Equals(tex.name, textureName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var replacement = GetReplacementTexture(tex) as Texture2D;
                if (replacement != null)
                {
                    if (!exportTargets.Any(t => ReferenceEquals(t.texture, replacement)))
                        exportTargets.Add((textureName, replacement));
                }
                else if (tex is Texture2D tex2D)
                {
                    if (!exportTargets.Any(t => ReferenceEquals(t.texture, tex2D)))
                        exportTargets.Add((tex.name, tex2D));
                }
            }

            if (exportTargets.Count == 0 && _textureOverridesByName.TryGetValue(textureName, out var overrideTex) && overrideTex is Texture2D overrideTex2D)
            {
                exportTargets.Add((textureName, overrideTex2D));
            }

            if (exportTargets.Count == 0)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Export failed - could not find texture '{textureName}'.");
                SafeAppendDebug($"ExportTexture failed for {textureName} (not found)");
                return;
            }

            Directory.CreateDirectory(_exportFolder);

            var index = 0;
            foreach (var entry in exportTargets)
            {
                try
                {
                    var readable = CreateReadableCopy(entry.texture);
                    if (readable == null)
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Export failed - could not read texture '{entry.texture.name}'.");
                        SafeAppendDebug($"ExportTexture failed for {entry.texture.name} (readable copy null)");
                        continue;
                    }

                    var bytes = readable.EncodeToPNG();
                    Destroy(readable);

                    var suffix = exportTargets.Count > 1 ? $"_{index}" : string.Empty;
                    var outputPath = Path.Combine(_exportFolder, $"{entry.name}{suffix}.png");
                    File.WriteAllBytes(outputPath, bytes);

                    _logger.LogInfo($"[CustomTextureReplacer] Exported texture '{entry.name}' to '{outputPath}'.");
                    SafeAppendDebug($"ExportTexture wrote {outputPath}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[CustomTextureReplacer] Export failed for '{entry.texture?.name}': {ex.Message}");
                    SafeAppendDebug($"ExportTexture exception for {entry.texture?.name}: {ex.Message}");
                }

                index++;
            }
        }

        private Texture2D CreateReadableCopy(Texture2D source)
        {
            if (source == null)
                return null;

            try
            {
                var width = source.width;
                var height = source.height;

                RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, rt);

                var previous = RenderTexture.active;
                RenderTexture.active = rt;

                Texture2D readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                readable.Apply();

                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);

                return readable;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Could not create readable copy: {ex.Message}");
                SafeAppendDebug($"CreateReadableCopy exception: {ex.Message}");
                return null;
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

















