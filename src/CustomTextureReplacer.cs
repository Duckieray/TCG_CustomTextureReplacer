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
using TMPro;
using UnityEngine.UI;
namespace CustomTextureReplacer
{
    [BepInPlugin("com.duckieray.cardshop.customtextures", "Custom Texture Replacer", "1.3.0")]
    public class CustomTextureReplacer : BaseUnityPlugin
    {
        private void Awake()
        {
#pragma warning disable CS0649
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
        private readonly Dictionary<string, CardTextOverride> _cardOverrides = new Dictionary<string, CardTextOverride>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CardTextOverride> _capturedCardData = new Dictionary<string, CardTextOverride>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MeshOverrideData> _meshOverridesByTarget = new Dictionary<string, MeshOverrideData>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _meshTextureFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, WeakReference<MeshFilter>> _meshFilterReferences = new Dictionary<int, WeakReference<MeshFilter>>();
        private readonly Dictionary<int, Mesh> _meshFilterOriginalMeshes = new Dictionary<int, Mesh>();
        private readonly HashSet<int> _meshFilterOverrideIds = new HashSet<int>();
        private readonly Dictionary<int, WeakReference<SkinnedMeshRenderer>> _skinnedRendererReferences = new Dictionary<int, WeakReference<SkinnedMeshRenderer>>();
        private readonly Dictionary<int, Mesh> _skinnedOriginalMeshes = new Dictionary<int, Mesh>();
        private readonly HashSet<int> _skinnedRendererOverrideIds = new HashSet<int>();
        private readonly Dictionary<int, WeakReference<MeshCollider>> _meshColliderReferences = new Dictionary<int, WeakReference<MeshCollider>>();
        private readonly Dictionary<int, Mesh> _meshColliderOriginalMeshes = new Dictionary<int, Mesh>();
        private readonly HashSet<int> _meshColliderOverrideIds = new HashSet<int>();
        private readonly Dictionary<int, WeakReference<Renderer>> _rendererReferences = new Dictionary<int, WeakReference<Renderer>>();
        private readonly Dictionary<int, Material[]> _rendererOriginalMaterials = new Dictionary<int, Material[]>();
        private readonly HashSet<int> _rendererOverrideIds = new HashSet<int>();
        private readonly Dictionary<string, AssetBundle> _meshAssetBundles = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FileSystemWatcher> _meshBundleWatchers = new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
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
        private string _meshDumpFile = string.Empty;
        private string _meshDumpTriggerFile = string.Empty;

        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private ConfigEntry<FolderPriorityMode> _folderPriorityMode;
        private ConfigEntry<string> _preferredFolderHint;
        private string[] _preferredFolderTokens = Array.Empty<string>();
        private static readonly char[] PreferredFolderSeparators = new[] { ';', ',', '|' };
        private string _cardOverridesPath = string.Empty;
        private string _meshOverridesPath = string.Empty;
        private bool _cardOverridesReloadRequested;
        private bool _meshOverridesReloadRequested;
        private FileSystemWatcher _cardOverridesWatcher;
        private FileSystemWatcher _meshOverridesWatcher;

        private Harmony _harmony;

        private volatile bool _reloadRequested;
        private bool _hasDumpedAfterSceneLoad;
        private bool _pendingDump;
        private bool _reapplyRequested;
        private bool _loggedHeartbeat;
        private float _nextScanTime;
        private bool _meshOverridesDirty;

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
            ResolveCardOverridesPath();
            LoadCardOverrides(logDetails: true);
            SetupCardOverridesWatcher();

            _dumpFile = Path.Combine(Paths.PluginPath, "TexturesList.txt");
            _dumpTriggerFile = Path.Combine(Paths.PluginPath, "CustomTextures.dump.now");
            _refreshTriggerFile = Path.Combine(Paths.PluginPath, "CustomTextures.refresh.now");
            _spriteDumpFile = Path.Combine(Paths.PluginPath, "SpritesList.txt");
            _spriteDumpTriggerFile = Path.Combine(Paths.PluginPath, "SpritesList.dump.now");
            _debugLogFile = Path.Combine(Paths.PluginPath, "CustomTextureReplacer.debug.log");
            _exportFolder = Path.Combine(Paths.PluginPath, "ExportedTextures");
            Directory.CreateDirectory(_exportFolder);
            _meshDumpFile = Path.Combine(Paths.PluginPath, "MeshesList.txt");
            _meshDumpTriggerFile = Path.Combine(Paths.PluginPath, "MeshesList.dump.now");

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

            if (_cardOverridesWatcher != null)
            {
                try
                {
                    _cardOverridesWatcher.EnableRaisingEvents = false;
                    _cardOverridesWatcher.Changed -= OnCardOverridesFileChanged;
                    _cardOverridesWatcher.Created -= OnCardOverridesFileChanged;
                    _cardOverridesWatcher.Deleted -= OnCardOverridesFileChanged;
                    _cardOverridesWatcher.Renamed -= OnCardOverridesFileRenamed;
                    _cardOverridesWatcher.Dispose();
                }
                catch { }
                _cardOverridesWatcher = null;
            }

            if (_meshOverridesWatcher != null)
            {
                try
                {
                    _meshOverridesWatcher.EnableRaisingEvents = false;
                    _meshOverridesWatcher.Changed -= OnMeshOverridesFileChanged;
                    _meshOverridesWatcher.Created -= OnMeshOverridesFileChanged;
                    _meshOverridesWatcher.Deleted -= OnMeshOverridesFileChanged;
                    _meshOverridesWatcher.Renamed -= OnMeshOverridesFileRenamed;
                    _meshOverridesWatcher.Dispose();
                }
                catch { }
                _meshOverridesWatcher = null;
            }

            RevertAllMeshOverrides();
            DisposeMeshOverrideResources();

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
            _cardOverrides.Clear();

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
            _meshTextureFolders.Clear();
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
                DumpAllMeshes();
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

            if (CheckAndConsumeFileTrigger(_meshDumpTriggerFile))
            {
                _logger.LogInfo($"[CustomTextureReplacer] Manual mesh dump triggered via '{Path.GetFileName(_meshDumpTriggerFile)}'.");
                SafeAppendDebug("Mesh dump trigger consumed.");
                DumpAllMeshes();
            }

            if (_reloadRequested)
            {
                _reloadRequested = false;
                SafeAppendDebug("Reload flag consumed.");
                ReloadCustomTextures();
            }

            if (_cardOverridesReloadRequested)
            {
                _cardOverridesReloadRequested = false;
                SafeAppendDebug("Card override reload flag consumed.");
                LoadCardOverrides(logDetails: true);
                ReapplyCardOverrides();
            }

            if (_meshOverridesReloadRequested)
            {
                _meshOverridesReloadRequested = false;
                SafeAppendDebug("Mesh override reload flag consumed.");
                LoadMeshOverrides(logDetails: true);
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                _logger.LogInfo("[CustomTextureReplacer] Manual dump triggered (F8).");
                SafeAppendDebug("F8 pressed");
                DumpAllTextures();
                DumpAllSprites();
                DumpAllMeshes();
            }

            if (Input.GetKeyDown(KeyCode.F9))
            {
                _logger.LogInfo("[CustomTextureReplacer] Original card data dump requested (F9).");
                SafeAppendDebug("F9 pressed - dumping original card data.");
                DumpOriginalCardData();
            }

            if (Input.GetKeyDown(KeyCode.F10))
            {
                _logger.LogInfo("[CustomTextureReplacer] Manual mesh dump triggered (F10).");
                SafeAppendDebug("F10 pressed");
                DumpAllMeshes();
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
                DumpAllMeshes();
            }

            ProcessExtractionRequests();

            if (_overridesDirty)
            {
                _overridesDirty = false;
                ApplyTextureOverridesToMaterials();
                ApplyTextureOverridesToRenderers();
                ApplySpriteOverridesToComponents();
                _meshOverridesDirty = true;
                ApplyMeshOverrides();
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

            if (_meshTextureFolders.Count > 0)
            {
                foreach (var folder in _meshTextureFolders)
                {
                    TryAddTextureFolder(discovered, folder, logDetails);
                }
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
            DumpAllMeshes();
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
                DumpAllMeshes();
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

            var overridesPathChanged = ResolveCardOverridesPath();
            if (overridesPathChanged)
            {
                LoadCardOverrides(logDetails: true);
                SetupCardOverridesWatcher();
            }

            var meshOverridesPathChanged = ResolveMeshOverridesPath();

            LoadCustomTextures();

            LoadMeshOverrides(logDetails: meshOverridesPathChanged || _meshOverridesByTarget.Count == 0);
            if (_meshOverridesWatcher == null && !string.IsNullOrEmpty(_meshOverridesPath) || meshOverridesPathChanged)
            {
                SetupMeshOverridesWatcher();
            }

            _logger.LogInfo($"[CustomTextureReplacer] Loaded {_customTextures.Count} custom textures from disk.");
            SafeAppendDebug($"ReloadCustomTextures complete: {_customTextures.Count} textures");
            RequestReapply("CustomTexturesReloaded");
            _overridesDirty = true;
            ReapplyCardOverrides();
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

            _meshOverridesDirty = true;
            ApplyMeshOverrides();
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

        private void DumpAllMeshes()
        {
            if (string.IsNullOrEmpty(_meshDumpFile))
                return;

            try
            {
                var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
                var ordered = meshes
                    .Where(mesh => mesh != null)
                    .OrderBy(mesh => mesh.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                using var writer = new StreamWriter(_meshDumpFile, false);
                foreach (var mesh in ordered)
                {
                    var vertexCount = mesh != null ? mesh.vertexCount : 0;
                    var subMeshCount = mesh != null ? mesh.subMeshCount : 0;
                    writer.WriteLine($"{mesh.name} (vertices: {vertexCount}, subMeshes: {subMeshCount})");
                }

                _logger.LogInfo($"[CustomTextureReplacer] Dumped mesh list to '{_meshDumpFile}' ({ordered.Count} entries).");
                SafeAppendDebug($"DumpAllMeshes wrote {ordered.Count} entries");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Unable to dump meshes: {ex.Message}");
                SafeAppendDebug($"DumpAllMeshes exception: {ex.Message}");
            }
        }

        private bool ResolveMeshOverridesPath()
        {
            var previous = _meshOverridesPath ?? string.Empty;
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    return;

                try
                {
                    var full = Path.GetFullPath(candidate);
                    if (seen.Add(full))
                        candidates.Add(full);
                }
                catch
                {
                    if (seen.Add(candidate))
                        candidates.Add(candidate);
                }
            }

            if (_textureFolders != null)
            {
                foreach (var folder in _textureFolders)
                {
                    if (string.IsNullOrWhiteSpace(folder))
                        continue;
                    AddCandidate(Path.Combine(folder, "MeshOverrides.json"));
                }
            }

            AddCandidate(Path.Combine(Paths.PluginPath, "CustomTextures", "MeshOverrides.json"));
            AddCandidate(Path.Combine(Paths.PluginPath, "MeshOverrides.json"));

            var resolved = string.Empty;
            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        resolved = candidate;
                        break;
                    }
                }
                catch
                {
                }

                if (string.IsNullOrEmpty(resolved))
                    resolved = candidate;
            }

            if (string.IsNullOrEmpty(resolved))
            {
                resolved = Path.Combine(Paths.PluginPath, "MeshOverrides.json");
            }

            var changed = !string.Equals(previous, resolved, StringComparison.OrdinalIgnoreCase);
            _meshOverridesPath = resolved;

            if (changed)
            {
                SafeAppendDebug($"MeshOverrides path resolved: {_meshOverridesPath}");
            }

            return changed;
        }

        private void LoadMeshOverrides(bool logDetails)
        {
            SafeAppendDebug("LoadMeshOverrides invoked.");

            RevertAllMeshOverrides();
            DisposeMeshOverrideResources();

            if (string.IsNullOrEmpty(_meshOverridesPath))
            {
                SafeAppendDebug("MeshOverrides path empty; nothing to load.");
                _meshOverridesDirty = true;
                RequestReapply("MeshOverridesReloaded");
                return;
            }

            var path = _meshOverridesPath;
            try
            {
                path = NormalizePath(path);
            }
            catch
            {
            }

            if (!File.Exists(path))
            {
                if (logDetails)
                    _logger.LogInfo($"[CustomTextureReplacer] Mesh override file not found at '{path}'.");
                SafeAppendDebug($"MeshOverrides file not found: {path}");
                _meshOverridesDirty = true;
                RequestReapply("MeshOverridesReloaded");
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                var parsed = MiniJson.Deserialize(json);

                IList entries = null;
                var rootTextureFolderCollector = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (parsed is IList list)
                {
                    entries = list;
                }
                else if (parsed is Dictionary<string, object> dict)
                {
                    CollectMeshTextureFolders(dict, "textureFolders", configDirectory: Path.GetDirectoryName(path) ?? Paths.PluginPath, rootTextureFolderCollector);
                    CollectMeshTextureFolders(dict, "textureFolder", configDirectory: Path.GetDirectoryName(path) ?? Paths.PluginPath, rootTextureFolderCollector);

                    if (dict.TryGetValue("overrides", out var overridesValue) && overridesValue is IList overrideList)
                    {
                        entries = overrideList;
                    }
                    else if (dict.TryGetValue("entries", out var entriesValue) && entriesValue is IList entryList)
                    {
                        entries = entryList;
                    }
                }

                entries ??= Array.Empty<object>();

                var configDirectory = Path.GetDirectoryName(path) ?? Paths.PluginPath;
                int loadedCount = 0;

                 var collectedTextureFolders = new HashSet<string>(rootTextureFolderCollector, StringComparer.OrdinalIgnoreCase);

                foreach (var entryObj in entries)
                {
                    if (!(entryObj is Dictionary<string, object> entryDict))
                        continue;

                    var targetName = GetString(entryDict, "target");
                    if (string.IsNullOrWhiteSpace(targetName))
                    {
                        SafeAppendDebug("MeshOverride entry skipped (missing target).");
                        continue;
                    }

                    var meshAssetName = GetString(entryDict, "mesh");
                    var meshHint = GetString(entryDict, "meshHint");
                    var materialHints = GetStringArray(entryDict, "materialHints");
                    var explicitMaterialsSpecified = entryDict.ContainsKey("materials");
                    var autoSelectMesh = string.IsNullOrWhiteSpace(meshAssetName);
                    var autoSelectMaterials = !explicitMaterialsSpecified;

                    var bundleReference = GetString(entryDict, "bundle");
                    string bundlePath = string.Empty;
                    AssetBundle bundle = null;

                    if (!string.IsNullOrWhiteSpace(bundleReference))
                    {
                        if (!TryResolveMeshBundlePath(bundleReference, configDirectory, out bundlePath))
                        {
                            _logger.LogWarning($"[CustomTextureReplacer] Could not locate bundle '{bundleReference}' for mesh override '{targetName}'.");
                            SafeAppendDebug($"MeshOverride bundle not found ({bundleReference}) for {targetName}");
                            continue;
                        }

                        bundle = GetOrLoadMeshBundle(bundlePath);
                        if (bundle == null)
                        {
                            _logger.LogWarning($"[CustomTextureReplacer] Failed to load bundle '{bundlePath}' for mesh override '{targetName}'.");
                            SafeAppendDebug($"MeshOverride bundle load failed: {bundlePath}");
                            continue;
                        }
                    }

                    var data = new MeshOverrideData
                    {
                        TargetName = targetName,
                        MeshAssetName = meshAssetName,
                        MeshSelectionHint = meshHint,
                        AutoSelectMesh = autoSelectMesh,
                        AutoSelectMaterials = autoSelectMaterials,
                        MaterialSelectionHints = materialHints ?? Array.Empty<string>(),
                        SourceBundlePath = bundlePath,
                        ApplyToMeshFilter = GetBool(entryDict, "applyToMeshFilters", true),
                        ApplyToSkinnedMeshRenderer = GetBool(entryDict, "applyToSkinnedMeshRenderers", true),
                        ApplyToMeshCollider = GetBool(entryDict, "applyToMeshColliders", false)
                    };

                    if (data.AutoSelectMesh && bundle == null)
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Mesh override for '{targetName}' requested automatic mesh selection but no bundle was provided.");
                        SafeAppendDebug($"MeshOverride auto-mesh failed (no bundle) for {targetName}");
                        continue;
                    }

                    Mesh sourceMesh = null;
                    try
                    {
                        if (data.AutoSelectMesh)
                        {
                            if (!TryAutoSelectMeshFromBundle(data, bundle, logDetails))
                            {
                                SafeAppendDebug($"MeshOverride auto-mesh selection failed for {targetName}");
                                continue;
                            }

                            meshAssetName = data.MeshAssetName;
                            data.AutoSelectMesh = false;
                        }

                        sourceMesh = bundle != null ? bundle.LoadAsset<Mesh>(meshAssetName) : Resources.Load<Mesh>(meshAssetName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Exception loading mesh '{meshAssetName}' for '{targetName}': {ex.Message}");
                        SafeAppendDebug($"MeshOverride load exception ({meshAssetName}): {ex.Message}");
                        continue;
                    }

                    if (sourceMesh == null)
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Mesh asset '{meshAssetName}' not found for override '{targetName}'.");
                        SafeAppendDebug($"MeshOverride missing asset {meshAssetName} for {targetName}");
                        continue;
                    }

                    var meshInstance = Instantiate(sourceMesh);
                    meshInstance.name = targetName;

                    data.MeshInstance = meshInstance;

                    CollectMeshTextureFolders(entryDict, "textureFolders", configDirectory, collectedTextureFolders);
                    CollectMeshTextureFolders(entryDict, "textureFolder", configDirectory, collectedTextureFolders);

                    if (explicitMaterialsSpecified)
                    {
                        PopulateMaterialOverrides(entryDict, configDirectory, bundleReference, data, collectedTextureFolders);
                    }

                    if (data.AutoSelectMaterials && data.MaterialOverrides.Count == 0)
                    {
                        if (!TryAutoPopulateMaterials(data, bundle, meshInstance.subMeshCount, logDetails))
                        {
                            SafeAppendDebug($"MeshOverride auto-material selection incomplete for {targetName}");
                        }
                    }

                    _meshOverridesByTarget[targetName] = data;
                    loadedCount++;
                }

                UpdateMeshTextureFolders(collectedTextureFolders);

                if (logDetails)
                    _logger.LogInfo($"[CustomTextureReplacer] Loaded {loadedCount} mesh override(s) from '{path}'.");
                SafeAppendDebug($"MeshOverrides load complete: {loadedCount} entries");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Failed to load mesh overrides from '{path}': {ex.Message}");
                SafeAppendDebug($"MeshOverrides load exception: {ex.Message}");
            }

            _meshOverridesDirty = true;
            RequestReapply("MeshOverridesReloaded");
        }

        private void CollectMeshTextureFolders(Dictionary<string, object> dict, string key, string configDirectory, HashSet<string> collector)
        {
            if (dict == null || collector == null || string.IsNullOrEmpty(key))
                return;

            if (!dict.TryGetValue(key, out var value) || value == null)
                return;

            foreach (var raw in EnumerateStringValues(value))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var resolved = ResolveRelativePath(configDirectory, raw);
                if (string.IsNullOrEmpty(resolved))
                    continue;

                if (!Directory.Exists(resolved))
                {
                    _logger.LogWarning($"[CustomTextureReplacer] Mesh override texture folder '{raw}' (resolved '{resolved}') not found.");
                    SafeAppendDebug($"Mesh texture folder missing: {resolved}");
                    continue;
                }

                collector.Add(resolved);
            }
        }

        private void UpdateMeshTextureFolders(HashSet<string> collected)
        {
            collected ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_meshTextureFolders.SetEquals(collected))
                return;

            var added = collected.Except(_meshTextureFolders, StringComparer.OrdinalIgnoreCase).ToList();
            var removed = _meshTextureFolders.Except(collected, StringComparer.OrdinalIgnoreCase).ToList();

            _meshTextureFolders.Clear();
            foreach (var folder in collected)
            {
                _meshTextureFolders.Add(folder);
            }

            foreach (var folder in added)
            {
                SafeAppendDebug($"Mesh texture folder registered: {folder}");
            }

            foreach (var folder in removed)
            {
                SafeAppendDebug($"Mesh texture folder removed: {folder}");
            }

            _reloadRequested = true;
        }

        private static IEnumerable<string> EnumerateStringValues(object value)
        {
            if (value == null)
                yield break;

            if (value is string s)
            {
                yield return s;
                yield break;
            }

            if (value is IList list)
            {
                foreach (var item in list)
                {
                    if (item == null)
                        continue;
                    var text = item.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        yield return text;
                }
                yield break;
            }

            var fallback = value.ToString();
            if (!string.IsNullOrWhiteSpace(fallback))
                yield return fallback;
        }

        private string ResolveRelativePath(string baseDirectory, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return string.Empty;

            try
            {
                if (Path.IsPathRooted(candidate))
                    return NormalizePath(candidate);

                var root = string.IsNullOrEmpty(baseDirectory) ? Paths.PluginPath : baseDirectory;
                return NormalizePath(Path.Combine(root, candidate));
            }
            catch
            {
                return candidate;
            }
        }

        private Texture2D LoadExternalTexture(string path, string label, bool logWarnings)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                var data = ms.ToArray();

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                if (!ImageConversion.LoadImage(texture, data, markNonReadable: false))
                {
                    Destroy(texture);
                    if (logWarnings)
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Failed to decode external texture '{path}'.");
                    }
                    SafeAppendDebug($"External texture decode failed: {path}");
                    return null;
                }

                texture.name = label ?? Path.GetFileNameWithoutExtension(path);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                return texture;
            }
            catch (Exception ex)
            {
                if (logWarnings)
                {
                    _logger.LogWarning($"[CustomTextureReplacer] Could not load external texture '{path}': {ex.Message}");
                }
                SafeAppendDebug($"External texture load exception ({Path.GetFileName(path)}): {ex.Message}");
                return null;
            }
        }

        private void PopulateMaterialOverrides(Dictionary<string, object> entryDict, string configDirectory, string defaultBundleReference, MeshOverrideData data, HashSet<string> textureFolderCollector)
        {
            if (entryDict == null || data == null)
                return;

            if (!entryDict.TryGetValue("materials", out var value) || !(value is IList materials))
                return;

            foreach (var materialObj in materials)
            {
                if (!(materialObj is Dictionary<string, object> materialDict))
                    continue;

                var slot = GetInt(materialDict, "slot", 0);
                var assetName = GetString(materialDict, "material");
                if (string.IsNullOrWhiteSpace(assetName))
                    assetName = GetString(materialDict, "asset");

                if (string.IsNullOrWhiteSpace(assetName))
                {
                    SafeAppendDebug($"Material override skipped for {data.TargetName} (missing material asset).");
                    continue;
                }

                var bundleReference = GetString(materialDict, "bundle");
                if (string.IsNullOrWhiteSpace(bundleReference))
                    bundleReference = defaultBundleReference;

                string bundlePath = string.Empty;
                AssetBundle bundle = null;
                if (!string.IsNullOrWhiteSpace(bundleReference))
                {
                    if (!TryResolveMeshBundlePath(bundleReference, configDirectory, out bundlePath))
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Could not locate bundle '{bundleReference}' for material override '{assetName}' (target '{data.TargetName}').");
                        SafeAppendDebug($"Material override bundle missing ({bundleReference}) for {data.TargetName}");
                        continue;
                    }

                    bundle = GetOrLoadMeshBundle(bundlePath);
                    if (bundle == null)
                    {
                        _logger.LogWarning($"[CustomTextureReplacer] Failed to load bundle '{bundlePath}' for material override '{assetName}' (target '{data.TargetName}').");
                        SafeAppendDebug($"Material override bundle load failed: {bundlePath}");
                        continue;
                    }
                }

                Material sourceMaterial = null;
                try
                {
                    sourceMaterial = bundle != null ? bundle.LoadAsset<Material>(assetName) : Resources.Load<Material>(assetName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[CustomTextureReplacer] Exception loading material '{assetName}' for '{data.TargetName}': {ex.Message}");
                    SafeAppendDebug($"Material override load exception ({assetName}): {ex.Message}");
                    continue;
                }

                if (sourceMaterial == null)
                {
                    _logger.LogWarning($"[CustomTextureReplacer] Material asset '{assetName}' not found for override '{data.TargetName}'.");
                    SafeAppendDebug($"Material override missing asset {assetName} for {data.TargetName}");
                    continue;
                }

                var materialInstance = Instantiate(sourceMaterial);
                materialInstance.name = $"{data.TargetName}_Material_{slot}";

                var materialOverride = new MaterialOverrideData
                {
                    Slot = slot,
                    MaterialAssetName = assetName,
                    SourceBundlePath = bundlePath,
                    MaterialInstance = materialInstance
                };

                data.MaterialOverrides.Add(materialOverride);

                LoadMaterialTextureOverrides(materialDict, configDirectory, data, materialOverride, textureFolderCollector);
            }
        }

        private void LoadMaterialTextureOverrides(Dictionary<string, object> materialDict, string configDirectory, MeshOverrideData meshData, MaterialOverrideData materialOverride, HashSet<string> textureFolderCollector)
        {
            if (materialDict == null || materialOverride == null || materialOverride.MaterialInstance == null)
                return;

            if (!materialDict.TryGetValue("textures", out var texturesValue) || texturesValue == null)
                return;

            IList texturesList = texturesValue as IList;
            if (texturesList == null)
            {
                texturesList = new ArrayList { texturesValue };
            }

            foreach (var textureObj in texturesList)
            {
                if (!(textureObj is Dictionary<string, object> textureDict))
                    continue;

                var propertyName = GetString(textureDict, "property");
                if (string.IsNullOrWhiteSpace(propertyName))
                    propertyName = "_MainTex";

                var fileValue = GetString(textureDict, "file");
                if (string.IsNullOrWhiteSpace(fileValue))
                {
                    _logger.LogWarning($"[CustomTextureReplacer] Material override '{meshData?.TargetName}' texture entry missing 'file'.");
                    SafeAppendDebug($"Material texture override missing file for {meshData?.TargetName}");
                    continue;
                }

                var resolvedPath = ResolveRelativePath(configDirectory, fileValue);
                if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
                {
                    _logger.LogWarning($"[CustomTextureReplacer] Material override texture file '{fileValue}' (resolved '{resolvedPath}') not found for '{meshData?.TargetName}'.");
                    SafeAppendDebug($"Material texture file missing: {resolvedPath}");
                    continue;
                }

                var texture = LoadExternalTexture(resolvedPath, $"{meshData?.TargetName}_{Path.GetFileNameWithoutExtension(resolvedPath)}", logWarnings: true);
                if (texture == null)
                    continue;

                if (textureDict.TryGetValue("wrap", out var wrapValue))
                {
                    var wrapText = wrapValue?.ToString();
                    if (TryParseTextureWrapMode(wrapText, out var wrapMode))
                    {
                        texture.wrapMode = wrapMode;
                    }
                }

                if (textureDict.TryGetValue("filter", out var filterValue))
                {
                    var filterText = filterValue?.ToString();
                    if (TryParseFilterMode(filterText, out var filterMode))
                    {
                        texture.filterMode = filterMode;
                    }
                }

                if (textureDict.TryGetValue("anisoLevel", out var anisoValue))
                {
                    var anisoText = anisoValue?.ToString();
                    if (int.TryParse(anisoText, out var aniso) && aniso >= 0)
                    {
                        texture.anisoLevel = aniso;
                    }
                }

                if (!materialOverride.MaterialInstance.HasProperty(propertyName))
                {
                    _logger.LogWarning($"[CustomTextureReplacer] Material '{materialOverride.MaterialAssetName}' does not have property '{propertyName}'.");
                    SafeAppendDebug($"Material property missing ({propertyName}) on {materialOverride.MaterialAssetName}");
                }
                else
                {
                    materialOverride.MaterialInstance.SetTexture(propertyName, texture);
                }

                var assignment = new MaterialTextureAssignment
                {
                    PropertyName = propertyName,
                    FilePath = resolvedPath,
                    Texture = texture
                };

                materialOverride.TextureAssignments.Add(assignment);
                var directory = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    textureFolderCollector?.Add(directory);
                }
            }
        }

        private bool TryAutoSelectMeshFromBundle(MeshOverrideData data, AssetBundle bundle, bool logDetails)
        {
            if (data == null || bundle == null)
                return false;

            Mesh[] meshes;
            try
            {
                meshes = bundle.LoadAllAssets<Mesh>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Failed to enumerate meshes in bundle '{GetBundleDebugName(data.SourceBundlePath, bundle)}': {ex.Message}");
                SafeAppendDebug($"Auto mesh scan failed for {data.TargetName}: {ex.Message}");
                return false;
            }

            if (meshes == null || meshes.Length == 0)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Bundle '{GetBundleDebugName(data.SourceBundlePath, bundle)}' does not contain any Mesh assets for target '{data.TargetName}'.");
                SafeAppendDebug($"Auto mesh selection failed (no meshes) for {data.TargetName}");
                return false;
            }

            var candidate = SelectBestMeshCandidate(meshes, data.MeshSelectionHint, data.TargetName);
            if (candidate == null)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Unable to auto-select a mesh from bundle '{GetBundleDebugName(data.SourceBundlePath, bundle)}' for target '{data.TargetName}'. Provide 'mesh' or 'meshHint' in MeshOverrides.json.");
                SafeAppendDebug($"Auto mesh selection could not determine candidate for {data.TargetName}");
                return false;
            }

            data.MeshAssetName = candidate.name;
            if (logDetails)
            {
                _logger.LogInfo($"[CustomTextureReplacer] Auto-selected mesh '{data.MeshAssetName}' from bundle '{GetBundleDebugName(data.SourceBundlePath, bundle)}' for target '{data.TargetName}'.");
            }
            SafeAppendDebug($"Auto mesh selected '{candidate.name}' for {data.TargetName}");
            return true;
        }

        private Mesh SelectBestMeshCandidate(IEnumerable<Mesh> meshes, string hint, string targetName)
        {
            if (meshes == null)
                return null;

            Mesh candidate = null;
            var ordered = meshes
                .Where(mesh => mesh != null)
                .OrderByDescending(mesh => mesh.vertexCount)
                .ToList();

            if (!string.IsNullOrWhiteSpace(hint))
            {
                candidate = ordered.FirstOrDefault(mesh => mesh.name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);
                if (candidate != null)
                    return candidate;
            }

            if (!string.IsNullOrWhiteSpace(targetName))
            {
                candidate = ordered.FirstOrDefault(mesh => mesh.name.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (candidate != null)
                    return candidate;
            }

            if (ordered.Count == 1)
                return ordered[0];

            candidate = ordered.FirstOrDefault(mesh =>
                !mesh.name.EndsWith("Collider", StringComparison.OrdinalIgnoreCase) &&
                !mesh.name.EndsWith("_LOD1", StringComparison.OrdinalIgnoreCase) &&
                !mesh.name.EndsWith("_LOD2", StringComparison.OrdinalIgnoreCase));

            return candidate ?? ordered.First();
        }

        private bool TryAutoPopulateMaterials(MeshOverrideData data, AssetBundle bundle, int subMeshCount, bool logDetails)
        {
            if (data == null || bundle == null)
                return false;

            Material[] materials;
            try
            {
                materials = bundle.LoadAllAssets<Material>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Failed to enumerate materials in bundle '{GetBundleDebugName(data.SourceBundlePath, bundle)}': {ex.Message}");
                SafeAppendDebug($"Auto material scan failed for {data.TargetName}: {ex.Message}");
                return false;
            }

            if (materials == null || materials.Length == 0)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Bundle '{GetBundleDebugName(data.SourceBundlePath, bundle)}' does not contain any Material assets for target '{data.TargetName}'.");
                SafeAppendDebug($"Auto material selection failed (no materials) for {data.TargetName}");
                return false;
            }

            var selection = new List<Material>();
            if (data.MaterialSelectionHints != null)
            {
                foreach (var hint in data.MaterialSelectionHints)
                {
                    if (string.IsNullOrWhiteSpace(hint))
                        continue;
                    var match = materials.FirstOrDefault(mat => mat != null && mat.name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match != null && !selection.Contains(match))
                        selection.Add(match);
                }
            }

            foreach (var mat in materials)
            {
                if (mat != null && !selection.Contains(mat))
                    selection.Add(mat);
            }

            if (subMeshCount <= 0)
                subMeshCount = 1;

            var required = Math.Min(selection.Count, subMeshCount);
            if (required == 0)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Unable to auto-select materials from bundle '{GetBundleDebugName(data.SourceBundlePath, bundle)}' for target '{data.TargetName}'. Provide explicit 'materials' or 'materialHints'.");
                SafeAppendDebug($"Auto material selection produced no candidates for {data.TargetName}");
                return false;
            }

            for (int slot = 0; slot < required; slot++)
            {
                var materialAsset = selection[slot];
                if (materialAsset == null)
                    continue;
                AddMaterialOverrideFromAsset(data, materialAsset, slot, logDetails);
            }

            data.AutoSelectMaterials = false;
            return true;
        }

        private void AddMaterialOverrideFromAsset(MeshOverrideData data, Material sourceMaterial, int slot, bool logDetails)
        {
            if (data == null || sourceMaterial == null)
                return;

            Material instance = null;
            try
            {
                instance = Instantiate(sourceMaterial);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Failed to instantiate material '{sourceMaterial.name}' for '{data.TargetName}': {ex.Message}");
                SafeAppendDebug($"Auto material instantiate failed for {sourceMaterial.name}: {ex.Message}");
                return;
            }

            instance.name = sourceMaterial.name;

            var materialOverride = new MaterialOverrideData
            {
                Slot = slot,
                MaterialAssetName = sourceMaterial.name,
                SourceBundlePath = data.SourceBundlePath,
                MaterialInstance = instance
            };

            data.MaterialOverrides.Add(materialOverride);

            if (logDetails)
            {
                _logger.LogInfo($"[CustomTextureReplacer] Auto-selected material '{sourceMaterial.name}' for slot {slot} (target '{data.TargetName}').");
            }

            SafeAppendDebug($"Auto material selected '{sourceMaterial.name}' (slot {slot}) for {data.TargetName}");
        }

        private static string GetBundleDebugName(string path, AssetBundle bundle)
        {
            if (bundle != null && !string.IsNullOrEmpty(bundle.name))
                return bundle.name;

            if (!string.IsNullOrEmpty(path))
                return Path.GetFileName(path);

            return "<bundle>";
        }

        private static bool TryParseTextureWrapMode(string value, out TextureWrapMode mode)
        {
            if (!string.IsNullOrEmpty(value) && Enum.TryParse(value, ignoreCase: true, out mode))
                return true;
            mode = TextureWrapMode.Clamp;
            return false;
        }

        private static bool TryParseFilterMode(string value, out FilterMode mode)
        {
            if (!string.IsNullOrEmpty(value) && Enum.TryParse(value, ignoreCase: true, out mode))
                return true;
            mode = FilterMode.Bilinear;
            return false;
        }

        private void SetupMeshOverridesWatcher()
        {
            if (string.IsNullOrEmpty(_meshOverridesPath))
                return;

            try
            {
                if (_meshOverridesWatcher != null)
                {
                    _meshOverridesWatcher.EnableRaisingEvents = false;
                    _meshOverridesWatcher.Changed -= OnMeshOverridesFileChanged;
                    _meshOverridesWatcher.Created -= OnMeshOverridesFileChanged;
                    _meshOverridesWatcher.Deleted -= OnMeshOverridesFileChanged;
                    _meshOverridesWatcher.Renamed -= OnMeshOverridesFileRenamed;
                    _meshOverridesWatcher.Dispose();
                    _meshOverridesWatcher = null;
                }

                var directory = Path.GetDirectoryName(_meshOverridesPath);
                var filename = Path.GetFileName(_meshOverridesPath);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(filename))
                    return;

                Directory.CreateDirectory(directory);

                var watcher = new FileSystemWatcher(directory, filename)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };

                watcher.Changed += OnMeshOverridesFileChanged;
                watcher.Created += OnMeshOverridesFileChanged;
                watcher.Deleted += OnMeshOverridesFileChanged;
                watcher.Renamed += OnMeshOverridesFileRenamed;
                watcher.EnableRaisingEvents = true;

                _meshOverridesWatcher = watcher;
                SafeAppendDebug($"MeshOverrides watcher initialised for '{_meshOverridesPath}'.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Could not watch mesh override file: {ex.Message}");
                SafeAppendDebug($"MeshOverrides watcher exception: {ex.Message}");
            }
        }

        private void OnMeshOverridesFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!IsMeshOverridesPath(e.FullPath))
                return;

            _meshOverridesReloadRequested = true;
        }

        private void OnMeshOverridesFileRenamed(object sender, RenamedEventArgs e)
        {
            if (IsMeshOverridesPath(e.OldFullPath))
            {
                _meshOverridesPath = e.FullPath;
                _meshOverridesReloadRequested = true;
                SetupMeshOverridesWatcher();
                return;
            }

            if (IsMeshOverridesPath(e.FullPath))
            {
                _meshOverridesReloadRequested = true;
            }
        }

        private bool IsMeshOverridesPath(string candidate)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(_meshOverridesPath))
                return false;

            try
            {
                return string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(_meshOverridesPath), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool TryResolveMeshBundlePath(string bundleReference, string baseDirectory, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(bundleReference))
                return false;

            var candidates = new List<string>();

            if (Path.IsPathRooted(bundleReference))
            {
                candidates.Add(bundleReference);
            }
            else
            {
                if (!string.IsNullOrEmpty(baseDirectory))
                    candidates.Add(Path.Combine(baseDirectory, bundleReference));

                if (_textureFolders != null)
                {
                    foreach (var folder in _textureFolders)
                    {
                        if (string.IsNullOrWhiteSpace(folder))
                            continue;
                        candidates.Add(Path.Combine(folder, bundleReference));
                    }
                }

                candidates.Add(Path.Combine(Paths.PluginPath, bundleReference));
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    var full = NormalizePath(candidate);
                    if (File.Exists(full))
                    {
                        resolvedPath = full;
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private AssetBundle GetOrLoadMeshBundle(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return null;

            if (_meshAssetBundles.TryGetValue(fullPath, out var cached) && cached != null)
                return cached;

            try
            {
                var bundle = AssetBundle.LoadFromFile(fullPath);
                if (bundle == null)
                    return null;

                _meshAssetBundles[fullPath] = bundle;
                RegisterMeshBundleWatcher(fullPath);
                SafeAppendDebug($"Mesh bundle loaded: {fullPath}");
                return bundle;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Failed to load asset bundle '{fullPath}': {ex.Message}");
                SafeAppendDebug($"Mesh bundle load exception ({Path.GetFileName(fullPath)}): {ex.Message}");
                return null;
            }
        }

        private void RegisterMeshBundleWatcher(string bundlePath)
        {
            if (string.IsNullOrEmpty(bundlePath))
                return;

            if (_meshBundleWatchers.ContainsKey(bundlePath))
                return;

            try
            {
                var directory = Path.GetDirectoryName(bundlePath);
                var filename = Path.GetFileName(bundlePath);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(filename))
                    return;

                var watcher = new FileSystemWatcher(directory, filename)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };

                watcher.Changed += OnMeshBundleFileChanged;
                watcher.Created += OnMeshBundleFileChanged;
                watcher.Deleted += OnMeshBundleFileChanged;
                watcher.Renamed += OnMeshBundleFileRenamed;
                watcher.EnableRaisingEvents = true;

                _meshBundleWatchers[bundlePath] = watcher;
                SafeAppendDebug($"Mesh bundle watcher initialised for '{bundlePath}'.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Could not watch mesh bundle '{bundlePath}': {ex.Message}");
                SafeAppendDebug($"Mesh bundle watcher exception ({Path.GetFileName(bundlePath)}): {ex.Message}");
            }
        }

        private void OnMeshBundleFileChanged(object sender, FileSystemEventArgs e)
        {
            _meshOverridesReloadRequested = true;
        }

        private void OnMeshBundleFileRenamed(object sender, RenamedEventArgs e)
        {
            _meshOverridesReloadRequested = true;
        }

        private void DisposeMeshOverrideResources()
        {
            foreach (var entry in _meshOverridesByTarget.Values)
            {
                if (entry?.MeshInstance != null)
                {
                    Destroy(entry.MeshInstance);
                }

                if (entry?.MaterialOverrides != null)
                {
                    foreach (var materialOverride in entry.MaterialOverrides)
                    {
                        if (materialOverride?.MaterialInstance != null)
                        {
                            Destroy(materialOverride.MaterialInstance);
                        }

                        if (materialOverride?.TextureAssignments != null)
                        {
                            foreach (var assignment in materialOverride.TextureAssignments)
                            {
                                if (assignment?.Texture != null)
                                {
                                    Destroy(assignment.Texture);
                                }
                            }
                        }
                    }
                }
            }
            _meshOverridesByTarget.Clear();

            foreach (var bundle in _meshAssetBundles.Values)
            {
                try
                {
                    bundle?.Unload(unloadAllLoadedObjects: true);
                }
                catch (Exception ex)
                {
                    SafeAppendDebug($"Mesh bundle unload exception: {ex.Message}");
                }
            }
            _meshAssetBundles.Clear();

            foreach (var watcher in _meshBundleWatchers.Values)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Changed -= OnMeshBundleFileChanged;
                    watcher.Created -= OnMeshBundleFileChanged;
                    watcher.Deleted -= OnMeshBundleFileChanged;
                    watcher.Renamed -= OnMeshBundleFileRenamed;
                    watcher.Dispose();
                }
                catch
                {
                }
            }
            _meshBundleWatchers.Clear();
        }

        private void RevertAllMeshOverrides()
        {
            RestoreMeshFilters();
            RestoreSkinnedRenderers();
            RestoreMeshColliders();
            RestoreAllRendererMaterials();
        }

        private void RestoreMeshFilters()
        {
            foreach (var pair in _meshFilterReferences)
            {
                if (pair.Value.TryGetTarget(out var filter) && filter != null)
                {
                    if (_meshFilterOriginalMeshes.TryGetValue(pair.Key, out var original) && original != null)
                    {
                        if (!ReferenceEquals(filter.sharedMesh, original))
                            filter.sharedMesh = original;
                    }
                }
            }
            _meshFilterOverrideIds.Clear();
        }

        private void RestoreSkinnedRenderers()
        {
            foreach (var pair in _skinnedRendererReferences)
            {
                if (pair.Value.TryGetTarget(out var renderer) && renderer != null)
                {
                    if (_skinnedOriginalMeshes.TryGetValue(pair.Key, out var original) && original != null)
                    {
                        if (!ReferenceEquals(renderer.sharedMesh, original))
                            renderer.sharedMesh = original;
                    }
                }
            }
            _skinnedRendererOverrideIds.Clear();
        }

        private void RestoreMeshColliders()
        {
            foreach (var pair in _meshColliderReferences)
            {
                if (pair.Value.TryGetTarget(out var collider) && collider != null)
                {
                    if (_meshColliderOriginalMeshes.TryGetValue(pair.Key, out var original) && original != null)
                    {
                        if (!ReferenceEquals(collider.sharedMesh, original))
                            collider.sharedMesh = original;
                    }
                }
            }
            _meshColliderOverrideIds.Clear();
        }

        private void RestoreAllRendererMaterials()
        {
            foreach (var pair in _rendererReferences)
            {
                if (pair.Value.TryGetTarget(out var renderer) && renderer != null)
                {
                    if (_rendererOriginalMaterials.TryGetValue(pair.Key, out var originals) && originals != null)
                    {
                        renderer.sharedMaterials = originals;
                    }
                }
            }

            _rendererOverrideIds.Clear();
        }

        private void ApplyMeshOverrides()
        {
            if (!_meshOverridesDirty)
                return;

            _meshOverridesDirty = false;

            ApplyMeshOverridesToMeshFilters();
            ApplyMeshOverridesToSkinnedRenderers();
            ApplyMeshOverridesToMeshColliders();
            CleanupMeshFilterEntries();
            CleanupSkinnedRendererEntries();
            CleanupMeshColliderEntries();
            CleanupRendererEntries();
        }

        private void ApplyMeshOverridesToMeshFilters()
        {
            foreach (var filter in Resources.FindObjectsOfTypeAll<MeshFilter>())
            {
                if (filter == null)
                    continue;

                var id = filter.GetInstanceID();
                if (!_meshFilterReferences.ContainsKey(id))
                {
                    _meshFilterReferences[id] = new WeakReference<MeshFilter>(filter);
                    _meshFilterOriginalMeshes[id] = filter.sharedMesh;
                }

                if (!_meshFilterOriginalMeshes.TryGetValue(id, out var originalMesh))
                {
                    originalMesh = filter.sharedMesh;
                    _meshFilterOriginalMeshes[id] = originalMesh;
                }

                var targetName = originalMesh != null ? originalMesh.name : filter.sharedMesh?.name;
                if (!string.IsNullOrEmpty(targetName) &&
                    _meshOverridesByTarget.TryGetValue(targetName, out var overrideData) &&
                    overrideData != null &&
                    overrideData.ApplyToMeshFilter)
                {
                    var replacement = overrideData.MeshInstance;
                    if (replacement != null && !ReferenceEquals(filter.sharedMesh, replacement))
                    {
                        filter.sharedMesh = replacement;
                    }

                    _meshFilterOverrideIds.Add(id);

                    var renderer = filter.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        ApplyRendererMaterialOverrides(renderer, overrideData);
                    }
                }
                else if (_meshFilterOverrideIds.Remove(id))
                {
                    if (originalMesh != null && !ReferenceEquals(filter.sharedMesh, originalMesh))
                    {
                        filter.sharedMesh = originalMesh;
                    }

                    var renderer = filter.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        RestoreRendererMaterials(renderer);
                    }
                }
            }
        }

        private void ApplyMeshOverridesToSkinnedRenderers()
        {
            foreach (var renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
            {
                if (renderer == null)
                    continue;

                var id = renderer.GetInstanceID();
                if (!_skinnedRendererReferences.ContainsKey(id))
                {
                    _skinnedRendererReferences[id] = new WeakReference<SkinnedMeshRenderer>(renderer);
                    _skinnedOriginalMeshes[id] = renderer.sharedMesh;
                }

                if (!_skinnedOriginalMeshes.TryGetValue(id, out var originalMesh))
                {
                    originalMesh = renderer.sharedMesh;
                    _skinnedOriginalMeshes[id] = originalMesh;
                }

                var targetName = originalMesh != null ? originalMesh.name : renderer.sharedMesh?.name;
                if (!string.IsNullOrEmpty(targetName) &&
                    _meshOverridesByTarget.TryGetValue(targetName, out var overrideData) &&
                    overrideData != null &&
                    overrideData.ApplyToSkinnedMeshRenderer)
                {
                    var replacement = overrideData.MeshInstance;
                    if (replacement != null && !ReferenceEquals(renderer.sharedMesh, replacement))
                    {
                        renderer.sharedMesh = replacement;
                    }

                    _skinnedRendererOverrideIds.Add(id);
                    ApplyRendererMaterialOverrides(renderer, overrideData);
                }
                else if (_skinnedRendererOverrideIds.Remove(id))
                {
                    if (originalMesh != null && !ReferenceEquals(renderer.sharedMesh, originalMesh))
                    {
                        renderer.sharedMesh = originalMesh;
                    }
                    RestoreRendererMaterials(renderer);
                }
            }
        }

        private void ApplyMeshOverridesToMeshColliders()
        {
            foreach (var collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
            {
                if (collider == null)
                    continue;

                var id = collider.GetInstanceID();
                if (!_meshColliderReferences.ContainsKey(id))
                {
                    _meshColliderReferences[id] = new WeakReference<MeshCollider>(collider);
                    _meshColliderOriginalMeshes[id] = collider.sharedMesh;
                }

                if (!_meshColliderOriginalMeshes.TryGetValue(id, out var originalMesh))
                {
                    originalMesh = collider.sharedMesh;
                    _meshColliderOriginalMeshes[id] = originalMesh;
                }

                var targetName = originalMesh != null ? originalMesh.name : collider.sharedMesh?.name;
                if (!string.IsNullOrEmpty(targetName) &&
                    _meshOverridesByTarget.TryGetValue(targetName, out var overrideData) &&
                    overrideData != null &&
                    overrideData.ApplyToMeshCollider)
                {
                    var replacement = overrideData.MeshInstance;
                    if (replacement != null && !ReferenceEquals(collider.sharedMesh, replacement))
                    {
                        collider.sharedMesh = replacement;
                    }

                    _meshColliderOverrideIds.Add(id);
                }
                else if (_meshColliderOverrideIds.Remove(id))
                {
                    if (originalMesh != null && !ReferenceEquals(collider.sharedMesh, originalMesh))
                    {
                        collider.sharedMesh = originalMesh;
                    }
                }
            }
        }

        private void CleanupMeshFilterEntries()
        {
            var toRemove = new List<int>();
            foreach (var pair in _meshFilterReferences)
            {
                if (!pair.Value.TryGetTarget(out var filter) || filter == null)
                {
                    toRemove.Add(pair.Key);
                }
            }

            foreach (var id in toRemove)
            {
                _meshFilterReferences.Remove(id);
                _meshFilterOriginalMeshes.Remove(id);
                _meshFilterOverrideIds.Remove(id);
            }
        }

        private void CleanupSkinnedRendererEntries()
        {
            var toRemove = new List<int>();
            foreach (var pair in _skinnedRendererReferences)
            {
                if (!pair.Value.TryGetTarget(out var renderer) || renderer == null)
                {
                    toRemove.Add(pair.Key);
                }
            }

            foreach (var id in toRemove)
            {
                _skinnedRendererReferences.Remove(id);
                _skinnedOriginalMeshes.Remove(id);
                _skinnedRendererOverrideIds.Remove(id);
            }
        }

        private void CleanupMeshColliderEntries()
        {
            var toRemove = new List<int>();
            foreach (var pair in _meshColliderReferences)
            {
                if (!pair.Value.TryGetTarget(out var collider) || collider == null)
                {
                    toRemove.Add(pair.Key);
                }
            }

            foreach (var id in toRemove)
            {
                _meshColliderReferences.Remove(id);
                _meshColliderOriginalMeshes.Remove(id);
                _meshColliderOverrideIds.Remove(id);
            }
        }

        private void CleanupRendererEntries()
        {
            var toRemove = new List<int>();
            foreach (var pair in _rendererReferences)
            {
                if (!pair.Value.TryGetTarget(out var renderer) || renderer == null)
                {
                    toRemove.Add(pair.Key);
                }
            }

            foreach (var id in toRemove)
            {
                _rendererReferences.Remove(id);
                _rendererOriginalMaterials.Remove(id);
                _rendererOverrideIds.Remove(id);
            }
        }

        private void ApplyRendererMaterialOverrides(Renderer renderer, MeshOverrideData data)
        {
            if (renderer == null || data == null || data.MaterialOverrides.Count == 0)
                return;

            var id = renderer.GetInstanceID();
            if (!_rendererReferences.ContainsKey(id))
            {
                _rendererReferences[id] = new WeakReference<Renderer>(renderer);
            }

            if (!_rendererOriginalMaterials.ContainsKey(id))
            {
                var originals = renderer.sharedMaterials ?? Array.Empty<Material>();
                var copy = new Material[originals.Length];
                Array.Copy(originals, copy, originals.Length);
                _rendererOriginalMaterials[id] = copy;
            }

            var working = renderer.sharedMaterials ?? Array.Empty<Material>();
            var maxSlot = 0;
            foreach (var materialOverride in data.MaterialOverrides)
            {
                if (materialOverride == null)
                    continue;
                if (materialOverride.Slot > maxSlot)
                    maxSlot = materialOverride.Slot;
            }

            var requiredLength = Math.Max(working.Length, maxSlot + 1);
            var updated = new Material[requiredLength];
            if (working.Length > 0)
            {
                Array.Copy(working, updated, working.Length);
            }

            var changed = false;

            foreach (var materialOverride in data.MaterialOverrides)
            {
                if (materialOverride == null || materialOverride.MaterialInstance == null)
                    continue;

                var slot = materialOverride.Slot;
                if (slot < 0 || slot >= updated.Length)
                {
                    _logger.LogWarning($"[CustomTextureReplacer] Material slot {slot} out of range for renderer '{renderer.name}' (target '{data.TargetName}').");
                    continue;
                }

                if (!ReferenceEquals(updated[slot], materialOverride.MaterialInstance))
                {
                    updated[slot] = materialOverride.MaterialInstance;
                    changed = true;
                }
            }

            if (changed)
            {
                renderer.sharedMaterials = updated;
                _rendererOverrideIds.Add(id);
            }
        }

        private void RestoreRendererMaterials(Renderer renderer)
        {
            if (renderer == null)
                return;

            var id = renderer.GetInstanceID();
            if (_rendererOverrideIds.Remove(id))
            {
                if (_rendererOriginalMaterials.TryGetValue(id, out var originals) && originals != null)
                {
                    renderer.sharedMaterials = originals;
                }
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

        private sealed class MeshOverrideData
        {
            public string TargetName;
            public string MeshAssetName;
            public string MeshSelectionHint;
            public bool AutoSelectMesh;
            public bool AutoSelectMaterials;
            public string[] MaterialSelectionHints = Array.Empty<string>();
            public string SourceBundlePath;
            public Mesh MeshInstance;
            public bool ApplyToMeshFilter;
            public bool ApplyToSkinnedMeshRenderer;
            public bool ApplyToMeshCollider;
            public List<MaterialOverrideData> MaterialOverrides { get; } = new List<MaterialOverrideData>();
        }

        private sealed class MaterialOverrideData
        {
            public int Slot;
            public string MaterialAssetName;
            public string SourceBundlePath;
            public Material MaterialInstance;
            public List<MaterialTextureAssignment> TextureAssignments { get; } = new List<MaterialTextureAssignment>();
        }

        private sealed class MaterialTextureAssignment
        {
            public string PropertyName;
            public string FilePath;
            public Texture Texture;
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
            else if (asset is Mesh mesh)
            {
                if (_logAssetLoads.Value)
                {
                    _logger.LogInfo($"[CustomTextureReplacer] {context}: Mesh '{mesh.name}' {detail}");
                }

                SafeAppendDebug($"Asset load ({context}) mesh {mesh.name}");
                _meshOverridesDirty = true;
                RequestReapply(context);
            }
            else if (asset is Material material)
            {
                if (_logAssetLoads.Value)
                {
                    _logger.LogInfo($"[CustomTextureReplacer] {context}: Material '{material.name}' {detail}");
                }

                SafeAppendDebug($"Asset load ({context}) material {material.name}");
                _meshOverridesDirty = true;
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

        private void RegisterCardOverrideAlias(CardTextOverride entry, string alias, string source)
        {
            if (entry == null || string.IsNullOrWhiteSpace(alias))
                return;

            var trimmed = alias.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return;

            if (_cardOverrides.TryGetValue(trimmed, out var existing) && !ReferenceEquals(existing, entry))
                return;

            if (_cardOverrides.ContainsKey(trimmed))
                return;

            _cardOverrides[trimmed] = entry;
            if (!string.IsNullOrEmpty(source))
            {
                SafeAppendDebug($"CardOverrides alias '{trimmed}' mapped to '{entry.Id}' via {source}.");
            }
            else
            {
                SafeAppendDebug($"CardOverrides alias '{trimmed}' mapped to '{entry.Id}'.");
            }
        }

        private void LoadCardOverrides(bool logDetails)
        {
            _cardOverrides.Clear();

            if (string.IsNullOrEmpty(_cardOverridesPath))
                return;

            if (!File.Exists(_cardOverridesPath))
            {
                if (logDetails)
                    _logger.LogInfo($"[CustomTextureReplacer] Card override file not found at '{_cardOverridesPath}'.");
                SafeAppendDebug("CardOverrides file missing.");
                return;
            }

            try
            {
                var json = File.ReadAllText(_cardOverridesPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    if (logDetails)
                        _logger.LogInfo($"[CustomTextureReplacer] Card override file '{_cardOverridesPath}' is empty.");
                    SafeAppendDebug("CardOverrides file empty.");
                    return;
                }

                json = json.Trim();
                SafeAppendDebug($"CardOverrides JSON length: {json.Length}");
                if (json.StartsWith("[", StringComparison.Ordinal))
                {
                    json = "{ \"entries\": " + json + " }";
                }

                var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
                if (root == null)
                {
                    SafeAppendDebug("CardOverrides JSON root was null or not an object.");
                }
                else if (root.TryGetValue("entries", out var entriesObj) && entriesObj is IList entriesList)
                {
                    foreach (var entryObj in entriesList)
                    {
                        if (entryObj is not Dictionary<string, object> entryDict)
                            continue;

                        var id = GetString(entryDict, "Id");
                        if (string.IsNullOrEmpty(id))
                            continue;

                        var trimmedId = id.Trim();
                        if (string.IsNullOrEmpty(trimmedId))
                            continue;

                        var entry = new CardTextOverride
                        {
                            Id = trimmedId,
                            DisplayName = GetString(entryDict, "DisplayName"),
                            Subtitle = GetString(entryDict, "Subtitle"),
                            CardNumber = GetString(entryDict, "CardNumber"),
                            Description = GetString(entryDict, "Description"),
                            EvolvesFrom = GetString(entryDict, "EvolvesFrom"),
                            Artist = GetString(entryDict, "Artist"),
                            Stat1 = GetString(entryDict, "Stat1"),
                            Stat2 = GetString(entryDict, "Stat2"),
                            Stat3 = GetString(entryDict, "Stat3"),
                            Stat4 = GetString(entryDict, "Stat4"),
                            Rarity = GetString(entryDict, "Rarity"),
                            Fame = GetString(entryDict, "Fame"),
                            Aliases = GetStringArray(entryDict, "Aliases")
                        };

                        _cardOverrides[trimmedId] = entry;
                        SafeAppendDebug($"CardOverrides added entry for '{trimmedId}'.");

                        RegisterCardOverrideAlias(entry, entry.DisplayName, "display");

                        if (entry.Aliases != null && entry.Aliases.Length > 0)
                        {
                            foreach (var alias in entry.Aliases)
                            {
                                RegisterCardOverrideAlias(entry, alias, "alias");
                            }
                        }
                    }
                }
                else
                {
                    SafeAppendDebug("CardOverrides 'entries' array missing or invalid.");
                }

                if (_capturedCardData.Count > 0)
                {
                    foreach (var snapshot in _capturedCardData.Values)
                    {
                        if (snapshot == null || string.IsNullOrEmpty(snapshot.Id))
                            continue;

                        if (!_cardOverrides.TryGetValue(snapshot.Id, out var overrideEntry) || overrideEntry == null)
                            continue;

                        RegisterCardOverrideAlias(overrideEntry, snapshot.DisplayName, "capture");

                        if (snapshot.Aliases != null && snapshot.Aliases.Length > 0)
                        {
                            foreach (var alias in snapshot.Aliases)
                            {
                                RegisterCardOverrideAlias(overrideEntry, alias, "capture");
                            }
                        }
                    }
                }

                if (logDetails)
                    _logger.LogInfo($"[CustomTextureReplacer] Loaded {_cardOverrides.Count} card override(s) from '{_cardOverridesPath}'.");
                SafeAppendDebug($"CardOverrides load complete: {_cardOverrides.Count} entries");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Failed to load card overrides from '{_cardOverridesPath}': {ex.Message}");
                SafeAppendDebug($"CardOverrides load exception: {ex.Message}");
            }
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            if (dict == null || string.IsNullOrEmpty(key))
                return string.Empty;

            if (dict.TryGetValue(key, out var value) && value != null)
                return value.ToString();

            return string.Empty;
        }

        private static string[] GetStringArray(Dictionary<string, object> dict, string key)
        {
            if (dict == null || string.IsNullOrEmpty(key))
                return Array.Empty<string>();

            if (!dict.TryGetValue(key, out var value) || value == null)
                return Array.Empty<string>();

            if (value is IList list)
            {
                var result = new List<string>(list.Count);
                foreach (var item in list)
                {
                    if (item == null)
                        continue;
                    var s = item.ToString();
                    if (!string.IsNullOrEmpty(s))
                        result.Add(s);
                }
                return result.ToArray();
            }

            var single = value.ToString();
            return string.IsNullOrEmpty(single) ? Array.Empty<string>() : new[] { single };
        }

        private static bool GetBool(Dictionary<string, object> dict, string key, bool defaultValue = false)
        {
            if (dict == null || string.IsNullOrEmpty(key))
                return defaultValue;

            if (!dict.TryGetValue(key, out var value) || value == null)
                return defaultValue;

            if (value is bool b)
                return b;

            var text = value.ToString();
            if (bool.TryParse(text, out var parsed))
                return parsed;

            if (int.TryParse(text, out var number))
                return number != 0;

            return defaultValue;
        }

        private static int GetInt(Dictionary<string, object> dict, string key, int defaultValue = 0)
        {
            if (dict == null || string.IsNullOrEmpty(key))
                return defaultValue;

            if (!dict.TryGetValue(key, out var value) || value == null)
                return defaultValue;

            if (value is int i)
                return i;

            if (value is long l)
                return (int)l;

            if (value is double d)
                return (int)d;

            var text = value.ToString();
            return int.TryParse(text, out var parsed) ? parsed : defaultValue;
        }

        private bool ResolveCardOverridesPath()
        {
            var previous = _cardOverridesPath ?? string.Empty;
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    return;
                if (seen.Add(candidate))
                    candidates.Add(candidate);
            }

            if (_textureFolders != null)
            {
                foreach (var folder in _textureFolders)
                {
                    if (string.IsNullOrWhiteSpace(folder))
                        continue;
                    AddCandidate(Path.Combine(folder, "CardOverrides.json"));
                }
            }

            AddCandidate(Path.Combine(Paths.PluginPath, "CustomTextures", "CardOverrides.json"));

            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath;
            AddCandidate(Path.Combine(assemblyDir, "CardOverrides.json"));
            AddCandidate(Path.Combine(Paths.PluginPath, "CardOverrides.json"));

            var resolved = candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault() ?? Path.Combine(Paths.PluginPath, "CardOverrides.json");

            var changed = !string.Equals(previous, resolved, StringComparison.OrdinalIgnoreCase);
            _cardOverridesPath = resolved;

            if (changed)
            {
                SafeAppendDebug($"CardOverrides path set to '{resolved}'.");
                _logger.LogInfo($"[CustomTextureReplacer] CardOverrides path set to '{resolved}'.");
            }

            return changed;
        }

        private void SetupCardOverridesWatcher()
        {
            try
            {
                if (_cardOverridesWatcher != null)
                {
                    _cardOverridesWatcher.EnableRaisingEvents = false;
                    _cardOverridesWatcher.Changed -= OnCardOverridesFileChanged;
                    _cardOverridesWatcher.Created -= OnCardOverridesFileChanged;
                    _cardOverridesWatcher.Deleted -= OnCardOverridesFileChanged;
                    _cardOverridesWatcher.Renamed -= OnCardOverridesFileRenamed;
                    _cardOverridesWatcher.Dispose();
                    _cardOverridesWatcher = null;
                }

                if (string.IsNullOrEmpty(_cardOverridesPath))
                    return;

                var directory = Path.GetDirectoryName(_cardOverridesPath);
                if (string.IsNullOrEmpty(directory))
                    return;

                Directory.CreateDirectory(directory);

                var fileName = Path.GetFileName(_cardOverridesPath);
                if (string.IsNullOrEmpty(fileName))
                    fileName = "CardOverrides.json";

                var watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };

                watcher.Changed += OnCardOverridesFileChanged;
                watcher.Created += OnCardOverridesFileChanged;
                watcher.Deleted += OnCardOverridesFileChanged;
                watcher.Renamed += OnCardOverridesFileRenamed;
                watcher.EnableRaisingEvents = true;

                _cardOverridesWatcher = watcher;
                SafeAppendDebug($"CardOverrides watcher initialised for '{_cardOverridesPath}'.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Could not watch card override file: {ex.Message}");
                SafeAppendDebug($"CardOverrides watcher exception: {ex.Message}");
            }
        }

        private void OnCardOverridesFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!IsCardOverridesPath(e.FullPath))
                return;

            _cardOverridesReloadRequested = true;
        }

        private void OnCardOverridesFileRenamed(object sender, RenamedEventArgs e)
        {
            if (IsCardOverridesPath(e.OldFullPath))
            {
                _cardOverridesPath = e.FullPath;
                _cardOverridesReloadRequested = true;
                SetupCardOverridesWatcher();
                return;
            }

            if (IsCardOverridesPath(e.FullPath))
            {
                _cardOverridesReloadRequested = true;
            }
        }

        private bool IsCardOverridesPath(string candidate)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(_cardOverridesPath))
                return false;

            try
            {
                return string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(_cardOverridesPath), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void ReapplyCardOverrides()
        {
            if (_cardOverrides.Count == 0)
                return;

            try
            {
                foreach (var cardUI in Resources.FindObjectsOfTypeAll<CardUI>())
                {
                    ApplyCardOverrides(cardUI);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[CustomTextureReplacer] ReapplyCardOverrides failed: {ex.Message}");
                SafeAppendDebug($"ReapplyCardOverrides exception: {ex.Message}");
            }
        }

        internal void ApplyCardOverrides(CardUI cardUI)
        {
            if (cardUI == null || _cardOverrides.Count == 0)
                return;

            try
            {
                var key = ResolveMonsterKey(cardUI);
                if (string.IsNullOrEmpty(key))
                    return;

                if (!_cardOverrides.TryGetValue(key, out var entry) || entry == null)
                    return;

                SetCardUIText(cardUI, CardUIBindings.MonsterNameTextField, entry.DisplayName);
                SetCardUIText(cardUI, CardUIBindings.NumberTextField, entry.CardNumber);
                SetCardUIText(cardUI, CardUIBindings.SubtitleTextField, entry.Subtitle);
                SetCardUIText(cardUI, CardUIBindings.DescriptionTextField, entry.Description);
                SetCardUIText(cardUI, CardUIBindings.ArtistTextField, entry.Artist);
                SetCardUIText(cardUI, CardUIBindings.RarityTextField, entry.Rarity);
                SetCardUIText(cardUI, CardUIBindings.FameTextField, entry.Fame);
                SetCardUIText(cardUI, CardUIBindings.Stat1TextField, entry.Stat1);
                SetCardUIText(cardUI, CardUIBindings.Stat2TextField, entry.Stat2);
                SetCardUIText(cardUI, CardUIBindings.Stat3TextField, entry.Stat3);
                SetCardUIText(cardUI, CardUIBindings.Stat4TextField, entry.Stat4);
                SetCardUIText(cardUI, CardUIBindings.EvoPreviousStageNameTextField, entry.EvolvesFrom);

                if (CardUIBindings.EvoGroupField?.GetValue(cardUI) is GameObject evoGroup)
                {
                    evoGroup.SetActive(!string.IsNullOrEmpty(entry?.EvolvesFrom));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[CustomTextureReplacer] ApplyCardOverrides failed: {ex.Message}");
                SafeAppendDebug($"ApplyCardOverrides exception: {ex.Message}");
            }
        }

        internal void CaptureCardData(CardUI cardUI)
        {
            if (cardUI == null)
                return;

            var monster = CardUIBindings.MonsterDataField?.GetValue(cardUI);
            var key = TryGetMemberString(monster, "MonsterType", "monsterType");
            if (string.IsNullOrEmpty(key))
                return;

            if (!_capturedCardData.TryGetValue(key, out var snapshot) || snapshot == null)
            {
                snapshot = new CardTextOverride { Id = key };
                _capturedCardData[key] = snapshot;
                SafeAppendDebug($"Captured baseline card data for '{key}'.");
            }

            var displayName = TryGetMemberString(monster, "Name", "DisplayName");
            displayName = Prefer(displayName, TryGetTMPText(cardUI, CardUIBindings.MonsterNameTextField));
            UpdateIfEmpty(ref snapshot.DisplayName, displayName);

            if ((snapshot.Aliases == null || snapshot.Aliases.Length == 0) && !string.IsNullOrEmpty(snapshot.DisplayName))
            {
                snapshot.Aliases = new[] { snapshot.DisplayName };
            }

            UpdateIfEmpty(ref snapshot.CardNumber, TryGetTMPText(cardUI, CardUIBindings.NumberTextField));
            UpdateIfEmpty(ref snapshot.Subtitle, Prefer(BuildRolesLabel(monster), TryGetTMPText(cardUI, CardUIBindings.SubtitleTextField)));
            UpdateIfEmpty(ref snapshot.Description, Prefer(TryGetMemberString(monster, "Description"), TryGetTMPText(cardUI, CardUIBindings.DescriptionTextField)));
            UpdateIfEmpty(ref snapshot.Artist, Prefer(TryGetMemberString(monster, "ArtistName", "Artist"), TryGetTMPText(cardUI, CardUIBindings.ArtistTextField)));
            UpdateIfEmpty(ref snapshot.EvolvesFrom, TryGetMemberString(monster, "PreviousEvolution"));
            UpdateIfEmpty(ref snapshot.Rarity, Prefer(TryGetMemberString(monster, "Rarity"), TryGetTMPText(cardUI, CardUIBindings.RarityTextField)));
            UpdateIfEmpty(ref snapshot.Fame, Prefer(TryGetMemberString(monster, "ElementIndex"), TryGetTMPText(cardUI, CardUIBindings.FameTextField)));

            var stats = TryGetMemberValue(monster, "BaseStats");
            UpdateIfEmpty(ref snapshot.Stat1, Prefer(FormatStat(stats, "Strength", "STR"), TryGetTMPText(cardUI, CardUIBindings.Stat1TextField)));
            UpdateIfEmpty(ref snapshot.Stat2, Prefer(FormatStat(stats, "Magic", "MAG"), TryGetTMPText(cardUI, CardUIBindings.Stat2TextField)));
            UpdateIfEmpty(ref snapshot.Stat3, Prefer(FormatStat(stats, "Spirit", "SPR"), TryGetTMPText(cardUI, CardUIBindings.Stat3TextField)));
            UpdateIfEmpty(ref snapshot.Stat4, Prefer(FormatStat(stats, "Speed", "SPD"), TryGetTMPText(cardUI, CardUIBindings.Stat4TextField)));

            if (_cardOverrides.TryGetValue(key, out var overrideEntry) && overrideEntry != null)
            {
                RegisterCardOverrideAlias(overrideEntry, snapshot.DisplayName, "capture");

                if (snapshot.Aliases != null && snapshot.Aliases.Length > 0)
                {
                    foreach (var alias in snapshot.Aliases)
                    {
                        RegisterCardOverrideAlias(overrideEntry, alias, "capture");
                    }
                }
            }
        }

        internal void ApplyCheckPricePanelOverrides(CheckPricePanelUI panel)
        {
            if (panel == null || _cardOverrides.Count == 0)
                return;

            try
            {
                if (CheckPricePanelUIBindings.NameTextField?.GetValue(panel) is not TMP_Text nameText)
                    return;

                var originalLabel = nameText.text;
                var cardUI = CheckPricePanelUIBindings.CardUIField?.GetValue(panel) as CardUI;
                var entry = FindCardOverride(cardUI, originalLabel);
                if (entry == null)
                    return;

                ReplaceNameLabel(nameText, entry, originalLabel);
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[CustomTextureReplacer] ApplyCheckPricePanelOverrides failed: {ex.Message}");
                SafeAppendDebug($"ApplyCheckPricePanelOverrides exception: {ex.Message}");
            }
        }

        internal void AdjustCheckoutItemName(UI_CheckoutItemBar itemBar, string originalLabel)
        {
            if (itemBar == null || _cardOverrides.Count == 0)
                return;

            try
            {
                if (string.IsNullOrEmpty(originalLabel))
                    return;

                if (CheckoutItemBarBindings.NameTextField?.GetValue(itemBar) is not TMP_Text nameText)
                    return;

                var entry = FindCardOverride(null, originalLabel);
                if (entry == null)
                    return;

                ReplaceNameLabel(nameText, entry, originalLabel);
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[CustomTextureReplacer] AdjustCheckoutItemName failed: {ex.Message}");
                SafeAppendDebug($"AdjustCheckoutItemName exception: {ex.Message}");
            }
        }

        internal void ApplyItemPriceGraphOverrides(ItemPriceGraphScreen screen)
        {
            if (screen == null || _cardOverrides.Count == 0)
                return;

            try
            {
                if (ItemPriceGraphScreenBindings.CardNameTextField?.GetValue(screen) is not TMP_Text nameText)
                    return;

                var originalLabel = nameText.text;
                var cardUI = ItemPriceGraphScreenBindings.CardUIField?.GetValue(screen) as CardUI;
                var entry = FindCardOverride(cardUI, originalLabel);
                if (entry == null)
                    return;

                ReplaceNameLabel(nameText, entry, originalLabel);
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[CustomTextureReplacer] ApplyItemPriceGraphOverrides failed: {ex.Message}");
                SafeAppendDebug($"ApplyItemPriceGraphOverrides exception: {ex.Message}");
            }
        }

        private CardTextOverride FindCardOverride(CardUI cardUI, string label)
        {
            if (_cardOverrides.Count == 0)
                return null;

            if (cardUI != null)
            {
                var key = ResolveMonsterKey(cardUI);
                if (!string.IsNullOrEmpty(key) && _cardOverrides.TryGetValue(key, out var entry) && entry != null)
                    return entry;
            }

            var prefix = GetNamePrefix(label);
            if (!string.IsNullOrEmpty(prefix) && _cardOverrides.TryGetValue(prefix, out var fromLabel) && fromLabel != null)
                return fromLabel;

            return null;
        }

        private static string GetNamePrefix(string label)
        {
            if (string.IsNullOrEmpty(label))
                return string.Empty;

            var normalised = label.Replace("\r\n", "\n");
            var newlineIndex = normalised.IndexOf('\n');
            if (newlineIndex >= 0)
            {
                normalised = normalised.Substring(0, newlineIndex);
            }

            var separatorIndex = normalised.IndexOf(" - ", StringComparison.Ordinal);
            var candidate = separatorIndex >= 0 ? normalised.Substring(0, separatorIndex) : normalised;
            return candidate.Trim();
        }

        private static string GetNameSuffix(string label)
        {
            if (string.IsNullOrEmpty(label))
                return string.Empty;

            var separatorIndex = label.IndexOf(" - ", StringComparison.Ordinal);
            return separatorIndex >= 0 ? label.Substring(separatorIndex) : string.Empty;
        }

        private void ReplaceNameLabel(TMP_Text text, CardTextOverride entry, string originalLabel)
        {
            if (text == null || entry == null)
                return;

            var displayName = entry.DisplayName;
            if (string.IsNullOrEmpty(displayName))
                return;

            var baseline = originalLabel;
            if (string.IsNullOrEmpty(baseline))
                baseline = text.text;

            var suffix = GetNameSuffix(baseline);
            var newLabel = string.Concat(displayName, suffix);
            newLabel = newLabel.Replace("\\n", "\n");

            if (!string.Equals(text.text, newLabel, StringComparison.Ordinal))
            {
                text.text = newLabel;
            }
        }

        private static void SetCardUIText(CardUI cardUI, FieldInfo field, string value)
        {
            if (field == null || cardUI == null)
                return;

            if (!(field.GetValue(cardUI) is TMP_Text textComponent))
                return;

            if (string.IsNullOrEmpty(value))
                return;

            var formatted = value.Replace("\\n", "\n");
            if (!string.Equals(textComponent.text, formatted, StringComparison.Ordinal))
            {
                textComponent.text = formatted;
            }
        }

        private void DumpOriginalCardData()
        {
            try
            {
                if (_capturedCardData.Count == 0)
                {
                    _logger.LogWarning("[CustomTextureReplacer] Card override dump skipped - no card data has been captured yet.");
                    SafeAppendDebug("CardOverrides dump aborted: no captured card data.");
                    return;
                }

                var ordered = _capturedCardData.Values
                    .Where(entry => entry != null && !string.IsNullOrEmpty(entry.Id))
                    .OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var entries = new List<Dictionary<string, object>>(ordered.Count);
                foreach (var entry in ordered)
                {
                    entries.Add(ConvertOverrideToDictionary(entry));
                }

                var root = new Dictionary<string, object>
                {
                    ["entries"] = entries
                };

                var json = MiniJson.Serialize(root);
                var path = Path.Combine(Paths.PluginPath, "CardOverrides.original.json");
                File.WriteAllText(path, json);
                _logger.LogInfo($"[CustomTextureReplacer] Dumped {entries.Count} original card entry/entries to '{path}'.");
                SafeAppendDebug($"CardOverrides dump complete: {entries.Count} entries -> {path}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Card override dump failed: {ex.Message}");
                SafeAppendDebug($"CardOverrides dump exception: {ex.Message}");
            }
        }

        private static string ResolveMonsterKey(CardUI cardUI)
        {
            if (cardUI == null)
                return string.Empty;

            try
            {
                if (CardUIBindings.CardDataField?.GetValue(cardUI) is CardData cardData)
                {
                    return cardData.monsterType.ToString();
                }

                if (CardUIBindings.MonsterDataField?.GetValue(cardUI) is MonsterData monsterData)
                {
                    return monsterData.MonsterType.ToString();
                }
            }
            catch
            {
                // ignore reflection errors
            }

            return string.Empty;
        }

        private static class CardUIBindings
        {
            internal static readonly FieldInfo CardDataField = AccessTools.Field(typeof(CardUI), "m_CardData");
            internal static readonly FieldInfo MonsterDataField = AccessTools.Field(typeof(CardUI), "m_MonsterData");
            internal static readonly FieldInfo MonsterNameTextField = AccessTools.Field(typeof(CardUI), "m_MonsterNameText");
            internal static readonly FieldInfo NumberTextField = AccessTools.Field(typeof(CardUI), "m_NumberText");
            internal static readonly FieldInfo SubtitleTextField = AccessTools.Field(typeof(CardUI), "m_ChampionText");
            internal static readonly FieldInfo DescriptionTextField = AccessTools.Field(typeof(CardUI), "m_DescriptionText");
            internal static readonly FieldInfo ArtistTextField = AccessTools.Field(typeof(CardUI), "m_ArtistText");
            internal static readonly FieldInfo RarityTextField = AccessTools.Field(typeof(CardUI), "m_RarityText");
            internal static readonly FieldInfo FameTextField = AccessTools.Field(typeof(CardUI), "m_FameText");
            internal static readonly FieldInfo Stat1TextField = AccessTools.Field(typeof(CardUI), "m_Stat1Text");
            internal static readonly FieldInfo Stat2TextField = AccessTools.Field(typeof(CardUI), "m_Stat2Text");
            internal static readonly FieldInfo Stat3TextField = AccessTools.Field(typeof(CardUI), "m_Stat3Text");
            internal static readonly FieldInfo Stat4TextField = AccessTools.Field(typeof(CardUI), "m_Stat4Text");
            internal static readonly FieldInfo EvoPreviousStageNameTextField = AccessTools.Field(typeof(CardUI), "m_EvoPreviousStageNameText");
            internal static readonly FieldInfo EvoGroupField = AccessTools.Field(typeof(CardUI), "m_EvoGrp");
        }

        private static class CheckPricePanelUIBindings
        {
            internal static readonly FieldInfo NameTextField = AccessTools.Field(typeof(CheckPricePanelUI), "m_NameText");
            internal static readonly FieldInfo CardUIField = AccessTools.Field(typeof(CheckPricePanelUI), "m_CardUI");
        }

        private static class CheckoutItemBarBindings
        {
            internal static readonly FieldInfo NameTextField = AccessTools.Field(typeof(UI_CheckoutItemBar), "m_NameText");
        }

        private static class ItemPriceGraphScreenBindings
        {
            internal static readonly FieldInfo CardNameTextField = AccessTools.Field(typeof(ItemPriceGraphScreen), "m_CardName");
            internal static readonly FieldInfo CardUIField = AccessTools.Field(typeof(ItemPriceGraphScreen), "m_CardUI");
        }

        private class CardTextOverride
        {
            public string Id;
            public string[] Aliases;
            public string DisplayName;
            public string Subtitle;
            public string CardNumber;
            public string Description;
            public string EvolvesFrom;
            public string Artist;
            public string Stat1;
            public string Stat2;
            public string Stat3;
            public string Stat4;
            public string Rarity;
            public string Fame;
        }

        private static object TryGetMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrEmpty(memberName))
                return null;

            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var field = type.GetField(memberName, flags);
            if (field != null)
                return field.GetValue(target);

            var property = type.GetProperty(memberName, flags);
            return property?.GetValue(target, null);
        }

        private static string TryGetMemberString(object target, params string[] names)
        {
            if (target == null || names == null)
                return string.Empty;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name))
                    continue;

                var value = TryGetMemberValue(target, name);
                if (value == null)
                    continue;

                var str = value.ToString();
                if (!string.IsNullOrEmpty(str))
                    return str;
            }

            return string.Empty;
        }

        private static string FormatStat(object statsObject, string memberName, string label)
        {
            if (statsObject == null || string.IsNullOrEmpty(memberName))
                return string.Empty;

            var value = TryGetMemberValue(statsObject, memberName);
            if (value == null)
                return string.Empty;

            if (value is float f)
                value = Mathf.RoundToInt(f);
            else if (value is double d)
                value = Mathf.RoundToInt((float)d);

            return $"{label}: {value}";
        }

        private static string BuildRolesLabel(object monster)
        {
            var rolesValue = TryGetMemberValue(monster, "Roles");
            if (rolesValue is IEnumerable enumerable && rolesValue is not string)
            {
                var buffer = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item == null)
                        continue;

                    if (item is string s)
                    {
                        if (!string.IsNullOrEmpty(s))
                            buffer.Add(s);
                    }
                    else
                    {
                        var name = TryGetMemberString(item, "Name", "DisplayName", "RoleName");
                        if (!string.IsNullOrEmpty(name))
                            buffer.Add(name);
                        else
                            buffer.Add(item.ToString());
                    }
                }

                if (buffer.Count > 0)
                    return string.Join(", ", buffer);
            }

            return string.Empty;
        }

        private static void SetIfNotEmpty(Dictionary<string, object> dictionary, string key, string value)
        {
            if (dictionary == null || string.IsNullOrEmpty(key))
                return;

            if (!string.IsNullOrEmpty(value))
                dictionary[key] = value;
        }

        private static string TryGetTMPText(CardUI cardUI, FieldInfo field)
        {
            if (cardUI == null || field == null)
                return string.Empty;

            if (field.GetValue(cardUI) is TMP_Text textComponent)
            {
                var value = textComponent.text;
                if (!string.IsNullOrEmpty(value))
                    return value.Trim();
            }

            return string.Empty;
        }

        private static void UpdateIfEmpty(ref string target, string candidate)
        {
            if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(candidate))
                target = candidate;
        }

        private static string Prefer(string primary, string fallback)
        {
            return !string.IsNullOrEmpty(primary) ? primary : fallback;
        }

        private static Dictionary<string, object> ConvertOverrideToDictionary(CardTextOverride entry)
        {
            var dictionary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["Id"] = entry.Id
            };

            if (entry.Aliases != null && entry.Aliases.Length > 0)
                dictionary["Aliases"] = entry.Aliases;

            SetIfNotEmpty(dictionary, "DisplayName", entry.DisplayName);
            SetIfNotEmpty(dictionary, "CardNumber", entry.CardNumber);
            SetIfNotEmpty(dictionary, "Subtitle", entry.Subtitle);
            SetIfNotEmpty(dictionary, "Description", entry.Description);
            SetIfNotEmpty(dictionary, "EvolvesFrom", entry.EvolvesFrom);
            SetIfNotEmpty(dictionary, "Artist", entry.Artist);
            SetIfNotEmpty(dictionary, "Stat1", entry.Stat1);
            SetIfNotEmpty(dictionary, "Stat2", entry.Stat2);
            SetIfNotEmpty(dictionary, "Stat3", entry.Stat3);
            SetIfNotEmpty(dictionary, "Stat4", entry.Stat4);
            SetIfNotEmpty(dictionary, "Rarity", entry.Rarity);
            SetIfNotEmpty(dictionary, "Fame", entry.Fame);

            return dictionary;
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

    [HarmonyPatch(typeof(CardUI))]
    internal static class CardUIPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("SetCardUI")]
        private static void SetCardUIPostfix(CardUI __instance)
        {
            var controller = ReplacerController.Instance;
            if (controller == null || __instance == null)
                return;

            controller.CaptureCardData(__instance);
            controller.ApplyCardOverrides(__instance);
        }
    }

    [HarmonyPatch(typeof(CheckPricePanelUI))]
    internal static class CheckPricePanelUIPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("InitCard")]
        private static void InitCardPostfix(CheckPricePanelUI __instance)
        {
            ReplacerController.Instance?.ApplyCheckPricePanelOverrides(__instance);
        }
    }

    [HarmonyPatch(typeof(UI_CheckoutItemBar))]
    internal static class CheckoutItemBarPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("SetItemName")]
        private static void SetItemNamePostfix(UI_CheckoutItemBar __instance, string name)
        {
            ReplacerController.Instance?.AdjustCheckoutItemName(__instance, name);
        }
    }

    [HarmonyPatch(typeof(ItemPriceGraphScreen))]
    internal static class ItemPriceGraphScreenPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("ShowCardPriceChart")]
        private static void ShowCardPriceChartPostfix(ItemPriceGraphScreen __instance)
        {
            ReplacerController.Instance?.ApplyItemPriceGraphOverrides(__instance);
        }
    }

}
























