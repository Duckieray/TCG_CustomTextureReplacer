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
    [BepInPlugin("com.duckieray.cardshop.customtextures", "Custom Texture Replacer", "1.7.0")]
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

        internal enum BoxColliderSearchScope
        {
            Self,
            SelfAndChildren,
            Parents
        }

        private struct TextureCandidate
        {
            public string Path;
            public DateTime TimestampUtc;
            public int FolderIndex;
            public long EventOrder;
        }

        private const string HarmonyId = "com.duckieray.cardshop.customtextures.harmony";
        private const float MeshOverrideRetryWindowSeconds = 10f;
        private const float MeshOverrideRescanIntervalApplied = 0.25f;
        private const float MeshOverrideRescanIntervalPending = 0.5f;
        private const float MeshOverrideRescanMinimumDelay = 0.12f;
        private const int MeshLabelSearchParentDepth = 12;
        private static readonly Type UIImageType = typeof(Image);
        private static readonly PropertyInfo UIImageSpriteProperty = UIImageType.GetProperty("sprite", BindingFlags.Instance | BindingFlags.Public);
        private static readonly PropertyInfo UIImageOverrideSpriteProperty = UIImageType.GetProperty("overrideSprite", BindingFlags.Instance | BindingFlags.Public);
        private static readonly Type UIRawImageType = typeof(RawImage);
        private static readonly PropertyInfo UIRawImageTextureProperty = UIRawImageType.GetProperty("texture", BindingFlags.Instance | BindingFlags.Public);

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
        // Keeps track of duplicate texture names so overrides don't bleed across unrelated assets.
        private readonly Dictionary<string, int> _textureNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguousTextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguousTextureLog = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Sprite, Sprite> _spriteOverrides = new Dictionary<Sprite, Sprite>();
        private readonly Dictionary<string, Sprite> _spriteOverridesByName = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Sprite, Texture2D> _spriteOverrideTextures = new Dictionary<Sprite, Texture2D>();
        private readonly HashSet<int> _imageOverrideInstances = new HashSet<int>();
        private readonly HashSet<int> _rawImageOverrideInstances = new HashSet<int>();
        private bool _applyingImageOverride;
        private bool _applyingRawImageOverride;
        private bool _applyingLabelOverride;
        private readonly Dictionary<int, string> _meshInstanceTargetByComponent = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _shelfMeshNameCache = new Dictionary<int, string>();
        private readonly HashSet<string> _loggedShelfCapacityWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loggedMeshLabelCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly FieldInfo ShelfCompartmentItemTypeField = AccessTools.Field(typeof(ShelfCompartment), "m_ItemType");
        private static readonly FieldInfo ShelfCompartmentItemPosListField = AccessTools.Field(typeof(ShelfCompartment), "m_ItemPosList");
        internal static readonly Type InventoryBaseType = AccessTools.TypeByName("InventoryBase");
        internal static readonly Type ItemTypeEnumType = AccessTools.TypeByName("EItemType");
        internal static readonly MethodInfo InventoryGetItemMeshDataMethod = (InventoryBaseType != null && ItemTypeEnumType != null) ? AccessTools.Method(InventoryBaseType, "GetItemMeshData", new[] { ItemTypeEnumType }) : null;
        internal static readonly Type ItemMeshDataType = AccessTools.TypeByName("ItemMeshData");
        internal static readonly FieldInfo ItemMeshDataMeshField = ItemMeshDataType != null ? AccessTools.Field(ItemMeshDataType, "mesh") : null;
        internal static readonly FieldInfo ShelfCompartmentSizeXField = AccessTools.Field(typeof(ShelfCompartment), "m_SizeX");
        internal static readonly FieldInfo ShelfCompartmentCurrentItemSizeXField = AccessTools.Field(typeof(ShelfCompartment), "m_CurrentItemSizeX");
        internal static readonly FieldInfo ShelfCompartmentSizeYField = AccessTools.Field(typeof(ShelfCompartment), "m_SizeY");
        internal static readonly FieldInfo ShelfCompartmentSizeZField = AccessTools.Field(typeof(ShelfCompartment), "m_SizeZ");
        internal static readonly FieldInfo ShelfCompartmentCurrentItemSizeYField = AccessTools.Field(typeof(ShelfCompartment), "m_CurrentItemSizeY");
        internal static readonly FieldInfo ShelfCompartmentCurrentItemSizeZField = AccessTools.Field(typeof(ShelfCompartment), "m_CurrentItemSizeZ");
        internal static readonly FieldInfo ShelfCompartmentMaxItemXField = AccessTools.Field(typeof(ShelfCompartment), "m_MaxItemX");
        internal static readonly FieldInfo ShelfCompartmentMaxItemYField = AccessTools.Field(typeof(ShelfCompartment), "m_MaxItemY");
        internal static readonly FieldInfo ShelfCompartmentMaxItemZField = AccessTools.Field(typeof(ShelfCompartment), "m_MaxItemZ");
        internal static readonly FieldInfo ShelfCompartmentMaxItemCountField = AccessTools.Field(typeof(ShelfCompartment), "m_MaxItemCount");
        internal static readonly FieldInfo ShelfCompartmentTransformPosListField = AccessTools.Field(typeof(ShelfCompartment), "m_PosList");
        internal static readonly FieldInfo ShelfCompartmentStoredItemListGrpField = AccessTools.Field(typeof(ShelfCompartment), "m_StoredItemListGrp");
        private readonly Dictionary<string, PersistentSpriteOverride> _spritePersistence = new Dictionary<string, PersistentSpriteOverride>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<PersistentSpriteOverride>> _spritePersistenceByTexture = new Dictionary<string, List<PersistentSpriteOverride>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Texture2D> _generatedTextures = new HashSet<Texture2D>();
        private readonly HashSet<Sprite> _generatedSprites = new HashSet<Sprite>();
        private readonly Dictionary<string, CardTextOverride> _cardOverrides = new Dictionary<string, CardTextOverride>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CardTextOverride> _capturedCardData = new Dictionary<string, CardTextOverride>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CardTextOverride> _meshCardOverrides = new Dictionary<string, CardTextOverride>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _cardOverrideAliasKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _meshCardOverrideAliasKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MeshOverrideData> _meshOverridesByTarget = new Dictionary<string, MeshOverrideData>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _meshTextureFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, WeakReference<MeshFilter>> _meshFilterReferences = new Dictionary<int, WeakReference<MeshFilter>>();
        private readonly Dictionary<int, Mesh> _meshFilterOriginalMeshes = new Dictionary<int, Mesh>();
        private readonly Dictionary<int, string> _meshFilterOriginalMeshNames = new Dictionary<int, string>();
        private readonly HashSet<int> _meshFilterMissingOriginalLogged = new HashSet<int>();
        private readonly HashSet<int> _meshFilterOverrideIds = new HashSet<int>();
        private readonly Dictionary<int, WeakReference<SkinnedMeshRenderer>> _skinnedRendererReferences = new Dictionary<int, WeakReference<SkinnedMeshRenderer>>();
        private readonly Dictionary<int, Mesh> _skinnedOriginalMeshes = new Dictionary<int, Mesh>();
        private readonly Dictionary<int, string> _skinnedOriginalMeshNames = new Dictionary<int, string>();
        private readonly HashSet<int> _skinnedMissingOriginalLogged = new HashSet<int>();
        private readonly HashSet<int> _skinnedRendererOverrideIds = new HashSet<int>();
        private readonly Dictionary<int, WeakReference<MeshCollider>> _meshColliderReferences = new Dictionary<int, WeakReference<MeshCollider>>();
        private readonly Dictionary<int, Mesh> _meshColliderOriginalMeshes = new Dictionary<int, Mesh>();
        private readonly Dictionary<int, string> _meshColliderOriginalMeshNames = new Dictionary<int, string>();
        private readonly HashSet<int> _meshColliderMissingOriginalLogged = new HashSet<int>();
        private readonly HashSet<int> _meshColliderOverrideIds = new HashSet<int>();
        private readonly List<BoxCollider> _boxColliderScratch = new List<BoxCollider>(8);
        private readonly List<Vector3> _colliderVertexScratch = new List<Vector3>(2048);
        private readonly Dictionary<int, WeakReference<Renderer>> _rendererReferences = new Dictionary<int, WeakReference<Renderer>>();
        private readonly Dictionary<int, Material[]> _rendererOriginalMaterials = new Dictionary<int, Material[]>();
        private readonly HashSet<int> _rendererOverrideIds = new HashSet<int>();
        private readonly Dictionary<int, string> _lastAppliedMeshLabels = new Dictionary<int, string>();
        private readonly Dictionary<int, WeakReference<TMP_Text>> _meshLabelTextRefs = new Dictionary<int, WeakReference<TMP_Text>>();
        private readonly List<int> _meshLabelCleanupScratch = new List<int>(64);
        private int _meshLabelCleanupBudget = 64;
        private readonly Dictionary<string, AssetBundle> _meshAssetBundles = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FileSystemWatcher> _meshBundleWatchers = new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendingSpriteAtlasReapply = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _overridesDirty;
        private readonly MaterialPropertyBlock _propertyBlock = new MaterialPropertyBlock();
        private readonly Dictionary<string, DateTime> _fileEventTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _fileEventOrders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private long _fileEventCounter;

        private Sprite[] _spriteArray = Array.Empty<Sprite>();

        private ManualLogSource _logger;
        private ConfigEntry<bool> _logNewTextureNames;
        private ConfigEntry<bool> _logAssetLoads;
        private ConfigEntry<bool> _enableDebugLog;
        private ConfigEntry<bool> _enableAutoDumps;
        private ConfigEntry<bool> _suppressRectTransformWarning;

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
        private bool _meshOverridesDirty;
        private float _nextMeshOverrideScanTime;
        private float _meshOverrideRetryDeadline;
        private bool _meshOverrideRetryLogged;

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
            _enableDebugLog = config.Bind("Debug", "EnableDebugLogFile", false, "Write CustomTextureReplacer.debug.log for troubleshooting. Disable to avoid extra disk writes.");
            _enableAutoDumps = config.Bind("Debug", "EnableAutomaticDumps", false, "Automatically dump texture/sprite/mesh lists after loading. Disable to avoid large writes at start-up.");
            _suppressRectTransformWarning = config.Bind("Debug", "SuppressRectTransformParentWarning", true, "Suppress the noisy 'Parent of RectTransform is being set with parent property' warnings Unity emits.");
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
            SpriteAtlasManager.atlasRegistered -= OnSpriteAtlasRegistered;
            SpriteAtlasManager.atlasRegistered += OnSpriteAtlasRegistered;
            ReloadCustomTextures();
            ReapplyExistingSpriteAtlases("Initialise");
            ScheduleMeshOverrideReapply(0f);
            if (_enableAutoDumps != null && _enableAutoDumps.Value)
                StartCoroutine(InitialDump());
        }

        private void OnDestroy()
        {
            SafeAppendDebug("Controller OnDestroy.");

            SceneManager.sceneLoaded -= OnSceneLoaded;
            SpriteAtlasManager.atlasRegistered -= OnSpriteAtlasRegistered;

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
            _cardOverrideAliasKeys.Clear();

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
            _imageOverrideInstances.Clear();
            _rawImageOverrideInstances.Clear();
            _meshTextureFolders.Clear();
            _meshCardOverrides.Clear();
            _meshCardOverrideAliasKeys.Clear();
            Instance = null;
            _pendingSpriteAtlasReapply.Clear();
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
                ScheduleMeshOverrideReapply(0f);
            }

            ApplyMeshOverrides();
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

            if (_enableAutoDumps == null || !_enableAutoDumps.Value)
                yield break;

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

            if (!_hasDumpedAfterSceneLoad && _enableAutoDumps != null && _enableAutoDumps.Value)
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
            _spritePersistence.Clear();
            _spritePersistenceByTexture.Clear();

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

                    RegisterPersistentSpriteOverride(sprite, replacement, width, height);

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
            ApplyTextureOverridesToRenderers();
            ApplySpriteOverridesToComponents();

            if (replaced > 0 || skipped > 0)
            {
                _logger.LogInfo($"[CustomTextureReplacer] Applied replacements. Success: {replaced}, skipped: {skipped}.");
                SafeAppendDebug($"ReplaceAllTextures finished. Success={replaced} Skipped={skipped}");
            }

            ScheduleMeshOverrideReapply(0f);
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

        private bool IsTextureNameAmbiguous(string name)
        {
            return !string.IsNullOrEmpty(name) && _ambiguousTextureNames.Contains(name);
        }

        private bool TryCreateTextureOverride(Texture target, Texture2D replacement, int width, int height)
        {
            if (!(target is Texture2D targetTex) || replacement == null)
                return false;

            var textureName = targetTex.name;
            var isAmbiguous = IsTextureNameAmbiguous(textureName);
            if (isAmbiguous && _ambiguousTextureLog.Add(textureName))
            {
                _textureNameCounts.TryGetValue(textureName, out var count);
                _logger.LogWarning($"[CustomTextureReplacer] Name-based override disabled for ambiguous texture '{textureName}' ({count} instances). Using direct references only.");
                SafeAppendDebug($"Ambiguous texture '{textureName}' ({count}) will rely on direct references.");
            }

            try
            {
                if (_textureOverrides.TryGetValue(targetTex, out var existingOverride))
                {
                    if (existingOverride is Texture2D tex2D && tex2D.width == width && tex2D.height == height)
                    {
                        if (TryCopyIntoTexture(replacement, tex2D, width, height, targetTex.name))
                        {
                            _textureOverrides[targetTex] = tex2D;
                            if (!string.IsNullOrEmpty(textureName))
                            {
                                if (!isAmbiguous)
                                {
                                    _textureOverridesByName[textureName] = tex2D;
                                    LinkMatchingTexturesByName(textureName, tex2D, targetTex);
                                }
                                else
                                {
                                    _textureOverridesByName.Remove(textureName);
                                }
                            }
                            _overridesDirty = true;
                            return true;
                        }

                        RemoveTextureOverride(targetTex);
                    }
                }

                if (!string.IsNullOrEmpty(textureName) && !isAmbiguous && _textureOverridesByName.TryGetValue(textureName, out var nameOverride))
                {
                    if (nameOverride is Texture2D tex2D && tex2D.width == width && tex2D.height == height)
                    {
                        if (TryCopyIntoTexture(replacement, tex2D, width, height, targetTex.name))
                        {
                            _textureOverrides[targetTex] = tex2D;
                            _textureOverridesByName[textureName] = tex2D;
                            LinkMatchingTexturesByName(textureName, tex2D, targetTex);
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
                if (!string.IsNullOrEmpty(textureName))
                {
                    if (!isAmbiguous)
                    {
                        _textureOverridesByName[textureName] = newTexture;
                        LinkMatchingTexturesByName(textureName, newTexture, targetTex);
                    }
                    else
                    {
                        _textureOverridesByName.Remove(textureName);
                    }
                }

                _logger.LogInfo($"[CustomTextureReplacer] Using runtime texture override for '{targetTex.name}'.");
                _logger.LogDebug($"[CustomTextureReplacer] Override '{targetTex.name}' uses custom '{replacement.name}' ({replacement.width}x{replacement.height}).");
                SafeAppendDebug($"Texture override created for {targetTex.name}");
                _overridesDirty = true;
                ApplyTextureOverridesToMaterials(targetTex);
                ApplyTextureOverridesToRenderers();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Failed to create texture override for '{target?.name}': {ex.Message}");
                SafeAppendDebug($"Texture override failed for {target?.name}: {ex.Message}");
                return false;
            }
        }

        private void LinkMatchingTexturesByName(string name, Texture replacement, Texture exclude)
        {
            if (string.IsNullOrEmpty(name) || replacement == null)
                return;

            foreach (var tex in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (tex == null || ReferenceEquals(tex, exclude))
                    continue;

                if (!string.Equals(tex.name, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                _textureOverrides[tex] = replacement;
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
                            RegisterPersistentSpriteOverride(originalSprite, replacement, width, height);
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
                RegisterPersistentSpriteOverride(originalSprite, replacement, width, height);
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

            if (_spritePersistence.TryGetValue(spriteName, out var record))
            {
                _spritePersistence.Remove(spriteName);
                if (!string.IsNullOrEmpty(record.TargetTextureName) && _spritePersistenceByTexture.TryGetValue(record.TargetTextureName, out var list))
                {
                    list.RemoveAll(entry => string.Equals(entry.SpriteName, spriteName, StringComparison.OrdinalIgnoreCase));
                    if (list.Count == 0)
                        _spritePersistenceByTexture.Remove(record.TargetTextureName);
                }
            }
        }

        private Texture GetReplacementTexture(Texture original)
        {
            if (original == null)
                return null;

            if (_textureOverrides.TryGetValue(original, out var direct))
                return direct;

            var name = original.name;
            if (!string.IsNullOrEmpty(name) &&
                !_ambiguousTextureNames.Contains(name) &&
                _textureOverridesByName.TryGetValue(name, out var byName))
            {
                return byName;
            }

            return null;
        }

        internal Sprite GetReplacementSprite(Sprite original)
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
                    SafeAppendDebug($"SpriteRenderer override applied on '{renderer.name}' using sprite '{replacement.name}' (was '{renderer.sprite?.name ?? "<null>"}').");
                    renderer.sprite = replacement;
                }
            }

            if (UIImageType != null && UIImageSpriteProperty != null)
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(UIImageType))
                {
                    if (obj is Image image && image != null)
                    {
                        var current = image.overrideSprite ?? image.sprite;
                        ApplySpriteOverrideToImage(image, current);
                    }
                }
            }

            if (UIRawImageType != null && UIRawImageTextureProperty != null)
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(UIRawImageType))
                {
                    if (obj is RawImage raw && raw != null)
                    {
                        ApplyTextureOverrideToRawImage(raw, raw.texture);
                    }
                }
            }
        }

        internal void ApplySpriteOverrideToImage(Image image, Sprite assigned)
        {
            if (image == null)
                return;

            if (_applyingImageOverride)
                return;

            var target = assigned ?? image.sprite;
            var replacement = GetReplacementSprite(target);
            var id = image.GetInstanceID();

            if (replacement != null && !ReferenceEquals(target, replacement))
            {
                _imageOverrideInstances.Add(id);

                if (!ReferenceEquals(image.overrideSprite, replacement))
                {
                    _applyingImageOverride = true;
                    image.overrideSprite = replacement;
                    _applyingImageOverride = false;
                    image.SetAllDirty();
                    SafeAppendDebug($"UI Image override applied: {target?.name ?? "<null>"} -> {replacement.name}");
                }
            }
            else
            {
                if (_imageOverrideInstances.Remove(id))
                {
                    _applyingImageOverride = true;
                    if (assigned != null && !ReferenceEquals(image.sprite, assigned))
                        image.overrideSprite = assigned;
                    else
                        image.overrideSprite = null;
                    _applyingImageOverride = false;

                    image.SetAllDirty();
                    SafeAppendDebug($"UI Image override cleared for '{target?.name ?? "<null>"}'.");
                }
            }
        }

        internal void ApplyTextureOverrideToRawImage(RawImage rawImage, Texture assigned)
        {
            if (rawImage == null)
                return;

            if (_applyingRawImageOverride)
                return;

            var target = assigned ?? rawImage.texture;
            var replacement = GetReplacementTexture(target);
            var id = rawImage.GetInstanceID();

            if (replacement != null && !ReferenceEquals(target, replacement))
            {
                _rawImageOverrideInstances.Add(id);

                if (!ReferenceEquals(rawImage.texture, replacement))
                {
                    _applyingRawImageOverride = true;
                    rawImage.texture = replacement;
                    _applyingRawImageOverride = false;
                    rawImage.SetAllDirty();
                    SafeAppendDebug($"UI RawImage override applied: {target?.name ?? "<null>"} -> {replacement.name}");
                }
            }
            else
            {
                if (_rawImageOverrideInstances.Remove(id))
                {
                    if (!ReferenceEquals(rawImage.texture, target))
                    {
                        _applyingRawImageOverride = true;
                        rawImage.texture = target;
                        _applyingRawImageOverride = false;
                    }

                    rawImage.SetAllDirty();
                    SafeAppendDebug($"UI RawImage override cleared for '{target?.name ?? "<null>"}'.");
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
            _meshCardOverrides.Clear();
            _meshCardOverrideAliasKeys.Clear();

            if (string.IsNullOrEmpty(_meshOverridesPath))
            {
                SafeAppendDebug("MeshOverrides path empty; nothing to load.");
                ScheduleMeshOverrideReapply(0f);
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
                ScheduleMeshOverrideReapply(0f);
                RequestReapply("MeshOverridesReloaded");
                return;
            }

            bool cardOverridesChanged = false;

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
                    data.AutoAdjustBoxColliders = GetBool(entryDict, "autoBoxCollider", false);
                    var boxColliderScopeValue = GetString(entryDict, "boxColliderScope");
                    if (!string.IsNullOrEmpty(boxColliderScopeValue))
                    {
                        data.BoxColliderScope = ParseBoxColliderScope(boxColliderScopeValue);
                    }

                    var shelfCount = GetInt(entryDict, "shelfCount", -1);
                    var instanceCount = GetInt(entryDict, "instanceCount", -1);
                    if (shelfCount < 0 && instanceCount >= 0)
                        shelfCount = instanceCount;
                    if (shelfCount >= 0)
                    {
                        data.DesiredShelfCapacity = shelfCount;
                        data.ShelfAllocationsDirty = true;
                    }
                    if (instanceCount >= 0)
                        data.DesiredInstanceCount = instanceCount;

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

                    cardOverridesChanged |= ApplyMeshCardMetadata(entryDict, data, logDetails);

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

            if (cardOverridesChanged)
            {
                ReapplyCardOverrides();
                ReapplyStoreLabels();
            }

            ScheduleMeshOverrideReapply(0f);
            RequestReapply("MeshOverridesReloaded");
            ApplyMeshOverrides();
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

            if (TryPopulateMaterialsFromPrefabs(data, bundle, materials, subMeshCount, logDetails))
            {
                data.AutoSelectMaterials = false;
                return true;
            }

            var candidates = BuildScoredMaterialList(data, materials);
            if (candidates.Count == 0 && logDetails)
            {
                _logger.LogInfo($"[CustomTextureReplacer] Auto material scoring produced 0 candidates for '{data.TargetName}' (hints={string.Join(",", data.MaterialSelectionHints ?? Array.Empty<string>())}).");
            }
            if (candidates.Count == 0)
            {
                _logger.LogWarning($"[CustomTextureReplacer] Unable to auto-select materials from bundle '{GetBundleDebugName(data.SourceBundlePath, bundle)}' for target '{data.TargetName}'. Provide explicit 'materials' or 'materialHints'.");
                SafeAppendDebug($"Auto material selection produced no candidates for {data.TargetName}");
                return false;
            }

            if (subMeshCount <= 0)
                subMeshCount = candidates.Count;

            var required = Math.Max(1, subMeshCount);
            if (logDetails)
            {
                var preview = string.Join(", ", candidates.Take(Math.Min(5, candidates.Count)).Select(mat => mat?.name ?? "<null>"));
                _logger.LogInfo($"[CustomTextureReplacer] Using {required} auto-selected material slot(s) for '{data.TargetName}' (candidates={candidates.Count}: {preview}).");
            }
            for (int slot = 0; slot < required; slot++)
            {
                var candidate = slot < candidates.Count ? candidates[slot] : candidates.Last();
                if (candidate == null)
                    continue;
                if (slot >= candidates.Count && logDetails)
                {
                    _logger.LogInfo($"[CustomTextureReplacer] Reusing material '{candidate.name}' for slot {slot} (target '{data.TargetName}').");
                }
                AddMaterialOverrideFromAsset(data, candidate, slot, logDetails);
            }

            data.AutoSelectMaterials = false;
            return true;
        }

        private bool TryPopulateMaterialsFromPrefabs(MeshOverrideData data, AssetBundle bundle, Material[] materials, int subMeshCount, bool logDetails)
        {
            if (string.IsNullOrEmpty(data?.MeshAssetName) || bundle == null)
            {
                if (logDetails)
                    _logger.LogInfo($"[CustomTextureReplacer] Prefab material scan skipped for '{data?.TargetName}' (missing mesh name or bundle).");
                return false;
            }

            GameObject[] prefabs;
            try
            {
                prefabs = bundle.LoadAllAssets<GameObject>();
            }
            catch
            {
                if (logDetails)
                    _logger.LogInfo($"[CustomTextureReplacer] Prefab material scan failed to enumerate prefabs for '{data.TargetName}'.");
                return false;
            }

            if (prefabs == null || prefabs.Length == 0)
            {
                if (logDetails)
                    _logger.LogInfo($"[CustomTextureReplacer] Prefab material scan found no prefabs in bundle for '{data.TargetName}'.");
                return false;
            }

            var matchedMaterials = new List<Material>();

            foreach (var prefab in prefabs)
            {
                if (prefab == null)
                    continue;

                try
                {
                    foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer == null)
                            continue;

                        Mesh mesh = null;
                        if (renderer is SkinnedMeshRenderer skinned)
                        {
                            mesh = skinned.sharedMesh;
                        }
                        else if (renderer.TryGetComponent<MeshFilter>(out var filter))
                        {
                            mesh = filter.sharedMesh;
                        }

                        if (mesh == null)
                            continue;

                        if (!MeshMatchesTarget(mesh, data))
                            continue;

                        var rendererMaterials = renderer.sharedMaterials ?? Array.Empty<Material>();
                        matchedMaterials.AddRange(rendererMaterials.Where(mat => mat != null));
                        if (matchedMaterials.Count > 0)
                            break;
                    }
                }
                catch
                {
                    // ignore malformed prefabs
                }

                if (matchedMaterials.Count > 0)
                    break;
            }

            if (matchedMaterials.Count == 0)
            {
                if (logDetails)
                    _logger.LogInfo($"[CustomTextureReplacer] Prefab material scan found no renderer using mesh '{data.MeshAssetName}' for '{data.TargetName}'.");
                return false;
            }

            var required = matchedMaterials.Count;
            for (int slot = 0; slot < required; slot++)
            {
                var rendererMat = matchedMaterials[slot];
                if (rendererMat == null)
                    continue;

                var assetMat = FindMatchingMaterialAsset(rendererMat, materials);
                var chosen = assetMat ?? rendererMat;
                if (logDetails)
                {
                    var sourceName = rendererMat?.name ?? "<null>";
                    var assetName = assetMat?.name ?? "<renderer>";
                    _logger.LogInfo($"[CustomTextureReplacer] Prefab mapping slot {slot}: renderer '{sourceName}' -> asset '{assetName}' for '{data.TargetName}'.");
                }
                AddMaterialOverrideFromAsset(data, chosen, slot, logDetails);
            }

            if (subMeshCount > 0 && required != subMeshCount)
            {
                _logger.LogInfo($"[CustomTextureReplacer] Prefab material count ({required}) differs from mesh sub-meshes ({subMeshCount}) for '{data.TargetName}'. Using prefab ordering.");
            }

            return data.MaterialOverrides.Count > 0;
        }

        private static Material FindMatchingMaterialAsset(Material rendererMaterial, Material[] candidates)
        {
            if (rendererMaterial == null || candidates == null || candidates.Length == 0)
                return null;

            foreach (var candidate in candidates)
            {
                if (ReferenceEquals(candidate, rendererMaterial))
                    return candidate;
            }

            var name = rendererMaterial.name;
            if (string.IsNullOrEmpty(name))
                return null;

            foreach (var candidate in candidates)
            {
                if (candidate != null && string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        private bool MeshMatchesTarget(Mesh mesh, MeshOverrideData data)
        {
            if (mesh == null || data == null)
                return false;

            var meshName = NormalizeAssetName(mesh.name);

            if (!string.IsNullOrEmpty(data.MeshAssetName))
            {
                var targetMeshName = NormalizeAssetName(data.MeshAssetName);
                if (!string.IsNullOrEmpty(targetMeshName) &&
                    string.Equals(meshName, targetMeshName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(targetMeshName) &&
                    meshName.IndexOf(targetMeshName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(data.MeshSelectionHint) &&
                meshName.IndexOf(data.MeshSelectionHint, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(data.TargetName) &&
                meshName.IndexOf(data.TargetName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static string NormalizeAssetName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var normalized = name.Replace("(Clone)", string.Empty);
            normalized = normalized.Trim();
            return normalized;
        }

        private List<Material> BuildScoredMaterialList(MeshOverrideData data, Material[] materials)
        {
            var result = new List<(Material material, float score)>();
            if (materials == null)
                return new List<Material>();

            for (int i = 0; i < materials.Length; i++)
            {
                var mat = materials[i];
                if (mat == null)
                    continue;

                var name = mat.name ?? string.Empty;
                var score = 0f;

                if (data.MaterialSelectionHints != null)
                {
                    for (int hintIndex = 0; hintIndex < data.MaterialSelectionHints.Length; hintIndex++)
                    {
                        var hint = data.MaterialSelectionHints[hintIndex];
                        if (string.IsNullOrWhiteSpace(hint))
                            continue;
                        if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            score += 100f - hintIndex;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(data.MeshAssetName) && name.IndexOf(data.MeshAssetName, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 60f;

                if (!string.IsNullOrEmpty(data.TargetName) && name.IndexOf(data.TargetName, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 40f;

                if (!string.IsNullOrEmpty(data.MeshSelectionHint) && name.IndexOf(data.MeshSelectionHint, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 30f;

                if (mat.mainTexture != null)
                    score += 10f;

                if (name.IndexOf("default", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("unity", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score -= 15f;
                }

                if (score <= 0f && i < materials.Length)
                {
                    score += Math.Max(1f, 5f - i);
                }

                result.Add((mat, score));
            }

            return result
                .OrderByDescending(entry => entry.score)
                .ThenBy(entry => entry.material?.name, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.material)
                .ToList();
        }

        private void AddMaterialOverrideFromAsset(MeshOverrideData data, Material sourceMaterial, int slot, bool logDetails)
        {
            if (data == null || sourceMaterial == null)
                return;

            if (data.MaterialOverrides.Any(existing => existing != null && existing.Slot == slot))
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

        private bool ApplyMeshCardMetadata(Dictionary<string, object> entryDict, MeshOverrideData data, bool logDetails)
        {
            var cardOverride = BuildCardOverrideFromMeshEntry(entryDict, data?.TargetName);
            if (cardOverride == null)
                return false;

            data.CardOverride = cardOverride;
            RegisterMeshCardOverride(cardOverride);

            if (logDetails)
            {
                _logger.LogInfo($"[CustomTextureReplacer] Mesh override provided card metadata for '{cardOverride.Id}' (target '{data?.TargetName}').");
            }

            SafeAppendDebug($"Mesh override card metadata registered for {cardOverride.Id} via {data?.TargetName}");
            return true;
        }

        private CardTextOverride BuildCardOverrideFromMeshEntry(Dictionary<string, object> entryDict, string targetName)
        {
            if (entryDict == null)
                return null;

            var cardDict = GetDictionary(entryDict, "card") ?? GetDictionary(entryDict, "cardOverride");
            if (cardDict == null)
                return null;

            string ResolveString(string key) => Prefer(GetString(cardDict, key), GetString(entryDict, key));
            string ResolveStringAlt(string primary, string alt) => Prefer(ResolveString(primary), ResolveString(alt));

            var id = ResolveStringAlt("Id", "cardId");
            if (string.IsNullOrEmpty(id))
                id = DeriveCardIdFromTarget(targetName);

            if (string.IsNullOrEmpty(id))
                return null;

            var displayName = ResolveStringAlt("DisplayName", "displayName");
            var subtitle = ResolveStringAlt("Subtitle", "subtitle");
            var cardNumber = ResolveStringAlt("CardNumber", "cardNumber");
            var description = ResolveStringAlt("Description", "description");
            var evolvesFrom = ResolveStringAlt("EvolvesFrom", "evolvesFrom");
            var artist = ResolveStringAlt("Artist", "artist");
            var stat1 = ResolveStringAlt("Stat1", "stat1");
            var stat2 = ResolveStringAlt("Stat2", "stat2");
            var stat3 = ResolveStringAlt("Stat3", "stat3");
            var stat4 = ResolveStringAlt("Stat4", "stat4");
            var rarity = ResolveStringAlt("Rarity", "rarity");
            var fame = ResolveStringAlt("Fame", "fame");

            var aliases = new List<string>();
            void AddAliases(Dictionary<string, object> source, string key)
            {
                if (source == null)
                    return;
                foreach (var alias in GetStringArray(source, key))
                {
                    if (!string.IsNullOrWhiteSpace(alias))
                        aliases.Add(alias.Trim());
                }
            }

            AddAliases(entryDict, "Aliases");
            AddAliases(entryDict, "aliases");
            AddAliases(cardDict, "Aliases");
            AddAliases(cardDict, "aliases");

            if (!string.IsNullOrEmpty(targetName))
                aliases.Add(targetName);

            var derivedId = DeriveCardIdFromTarget(targetName);
            if (!string.IsNullOrEmpty(derivedId))
                aliases.Add(derivedId);

            aliases = aliases
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrEmpty(displayName) && aliases.Count == 0 && string.IsNullOrEmpty(description))
                return null;

            var entry = new CardTextOverride
            {
                Id = id.Trim(),
                DisplayName = displayName ?? string.Empty,
                Subtitle = subtitle,
                CardNumber = cardNumber,
                Description = description,
                EvolvesFrom = evolvesFrom,
                Artist = artist,
                Stat1 = stat1,
                Stat2 = stat2,
                Stat3 = stat3,
                Stat4 = stat4,
                Rarity = rarity,
                Fame = fame,
                Aliases = aliases.ToArray(),
                IsMeshOverride = true
            };

            return entry;
        }

        private static string DeriveCardIdFromTarget(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return string.Empty;

            var trimmed = targetName.Trim();
            var suffixes = new[]
            {
                "_Mesh", "_mesh", "_Model", "_model", "_Prefab", "_prefab"
            };

            foreach (var suffix in suffixes)
            {
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(0, trimmed.Length - suffix.Length);
                    break;
                }
            }

            return trimmed;
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
                if (entry != null)
                {
                    RestoreHiddenInstances(entry);
                    entry.InstanceIds.Clear();
                    entry.InstanceReferences.Clear();
                    entry.HiddenInstanceIds.Clear();
                }

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
            _meshInstanceTargetByComponent.Clear();
            _shelfMeshNameCache.Clear();
            _loggedShelfCapacityWarnings.Clear();
            _meshOverrideRetryDeadline = 0f;
            _meshOverrideRetryLogged = false;

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
                    if (_meshFilterOriginalMeshes.TryGetValue(pair.Key, out var original))
                    {
                        if (original == null)
                        {
                            _meshFilterOriginalMeshNames.TryGetValue(pair.Key, out var cachedName);
                            LogMissingOriginal(_meshFilterMissingOriginalLogged, pair.Key, "MeshFilter", cachedName ?? filter.sharedMesh?.name ?? filter.name);
                        }

                        if (!ReferenceEquals(filter.sharedMesh, original))
                            filter.sharedMesh = original;
                    }
                    else
                    {
                        _meshFilterOriginalMeshNames.TryGetValue(pair.Key, out var cachedName);
                        LogMissingOriginal(_meshFilterMissingOriginalLogged, pair.Key, "MeshFilter", cachedName ?? filter.sharedMesh?.name ?? filter.name);
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
                    if (_skinnedOriginalMeshes.TryGetValue(pair.Key, out var original))
                    {
                        if (original == null)
                        {
                            _skinnedOriginalMeshNames.TryGetValue(pair.Key, out var cachedName);
                            LogMissingOriginal(_skinnedMissingOriginalLogged, pair.Key, "SkinnedMeshRenderer", cachedName ?? renderer.sharedMesh?.name ?? renderer.name);
                        }

                        if (!ReferenceEquals(renderer.sharedMesh, original))
                            renderer.sharedMesh = original;
                    }
                    else
                    {
                        _skinnedOriginalMeshNames.TryGetValue(pair.Key, out var cachedName);
                        LogMissingOriginal(_skinnedMissingOriginalLogged, pair.Key, "SkinnedMeshRenderer", cachedName ?? renderer.sharedMesh?.name ?? renderer.name);
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
                    if (_meshColliderOriginalMeshes.TryGetValue(pair.Key, out var original))
                    {
                        if (original == null)
                        {
                            _meshColliderOriginalMeshNames.TryGetValue(pair.Key, out var cachedName);
                            LogMissingOriginal(_meshColliderMissingOriginalLogged, pair.Key, "MeshCollider", cachedName ?? collider.sharedMesh?.name ?? collider.name);
                        }

                        if (!ReferenceEquals(collider.sharedMesh, original))
                            collider.sharedMesh = original;
                    }
                    else
                    {
                        _meshColliderOriginalMeshNames.TryGetValue(pair.Key, out var cachedName);
                        LogMissingOriginal(_meshColliderMissingOriginalLogged, pair.Key, "MeshCollider", cachedName ?? collider.sharedMesh?.name ?? collider.name);
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

        internal void RequestMeshOverrideRescan(float delaySeconds = 0.05f, float retryWindowSeconds = MeshOverrideRetryWindowSeconds)
        {
            ScheduleMeshOverrideReapply(delaySeconds, retryWindowSeconds);
        }

        private void ScheduleMeshOverrideReapply(float delaySeconds = 0.05f, float retryWindowSeconds = MeshOverrideRetryWindowSeconds)
        {
            if (_meshOverridesByTarget.Count == 0)
                return;

            var now = Time.realtimeSinceStartup;
            var minDelay = Mathf.Max(delaySeconds, MeshOverrideRescanMinimumDelay);
            var requestedTime = now + minDelay;

            if (_meshOverridesDirty)
            {
                // If an earlier scan is already scheduled, keep it; otherwise adopt the new time.
                if (requestedTime > _nextMeshOverrideScanTime)
                    _nextMeshOverrideScanTime = requestedTime;
            }
            else
            {
                _meshOverridesDirty = true;
                _nextMeshOverrideScanTime = requestedTime;
            }

            if (retryWindowSeconds > 0f)
            {
                var deadlineCandidate = now + retryWindowSeconds;
                if (deadlineCandidate > _meshOverrideRetryDeadline)
                    _meshOverrideRetryDeadline = deadlineCandidate;
                _meshOverrideRetryLogged = false;
            }
        }

        private void ApplyMeshOverrides()
        {
            if (!_meshOverridesDirty)
                return;

            if (Time.realtimeSinceStartup < _nextMeshOverrideScanTime)
                return;

            _meshOverridesDirty = false;

            var anyTargetPresent = false;
            var appliedAny =
                ApplyMeshOverridesToMeshFilters(ref anyTargetPresent) |
                ApplyMeshOverridesToSkinnedRenderers(ref anyTargetPresent) |
                ApplyMeshOverridesToMeshColliders(ref anyTargetPresent);

            CleanupMeshFilterEntries();
            CleanupSkinnedRendererEntries();
            CleanupMeshColliderEntries();
            CleanupRendererEntries();
            EnforceMeshOverrideInstanceCounts();

            var now = Time.realtimeSinceStartup;
            var hasOverrides = _meshOverridesByTarget.Count > 0;
            var withinRetryWindow = now < _meshOverrideRetryDeadline;

            if (hasOverrides && withinRetryWindow)
            {
                var nextDelay = appliedAny ? MeshOverrideRescanIntervalApplied : MeshOverrideRescanIntervalPending;
                ScheduleMeshOverrideReapply(nextDelay, 0f);
            }
            else if (hasOverrides && !withinRetryWindow && !anyTargetPresent && !_meshOverrideRetryLogged)
            {
                SafeAppendDebug("Mesh override retry window elapsed without detecting target meshes.");
                _meshOverrideRetryLogged = true;
                _meshOverrideRetryDeadline = 0f;
            }
            else if (!hasOverrides)
            {
                _meshOverrideRetryDeadline = 0f;
                _meshOverrideRetryLogged = false;
            }
        }

        private bool ApplyMeshOverridesToMeshFilters(ref bool anyTargetPresent)
        {
            var applied = false;
            foreach (var filter in Resources.FindObjectsOfTypeAll<MeshFilter>())
            {
                if (filter == null)
                    continue;

                var id = filter.GetInstanceID();
                if (!_meshFilterReferences.ContainsKey(id))
                {
                    _meshFilterReferences[id] = new WeakReference<MeshFilter>(filter);
                    _meshFilterOriginalMeshes[id] = filter.sharedMesh;
                    RememberMeshFilterOriginalName(id, filter.sharedMesh, filter);
                }

                if (!_meshFilterOriginalMeshes.TryGetValue(id, out var originalMesh))
                {
                    originalMesh = filter.sharedMesh;
                    _meshFilterOriginalMeshes[id] = originalMesh;
                    RememberMeshFilterOriginalName(id, originalMesh, filter);
                }
                else if (originalMesh == null && !_meshFilterOverrideIds.Contains(id))
                {
                    var currentMesh = filter.sharedMesh;
                    if (currentMesh != null)
                    {
                        originalMesh = currentMesh;
                        _meshFilterOriginalMeshes[id] = currentMesh;
                        RememberMeshFilterOriginalName(id, currentMesh, filter);
                    }
                }
                else if (!_meshFilterOverrideIds.Contains(id) &&
                         originalMesh != null &&
                         filter.sharedMesh != null &&
                         !ReferenceEquals(originalMesh, filter.sharedMesh))
                {
                    originalMesh = filter.sharedMesh;
                    _meshFilterOriginalMeshes[id] = originalMesh;
                    RememberMeshFilterOriginalName(id, originalMesh, filter);
                }

                var targetName = ResolveMeshFilterTargetName(id, filter, originalMesh);
                if (!string.IsNullOrEmpty(targetName) &&
                    _meshOverridesByTarget.TryGetValue(targetName, out var overrideData) &&
                    overrideData != null)
                {
                    anyTargetPresent = true;

                    if (overrideData.ApplyToMeshFilter)
                    {
                        var currentMesh = filter.sharedMesh;
                        var replacement = overrideData.MeshInstance;
                        if (_meshFilterOverrideIds.Contains(id) &&
                            currentMesh != null &&
                            replacement != null &&
                            !ReferenceEquals(currentMesh, replacement) &&
                            !ReferenceEquals(currentMesh, originalMesh))
                        {
                            if (_meshInstanceTargetByComponent.TryGetValue(id, out var previousTarget) &&
                                _meshOverridesByTarget.TryGetValue(previousTarget, out var previousData))
                            {
                                UnregisterMeshInstance(previousData, filter.gameObject);
                            }
                            _meshInstanceTargetByComponent.Remove(id);
                            _meshFilterOverrideIds.Remove(id);
                            _meshFilterOriginalMeshes[id] = currentMesh;
                            RememberMeshFilterOriginalName(id, currentMesh, filter);
                            var componentRenderer = filter.GetComponent<Renderer>();
                            if (componentRenderer != null)
                            {
                                RestoreRendererMaterials(componentRenderer);
                            }
                            applied = true;
                            continue;
                        }

                        RegisterMeshInstance(overrideData, filter.gameObject);
                        _meshInstanceTargetByComponent[id] = overrideData.TargetName;

                        if (replacement != null && !ReferenceEquals(filter.sharedMesh, replacement))
                        {
                            filter.sharedMesh = replacement;
                            AutoAdjustBoxColliders(overrideData, filter.transform, replacement);
                            applied = true;
                            SafeAppendDebug($"Mesh override applied to MeshFilter '{targetName}' using '{replacement.name}'.");
                            if (_logAssetLoads.Value)
                                _logger.LogInfo($"[CustomTextureReplacer] MeshFilter '{targetName}' swapped to '{replacement.name}'.");
                        }

                        _meshFilterOverrideIds.Add(id);

                        var renderer = filter.GetComponent<Renderer>();
                        if (renderer != null && ApplyRendererMaterialOverrides(renderer, overrideData))
                        {
                            applied = true;
                        }
                    }
                    else if (_meshFilterOverrideIds.Remove(id))
                    {
                        if (_meshInstanceTargetByComponent.TryGetValue(id, out var previousTarget) &&
                            _meshOverridesByTarget.TryGetValue(previousTarget, out var previousData))
                        {
                            UnregisterMeshInstance(previousData, filter.gameObject);
                        }
                        _meshInstanceTargetByComponent.Remove(id);

                        if (originalMesh != null && !ReferenceEquals(filter.sharedMesh, originalMesh))
                        {
                            filter.sharedMesh = originalMesh;
                            AutoAdjustBoxColliders(overrideData, filter.transform, originalMesh);
                            applied = true;
                        }

                        var renderer = filter.GetComponent<Renderer>();
                        if (renderer != null && RestoreRendererMaterials(renderer))
                        {
                            applied = true;
                        }
                    }
                }
                else if (_meshFilterOverrideIds.Remove(id))
                {
                    MeshOverrideData previousData = null;
                    if (_meshInstanceTargetByComponent.TryGetValue(id, out var previousTarget) &&
                        _meshOverridesByTarget.TryGetValue(previousTarget, out previousData))
                    {
                        UnregisterMeshInstance(previousData, filter.gameObject);
                    }
                    _meshInstanceTargetByComponent.Remove(id);

                    if (originalMesh != null && !ReferenceEquals(filter.sharedMesh, originalMesh))
                    {
                        filter.sharedMesh = originalMesh;
                        AutoAdjustBoxColliders(previousData, filter.transform, originalMesh);
                        applied = true;
                    }

                    var renderer = filter.GetComponent<Renderer>();
                    if (renderer != null && RestoreRendererMaterials(renderer))
                    {
                        applied = true;
                    }
                }
            }

            return applied;
        }

        private bool ApplyMeshOverridesToSkinnedRenderers(ref bool anyTargetPresent)
        {
            var applied = false;
            foreach (var renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
            {
                if (renderer == null)
                    continue;

                var id = renderer.GetInstanceID();
                if (!_skinnedRendererReferences.ContainsKey(id))
                {
                    _skinnedRendererReferences[id] = new WeakReference<SkinnedMeshRenderer>(renderer);
                    _skinnedOriginalMeshes[id] = renderer.sharedMesh;
                    RememberSkinnedOriginalName(id, renderer.sharedMesh, renderer);
                }

                if (!_skinnedOriginalMeshes.TryGetValue(id, out var originalMesh))
                {
                    originalMesh = renderer.sharedMesh;
                    _skinnedOriginalMeshes[id] = originalMesh;
                    RememberSkinnedOriginalName(id, originalMesh, renderer);
                }
                else if (originalMesh == null && !_skinnedRendererOverrideIds.Contains(id))
                {
                    var currentMesh = renderer.sharedMesh;
                    if (currentMesh != null)
                    {
                        originalMesh = currentMesh;
                        _skinnedOriginalMeshes[id] = currentMesh;
                        RememberSkinnedOriginalName(id, currentMesh, renderer);
                    }
                }
                else if (!_skinnedRendererOverrideIds.Contains(id) &&
                         originalMesh != null &&
                         renderer.sharedMesh != null &&
                         !ReferenceEquals(originalMesh, renderer.sharedMesh))
                {
                    originalMesh = renderer.sharedMesh;
                    _skinnedOriginalMeshes[id] = originalMesh;
                    RememberSkinnedOriginalName(id, originalMesh, renderer);
                }

                var targetName = ResolveSkinnedTargetName(id, renderer, originalMesh);
                if (!string.IsNullOrEmpty(targetName) &&
                    _meshOverridesByTarget.TryGetValue(targetName, out var overrideData) &&
                    overrideData != null)
                {
                    anyTargetPresent = true;

                    if (overrideData.ApplyToSkinnedMeshRenderer)
                    {
                        var currentMesh = renderer.sharedMesh;
                        var replacement = overrideData.MeshInstance;
                        if (_skinnedRendererOverrideIds.Contains(id) &&
                            currentMesh != null &&
                            replacement != null &&
                            !ReferenceEquals(currentMesh, replacement) &&
                            !ReferenceEquals(currentMesh, originalMesh))
                        {
                            if (_meshInstanceTargetByComponent.TryGetValue(id, out var previousTarget) &&
                                _meshOverridesByTarget.TryGetValue(previousTarget, out var previousData))
                            {
                                UnregisterMeshInstance(previousData, renderer.gameObject);
                            }
                            _meshInstanceTargetByComponent.Remove(id);
                            _skinnedRendererOverrideIds.Remove(id);
                            _skinnedOriginalMeshes[id] = currentMesh;
                            RememberSkinnedOriginalName(id, currentMesh, renderer);
                            RestoreRendererMaterials(renderer);
                            applied = true;
                            continue;
                        }

                        RegisterMeshInstance(overrideData, renderer.gameObject);
                        _meshInstanceTargetByComponent[id] = overrideData.TargetName;

                        if (replacement != null && !ReferenceEquals(renderer.sharedMesh, replacement))
                        {
                            renderer.sharedMesh = replacement;
                            AutoAdjustBoxColliders(overrideData, renderer.transform, replacement);
                            applied = true;
                            SafeAppendDebug($"Mesh override applied to SkinnedMeshRenderer '{targetName}' using '{replacement.name}'.");
                            if (_logAssetLoads.Value)
                                _logger.LogInfo($"[CustomTextureReplacer] SkinnedMeshRenderer '{targetName}' swapped to '{replacement.name}'.");
                        }

                        _skinnedRendererOverrideIds.Add(id);
                        if (ApplyRendererMaterialOverrides(renderer, overrideData))
                        {
                            applied = true;
                        }
                    }
                    else if (_skinnedRendererOverrideIds.Remove(id))
                    {
                        if (_meshInstanceTargetByComponent.TryGetValue(id, out var previousTarget) &&
                            _meshOverridesByTarget.TryGetValue(previousTarget, out var previousData))
                        {
                            UnregisterMeshInstance(previousData, renderer.gameObject);
                        }
                        _meshInstanceTargetByComponent.Remove(id);

                        if (originalMesh != null && !ReferenceEquals(renderer.sharedMesh, originalMesh))
                        {
                            renderer.sharedMesh = originalMesh;
                            AutoAdjustBoxColliders(overrideData, renderer.transform, originalMesh);
                            applied = true;
                        }
                        if (RestoreRendererMaterials(renderer))
                        {
                            applied = true;
                        }
                    }
                }
                else if (_skinnedRendererOverrideIds.Remove(id))
                {
                    MeshOverrideData previousData = null;
                    if (_meshInstanceTargetByComponent.TryGetValue(id, out var previousTarget) &&
                        _meshOverridesByTarget.TryGetValue(previousTarget, out previousData))
                    {
                        UnregisterMeshInstance(previousData, renderer.gameObject);
                    }
                    _meshInstanceTargetByComponent.Remove(id);

                    if (originalMesh != null && !ReferenceEquals(renderer.sharedMesh, originalMesh))
                    {
                        renderer.sharedMesh = originalMesh;
                        AutoAdjustBoxColliders(previousData, renderer.transform, originalMesh);
                        applied = true;
                    }
                    if (RestoreRendererMaterials(renderer))
                    {
                        applied = true;
                    }
                }
            }

            return applied;
        }

        private bool ApplyMeshOverridesToMeshColliders(ref bool anyTargetPresent)
        {
            var applied = false;
            foreach (var collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
            {
                if (collider == null)
                    continue;

                var id = collider.GetInstanceID();
                if (!_meshColliderReferences.ContainsKey(id))
                {
                    _meshColliderReferences[id] = new WeakReference<MeshCollider>(collider);
                    _meshColliderOriginalMeshes[id] = collider.sharedMesh;
                    RememberColliderOriginalName(id, collider.sharedMesh, collider);
                }

                if (!_meshColliderOriginalMeshes.TryGetValue(id, out var originalMesh))
                {
                    originalMesh = collider.sharedMesh;
                    _meshColliderOriginalMeshes[id] = originalMesh;
                    RememberColliderOriginalName(id, originalMesh, collider);
                }
                else if (originalMesh == null && !_meshColliderOverrideIds.Contains(id))
                {
                    var currentMesh = collider.sharedMesh;
                    if (currentMesh != null)
                    {
                        originalMesh = currentMesh;
                        _meshColliderOriginalMeshes[id] = currentMesh;
                        RememberColliderOriginalName(id, currentMesh, collider);
                    }
                }
                else if (!_meshColliderOverrideIds.Contains(id) &&
                         originalMesh != null &&
                         collider.sharedMesh != null &&
                         !ReferenceEquals(originalMesh, collider.sharedMesh))
                {
                    originalMesh = collider.sharedMesh;
                    _meshColliderOriginalMeshes[id] = originalMesh;
                    RememberColliderOriginalName(id, originalMesh, collider);
                }

                var targetName = ResolveMeshColliderTargetName(id, collider, originalMesh);
                if (!string.IsNullOrEmpty(targetName) &&
                    _meshOverridesByTarget.TryGetValue(targetName, out var overrideData) &&
                    overrideData != null)
                {
                    anyTargetPresent = true;

                    if (overrideData.ApplyToMeshCollider)
                    {
                        var currentMesh = collider.sharedMesh;
                        var replacement = overrideData.MeshInstance;
                        if (_meshColliderOverrideIds.Contains(id) &&
                            currentMesh != null &&
                            replacement != null &&
                            !ReferenceEquals(currentMesh, replacement) &&
                            !ReferenceEquals(currentMesh, originalMesh))
                        {
                            if (_meshInstanceTargetByComponent.TryGetValue(id, out var previousTarget) &&
                                _meshOverridesByTarget.TryGetValue(previousTarget, out var previousData))
                            {
                                UnregisterMeshInstance(previousData, collider.gameObject);
                            }
                            _meshInstanceTargetByComponent.Remove(id);
                            _meshColliderOverrideIds.Remove(id);
                            _meshColliderOriginalMeshes[id] = currentMesh;
                            RememberColliderOriginalName(id, currentMesh, collider);
                            applied = true;
                            continue;
                        }

                        RegisterMeshInstance(overrideData, collider.gameObject);
                        _meshInstanceTargetByComponent[id] = overrideData.TargetName;

                        if (replacement != null && !ReferenceEquals(collider.sharedMesh, replacement))
                        {
                            collider.sharedMesh = replacement;
                            applied = true;
                            SafeAppendDebug($"Mesh override applied to MeshCollider '{targetName}' using '{replacement.name}'.");
                        }

                        _meshColliderOverrideIds.Add(id);
                    }
                    else if (_meshColliderOverrideIds.Remove(id))
                    {
                        if (_meshInstanceTargetByComponent.TryGetValue(id, out var previousTarget) &&
                            _meshOverridesByTarget.TryGetValue(previousTarget, out var previousData))
                        {
                            UnregisterMeshInstance(previousData, collider.gameObject);
                        }
                        _meshInstanceTargetByComponent.Remove(id);

                        if (originalMesh != null && !ReferenceEquals(collider.sharedMesh, originalMesh))
                        {
                            collider.sharedMesh = originalMesh;
                            applied = true;
                        }
                    }
                }
                else if (_meshColliderOverrideIds.Remove(id))
                {
                    if (_meshInstanceTargetByComponent.TryGetValue(id, out var previousTarget) &&
                        _meshOverridesByTarget.TryGetValue(previousTarget, out var previousData))
                    {
                        UnregisterMeshInstance(previousData, collider.gameObject);
                    }
                    _meshInstanceTargetByComponent.Remove(id);

                    if (originalMesh != null && !ReferenceEquals(collider.sharedMesh, originalMesh))
                    {
                        collider.sharedMesh = originalMesh;
                        applied = true;
                    }
                }
            }

            return applied;
        }

        private void RegisterMeshInstance(MeshOverrideData data, GameObject instance)
        {
            if (data == null || instance == null)
                return;

            var id = instance.GetInstanceID();
            if (!data.InstanceIds.Add(id))
                return;

            data.InstanceReferences.Add(new WeakReference<GameObject>(instance));
            data.HiddenInstanceIds.Remove(id);
            ApplyMeshInstanceNameOverrides(data, instance);
        }

        private void UnregisterMeshInstance(MeshOverrideData data, GameObject instance)
        {
            if (data == null || instance == null)
                return;

            var id = instance.GetInstanceID();
            data.InstanceIds.Remove(id);
            if (data.HiddenInstanceIds.Remove(id) && !instance.activeSelf)
            {
                instance.SetActive(true);
            }

            for (int i = data.InstanceReferences.Count - 1; i >= 0; i--)
            {
                if (!data.InstanceReferences[i].TryGetTarget(out var existing) || existing == null || existing.GetInstanceID() == id)
                {
                    data.InstanceReferences.RemoveAt(i);
                }
            }
        }

        private void AutoAdjustBoxColliders(MeshOverrideData data, Transform meshTransform, Mesh mesh)
        {
            if (data == null || meshTransform == null || mesh == null || !data.AutoAdjustBoxColliders)
                return;

            if (!PrepareColliderVertexScratch(mesh))
                return;

            CollectBoxColliders(meshTransform, data, _boxColliderScratch);
            if (_boxColliderScratch.Count == 0)
                return;

            foreach (var collider in _boxColliderScratch)
            {
                if (collider == null)
                    continue;

                RecalculateBoxCollider(collider, meshTransform, _colliderVertexScratch);
            }

            _boxColliderScratch.Clear();
        }

        private bool PrepareColliderVertexScratch(Mesh mesh)
        {
            if (mesh == null)
                return false;

            var count = mesh.vertexCount;
            if (count <= 0)
                return false;

            if (_colliderVertexScratch.Capacity < count)
                _colliderVertexScratch.Capacity = count;
            _colliderVertexScratch.Clear();
            mesh.GetVertices(_colliderVertexScratch);
            return _colliderVertexScratch.Count > 0;
        }

        private void CollectBoxColliders(Transform origin, MeshOverrideData data, List<BoxCollider> scratch)
        {
            scratch.Clear();
            if (origin == null || data == null)
                return;

            switch (data.BoxColliderScope)
            {
                case BoxColliderSearchScope.SelfAndChildren:
                    origin.GetComponentsInChildren(includeInactive: true, scratch);
                    break;
                case BoxColliderSearchScope.Parents:
                    var current = origin;
                    while (current != null)
                    {
                        current.GetComponents(scratch);
                        if (scratch.Count > 0)
                            break;
                        current = current.parent;
                    }
                    break;
                default:
                    origin.GetComponents(scratch);
                    break;
            }

            for (int i = scratch.Count - 1; i >= 0; i--)
            {
                if (scratch[i] == null)
                    scratch.RemoveAt(i);
            }
        }

        private void RecalculateBoxCollider(BoxCollider collider, Transform meshTransform, List<Vector3> vertices)
        {
            if (collider == null || meshTransform == null || vertices == null || vertices.Count == 0)
                return;

            var colliderTransform = collider.transform;
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            for (int i = 0; i < vertices.Count; i++)
            {
                var world = meshTransform.TransformPoint(vertices[i]);
                var local = colliderTransform.InverseTransformPoint(world);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }

            if (float.IsInfinity(min.x) || float.IsInfinity(max.x))
                return;

            var size = max - min;
            if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
                return;

            collider.center = (min + max) * 0.5f;
            collider.size = size;
        }

        private void ApplyMeshInstanceNameOverrides(MeshOverrideData data, GameObject instance)
        {
            if (data == null || instance == null)
                return;

            var cardOverride = data.CardOverride;
            if (cardOverride == null || !HasAnyCardOverrides())
                return;

            try
            {
                foreach (var text in EnumerateNearbyTextComponents(instance))
                {
                    if (text == null)
                        continue;

                    string originalLabel;
                    try
                    {
                        originalLabel = text.text;
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(originalLabel))
                        continue;

                    if (!LabelMatchesOverride(originalLabel, data))
                    {
                        LogMeshLabelCandidate(data, text, originalLabel, matched: false);
                        continue;
                    }

                    ReplaceNameLabel(text, cardOverride, originalLabel);
                    LogMeshLabelCandidate(data, text, originalLabel, matched: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[CustomTextureReplacer] ApplyMeshInstanceNameOverrides failed: {ex.Message}");
                SafeAppendDebug($"ApplyMeshInstanceNameOverrides exception: {ex.Message}");
            }
        }

        private void ReapplyMeshInstanceNameOverrides()
        {
            if (!HasAnyCardOverrides())
                return;

            try
            {
                foreach (var data in _meshOverridesByTarget.Values)
                {
                    if (data == null || data.CardOverride == null)
                        continue;

                    for (int i = data.InstanceReferences.Count - 1; i >= 0; i--)
                    {
                        if (!data.InstanceReferences[i].TryGetTarget(out var instance) || instance == null)
                        {
                            data.InstanceReferences.RemoveAt(i);
                            continue;
                        }

                        ApplyMeshInstanceNameOverrides(data, instance);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[CustomTextureReplacer] ReapplyMeshInstanceNameOverrides failed: {ex.Message}");
                SafeAppendDebug($"ReapplyMeshInstanceNameOverrides exception: {ex.Message}");
            }
        }

        private IEnumerable<TMP_Text> EnumerateNearbyTextComponents(GameObject instance)
        {
            if (instance == null)
                yield break;

            var visited = new HashSet<int>();
            var current = instance.transform;
            var depth = 0;

            while (current != null && depth < MeshLabelSearchParentDepth)
            {
                TMP_Text[] buffer;
                try
                {
                    buffer = current.GetComponentsInChildren<TMP_Text>(true);
                }
                catch
                {
                    buffer = Array.Empty<TMP_Text>();
                }

                foreach (var text in buffer)
                {
                    if (text == null)
                        continue;

                    var id = text.GetInstanceID();
                    if (visited.Add(id))
                        yield return text;
                }

                current = current.parent;
                depth++;
            }
        }

        private static bool LabelMatchesOverride(string label, MeshOverrideData data)
        {
            if (string.IsNullOrEmpty(label) || data?.CardOverride == null)
                return false;

            if (MatchesCandidate(label, data))
                return true;

            var prefix = GetNamePrefix(label);
            if (!string.IsNullOrEmpty(prefix) && MatchesCandidate(prefix, data))
                return true;

            return false;
        }

        private static bool MatchesCandidate(string candidate, MeshOverrideData data)
        {
            if (data?.CardOverride == null)
                return false;

            var trimmed = candidate?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return false;

            var entry = data.CardOverride;

            if (!string.IsNullOrEmpty(entry.DisplayName) &&
                string.Equals(trimmed, entry.DisplayName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(entry.Id) &&
                string.Equals(trimmed, entry.Id, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(data.TargetName) &&
                string.Equals(trimmed, data.TargetName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (entry.Aliases != null)
            {
                foreach (var alias in entry.Aliases)
                {
                    if (string.IsNullOrEmpty(alias))
                        continue;

                    if (string.Equals(trimmed, alias.Trim(), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private void LogMeshLabelCandidate(MeshOverrideData data, TMP_Text text, string label, bool matched)
        {
            if (data == null || text == null)
                return;

            var path = GetTransformPath(text.transform, 32);
            var key = $"{data.TargetName}|{label}|{path}|{matched}";
            if (!_loggedMeshLabelCandidates.Add(key))
                return;

            var status = matched ? "match" : "no-match";
            SafeAppendDebug($"Mesh label {status} for '{data.TargetName}': '{label}' @ {path}");
        }

        private static string GetTransformPath(Transform transform, int maxDepth)
        {
            if (transform == null)
                return "<null>";

            var names = new List<string>();
            var current = transform;
            var depth = 0;
            while (current != null && depth < maxDepth)
            {
                names.Add(current.name);
                current = current.parent;
                depth++;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private void EnforceMeshOverrideInstanceCounts()
        {
            foreach (var data in _meshOverridesByTarget.Values)
            {
                if (data == null)
                    continue;

                if (data.DesiredInstanceCount < 0)
                {
                    RestoreHiddenInstances(data);
                    continue;
                }

                ApplyMeshInstanceLimit(data);
            }
        }

        private void ApplyMeshInstanceLimit(MeshOverrideData data)
        {
            if (data == null)
                return;

            var desired = data.DesiredInstanceCount;
            if (desired < 0)
                return;

            var alive = new List<GameObject>();
            for (int i = data.InstanceReferences.Count - 1; i >= 0; i--)
            {
                if (data.InstanceReferences[i].TryGetTarget(out var go) && go != null)
                {
                    data.InstanceIds.Add(go.GetInstanceID());
                    alive.Add(go);
                }
                else
                {
                    data.InstanceReferences.RemoveAt(i);
                }
            }

            if (alive.Count == 0)
            {
                data.HiddenInstanceIds.Clear();
                return;
            }

            var groups = new Dictionary<int, List<GameObject>>();
            foreach (var go in alive)
            {
                var parent = go.transform.parent != null ? go.transform.parent.gameObject : go;
                var parentId = parent.GetInstanceID();
                if (!groups.TryGetValue(parentId, out var list))
                {
                    list = new List<GameObject>();
                    groups[parentId] = list;
                }
                list.Add(go);
            }

            foreach (var pair in groups)
            {
                var list = pair.Value;
                list.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));

                for (int i = 0; i < list.Count; i++)
                {
                    var go = list[i];
                    var id = go.GetInstanceID();
                    if (i < desired)
                    {
                        if (!go.activeSelf)
                        {
                            go.SetActive(true);
                        }
                        data.HiddenInstanceIds.Remove(id);
                    }
                    else
                    {
                        if (go.activeSelf)
                        {
                            go.SetActive(false);
                        }
                        data.HiddenInstanceIds.Add(id);
                    }
                }

                if (desired > list.Count)
                {
                    SafeAppendDebug($"Mesh override '{data.TargetName}' requested {desired} instances but only {list.Count} exist under parent '{pair.Key}'.");
                }
            }
        }
        private void RestoreHiddenInstances(MeshOverrideData data)
        {
            if (data == null || data.HiddenInstanceIds.Count == 0)
                return;

            for (int i = data.InstanceReferences.Count - 1; i >= 0; i--)
            {
                if (!data.InstanceReferences[i].TryGetTarget(out var go) || go == null)
                {
                    data.InstanceReferences.RemoveAt(i);
                    continue;
                }

                var id = go.GetInstanceID();
                if (data.HiddenInstanceIds.Remove(id) && !go.activeSelf)
                {
                    go.SetActive(true);
                }
            }

            data.HiddenInstanceIds.Clear();
        }
        internal void AdjustShelfSpawn(ShelfCompartment compartment, ref int amount)
        {
            if (compartment == null || amount <= 0 || _meshOverridesByTarget.Count == 0)
                return;

            if (!TryGetShelfOverride(compartment, out var data, out var meshName))
                return;

            var desiredPerSlot = data.DesiredShelfCapacity;
            if (desiredPerSlot < 0)
                return;

            var anchors = GetShelfAnchorCount(compartment);
            var slotKey = GetShelfSlotKey(compartment);
            var slotState = EnsureShelfSlotState(data, slotKey, anchors);
            if (slotState == null)
                return;

            RefreshShelfSlotAllocations(data);

            var slotLimit = slotState.DesiredCapacity >= 0 ? slotState.DesiredCapacity : desiredPerSlot;
            if (slotLimit < 0)
                slotLimit = desiredPerSlot;

            var targetCapacity = Math.Max(0, slotLimit);
            var currentCount = GetShelfItemCount(compartment);
            slotState.LastItemCount = currentCount;

            var needed = targetCapacity - currentCount;
            if (needed < 0)
                needed = 0;

            var originalAmount = amount;

            amount = needed;

            SafeAppendDebug($"Shelf slot {slotKey} for '{meshName}': default={slotState.DefaultCapacity}, anchorsNow={anchors}, desiredPerSlot={targetCapacity}, currentCount={currentCount}, needed={needed}, requestedAmount={originalAmount}, appliedAmount={amount}");

            if (anchors > 0 && targetCapacity > anchors)
            {
                var warningKey = string.Concat(meshName, ":", anchors.ToString());
                if (_loggedShelfCapacityWarnings.Add(warningKey))
                {
                    SafeAppendDebug(string.Format("Shelf slot for '{0}' currently exposes {1} anchors; requested {2}.", meshName, anchors, targetCapacity));
                }
            }
        }

        private string GetShelfMeshName(ShelfCompartment compartment)
        {
            if (compartment == null)
                return string.Empty;

            var id = compartment.GetInstanceID();
            if (_shelfMeshNameCache.TryGetValue(id, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;

            if (ShelfCompartmentItemTypeField == null || InventoryGetItemMeshDataMethod == null || ItemMeshDataMeshField == null)
                return string.Empty;

            object itemType;
            try
            {
                itemType = ShelfCompartmentItemTypeField.GetValue(compartment);
            }
            catch
            {
                return string.Empty;
            }

            if (itemType == null)
                return string.Empty;

            object meshData = null;
            try
            {
                meshData = InventoryGetItemMeshDataMethod.Invoke(null, new[] { itemType });
            }
            catch
            {
                return string.Empty;
            }

            if (meshData == null)
                return string.Empty;

            Mesh mesh = null;
            try
            {
                mesh = ItemMeshDataMeshField.GetValue(meshData) as Mesh;
            }
            catch
            {
                return string.Empty;
            }

            var name = mesh?.name ?? string.Empty;
            if (!string.IsNullOrEmpty(name))
                _shelfMeshNameCache[id] = name;

            return name;
        }

        internal int GetShelfSlotKey(ShelfCompartment compartment)
        {
            if (compartment == null)
                return 0;

            if (ShelfCompartmentStoredItemListGrpField != null &&
                ShelfCompartmentStoredItemListGrpField.GetValue(compartment) is Transform group &&
                group != null)
            {
                return group.GetInstanceID();
            }

            return compartment.GetInstanceID();
        }

        internal int GetShelfAnchorCount(ShelfCompartment compartment)
        {
            if (compartment == null)
                return 0;

            var positions = ShelfCompartmentItemPosListField?.GetValue(compartment) as System.Collections.ICollection;
            return positions?.Count ?? 0;
        }

        internal int GetShelfItemCount(ShelfCompartment compartment)
        {
            if (compartment == null)
                return 0;

            if (ShelfCompartmentStoredItemListGrpField != null &&
                ShelfCompartmentStoredItemListGrpField.GetValue(compartment) is Transform group &&
                group != null)
            {
                return group.childCount;
            }

            return 0;
        }

        internal ShelfSlotState EnsureShelfSlotState(MeshOverrideData data, int slotKey, int anchors)
        {
            if (data == null)
                return null;

            var slotCreated = false;
            if (!data.ShelfSlots.TryGetValue(slotKey, out var slotState))
            {
                slotState = new ShelfSlotState();
                data.ShelfSlots[slotKey] = slotState;
                slotCreated = true;
            }

            if (anchors > 0 && slotState.DefaultCapacity != anchors)
            {
                slotState.DefaultCapacity = anchors;
                data.ShelfAllocationsDirty = true;
            }

            if (slotState.LastAnchorCount != anchors)
            {
                slotState.LastAnchorCount = anchors;
                if (slotState.DefaultCapacity <= 0)
                    data.ShelfAllocationsDirty = true;
            }

            if (slotCreated)
                data.ShelfAllocationsDirty = true;

            if (slotState.DesiredCapacity != data.DesiredShelfCapacity)
                data.ShelfAllocationsDirty = true;

            return slotState;
        }

        internal void ApplyShelfLayoutScaling(ShelfCompartment compartment, MeshOverrideData data, int desiredCapacity)
        {
            if (compartment == null || data == null || desiredCapacity <= 0)
                return;

            if (ShelfCompartmentSizeXField == null || ShelfCompartmentCurrentItemSizeXField == null)
                return;

            var sizeXObj = ShelfCompartmentSizeXField.GetValue(compartment);
            if (!(sizeXObj is int sizeX) || sizeX <= 0)
                return;

            var itemSizeXObj = ShelfCompartmentCurrentItemSizeXField.GetValue(compartment);
            var currentItemSizeX = itemSizeXObj is float sx && sx > 0f ? sx : sizeX;

            var sizeY = ShelfCompartmentSizeYField != null ? Convert.ToInt32(ShelfCompartmentSizeYField.GetValue(compartment)) : 1;
            var sizeZ = ShelfCompartmentSizeZField != null ? Convert.ToInt32(ShelfCompartmentSizeZField.GetValue(compartment)) : 1;
            if (sizeY <= 0) sizeY = 1;
            if (sizeZ <= 0) sizeZ = 1;

            var itemSizeY = ShelfCompartmentCurrentItemSizeYField != null ? Convert.ToSingle(ShelfCompartmentCurrentItemSizeYField.GetValue(compartment)) : 1f;
            var itemSizeZ = ShelfCompartmentCurrentItemSizeZField != null ? Convert.ToSingle(ShelfCompartmentCurrentItemSizeZField.GetValue(compartment)) : 1f;
            if (itemSizeY <= 0f) itemSizeY = 1f;
            if (itemSizeZ <= 0f) itemSizeZ = 1f;

            var defaultMaxX = Mathf.Max(1, Mathf.RoundToInt(sizeX / Mathf.Max(currentItemSizeX, 0.0001f)));
            var maxY = Mathf.Max(1, Mathf.RoundToInt(sizeY / Mathf.Max(itemSizeY, 0.0001f)));
            var maxZ = Mathf.Max(1, Mathf.RoundToInt(sizeZ / Mathf.Max(itemSizeZ, 0.0001f)));
            var perLayer = Mathf.Max(1, maxY * maxZ);

            var desiredX = Mathf.Max(1, Mathf.CeilToInt((float)desiredCapacity / perLayer));
            if (desiredX == defaultMaxX)
                return;

            var newItemSizeX = (float)sizeX / desiredX;
            newItemSizeX = Mathf.Clamp(newItemSizeX, 0.01f, sizeX);

            ShelfCompartmentCurrentItemSizeXField.SetValue(compartment, newItemSizeX);
            SafeAppendDebug($"Adjust shelf layout for '{data.TargetName}': desired={desiredCapacity}, defaultMaxX={defaultMaxX}, perLayer={perLayer}, newMaxX={desiredX}, sizeX={sizeX}, itemSizeX={currentItemSizeX:F3}->{newItemSizeX:F3}");
        }

        internal void RecordShelfSlotSnapshot(ShelfCompartment compartment, MeshOverrideData data, string meshName, int slotKey, int anchors)
        {
            var slotState = EnsureShelfSlotState(data, slotKey, anchors);
            if (slotState == null)
                return;

            var maxItemX = ShelfCompartmentMaxItemXField != null ? Convert.ToInt32(ShelfCompartmentMaxItemXField.GetValue(compartment)) : 0;
            var maxItemY = ShelfCompartmentMaxItemYField != null ? Convert.ToInt32(ShelfCompartmentMaxItemYField.GetValue(compartment)) : 0;
            var maxItemZ = ShelfCompartmentMaxItemZField != null ? Convert.ToInt32(ShelfCompartmentMaxItemZField.GetValue(compartment)) : 0;
            var maxItemCount = ShelfCompartmentMaxItemCountField != null ? Convert.ToInt32(ShelfCompartmentMaxItemCountField.GetValue(compartment)) : 0;

            var sizeX = ShelfCompartmentSizeXField != null ? Convert.ToInt32(ShelfCompartmentSizeXField.GetValue(compartment)) : 0;
            var sizeY = ShelfCompartmentSizeYField != null ? Convert.ToInt32(ShelfCompartmentSizeYField.GetValue(compartment)) : 0;
            var sizeZ = ShelfCompartmentSizeZField != null ? Convert.ToInt32(ShelfCompartmentSizeZField.GetValue(compartment)) : 0;

            var currentSizeX = ShelfCompartmentCurrentItemSizeXField != null ? Convert.ToSingle(ShelfCompartmentCurrentItemSizeXField.GetValue(compartment)) : 0f;
            var currentSizeY = ShelfCompartmentCurrentItemSizeYField != null ? Convert.ToSingle(ShelfCompartmentCurrentItemSizeYField.GetValue(compartment)) : 0f;
            var currentSizeZ = ShelfCompartmentCurrentItemSizeZField != null ? Convert.ToSingle(ShelfCompartmentCurrentItemSizeZField.GetValue(compartment)) : 0f;

            var posListCount = 0;
            if (ShelfCompartmentTransformPosListField != null && ShelfCompartmentTransformPosListField.GetValue(compartment) is System.Collections.ICollection transforms)
            {
                posListCount = transforms.Count;
            }

            SafeAppendDebug($"Shelf slot snapshot for '{meshName}' (slot {slotKey}): anchors={anchors}, default={slotState.DefaultCapacity}, desired={data.DesiredShelfCapacity}, max=({maxItemX}x{maxItemY}x{maxItemZ}={maxItemCount}), size=({sizeX},{sizeY},{sizeZ}), itemSize=({currentSizeX:F3},{currentSizeY:F3},{currentSizeZ:F3}), posList={posListCount}");
        }

        private void RefreshShelfSlotAllocations(MeshOverrideData data)
        {
            if (data == null || !data.ShelfAllocationsDirty)
                return;

            data.ShelfAllocationsDirty = false;

            var desiredPerSlot = data.DesiredShelfCapacity;
            foreach (var slot in data.ShelfSlots.Values)
            {
                if (slot != null)
                    slot.DesiredCapacity = desiredPerSlot;
            }
        }

        internal bool TryGetShelfOverride(ShelfCompartment compartment, out MeshOverrideData data, out string meshName)
        {
            data = null;
            meshName = GetShelfMeshName(compartment);
            if (string.IsNullOrEmpty(meshName))
                return false;

            return _meshOverridesByTarget.TryGetValue(meshName, out data) && data != null;
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
                _meshFilterOriginalMeshNames.Remove(id);
                _meshFilterMissingOriginalLogged.Remove(id);
                _meshFilterOverrideIds.Remove(id);
                _meshInstanceTargetByComponent.Remove(id);
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
                _skinnedOriginalMeshNames.Remove(id);
                _skinnedMissingOriginalLogged.Remove(id);
                _skinnedRendererOverrideIds.Remove(id);
                _meshInstanceTargetByComponent.Remove(id);
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
                _meshColliderOriginalMeshNames.Remove(id);
                _meshColliderMissingOriginalLogged.Remove(id);
                _meshColliderOverrideIds.Remove(id);
                _meshInstanceTargetByComponent.Remove(id);
            }
        }

        private void RememberMeshFilterOriginalName(int id, Mesh mesh, MeshFilter owner)
        {
            var name = mesh != null ? mesh.name : owner?.sharedMesh?.name;
            if (!string.IsNullOrEmpty(name))
            {
                _meshFilterOriginalMeshNames[id] = name;
                if (mesh != null)
                    _meshFilterMissingOriginalLogged.Remove(id);
            }
        }

        private void RememberSkinnedOriginalName(int id, Mesh mesh, SkinnedMeshRenderer owner)
        {
            var name = mesh != null ? mesh.name : owner?.sharedMesh?.name;
            if (!string.IsNullOrEmpty(name))
            {
                _skinnedOriginalMeshNames[id] = name;
                if (mesh != null)
                    _skinnedMissingOriginalLogged.Remove(id);
            }
        }

        private void RememberColliderOriginalName(int id, Mesh mesh, MeshCollider owner)
        {
            var name = mesh != null ? mesh.name : owner?.sharedMesh?.name;
            if (!string.IsNullOrEmpty(name))
            {
                _meshColliderOriginalMeshNames[id] = name;
                if (mesh != null)
                    _meshColliderMissingOriginalLogged.Remove(id);
            }
        }

        private string ResolveMeshFilterTargetName(int id, MeshFilter filter, Mesh originalMesh)
        {
            if (originalMesh != null && !string.IsNullOrEmpty(originalMesh.name))
            {
                _meshFilterOriginalMeshNames[id] = originalMesh.name;
                _meshFilterMissingOriginalLogged.Remove(id);
                return originalMesh.name;
            }

            if (_meshFilterOriginalMeshNames.TryGetValue(id, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;

            var current = filter.sharedMesh?.name;
            if (!string.IsNullOrEmpty(current))
            {
                _meshFilterOriginalMeshNames[id] = current;
                return current;
            }

            return null;
        }

        private string ResolveSkinnedTargetName(int id, SkinnedMeshRenderer renderer, Mesh originalMesh)
        {
            if (originalMesh != null && !string.IsNullOrEmpty(originalMesh.name))
            {
                _skinnedOriginalMeshNames[id] = originalMesh.name;
                _skinnedMissingOriginalLogged.Remove(id);
                return originalMesh.name;
            }

            if (_skinnedOriginalMeshNames.TryGetValue(id, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;

            var current = renderer.sharedMesh?.name;
            if (!string.IsNullOrEmpty(current))
            {
                _skinnedOriginalMeshNames[id] = current;
                return current;
            }

            return null;
        }

        private string ResolveMeshColliderTargetName(int id, MeshCollider collider, Mesh originalMesh)
        {
            if (originalMesh != null && !string.IsNullOrEmpty(originalMesh.name))
            {
                _meshColliderOriginalMeshNames[id] = originalMesh.name;
                _meshColliderMissingOriginalLogged.Remove(id);
                return originalMesh.name;
            }

            if (_meshColliderOriginalMeshNames.TryGetValue(id, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;

            var current = collider.sharedMesh?.name;
            if (!string.IsNullOrEmpty(current))
            {
                _meshColliderOriginalMeshNames[id] = current;
                return current;
            }

            return null;
        }

        private void LogMissingOriginal(HashSet<int> cache, int id, string category, string name)
        {
            if (!cache.Add(id))
                return;

            var display = string.IsNullOrEmpty(name) ? "<unknown>" : name;
            SafeAppendDebug($"Missing original {category} mesh reference for '{display}' (component {id}); relying on cached name.");
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

        private bool ApplyRendererMaterialOverrides(Renderer renderer, MeshOverrideData data)
        {
            if (renderer == null || data == null || data.MaterialOverrides.Count == 0)
                return false;

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

            return changed;
        }

        private bool RestoreRendererMaterials(Renderer renderer)
        {
            if (renderer == null)
                return false;

            var id = renderer.GetInstanceID();
            if (_rendererOverrideIds.Remove(id))
            {
                if (_rendererOriginalMaterials.TryGetValue(id, out var originals) && originals != null)
                {
                    renderer.sharedMaterials = originals;
                    return true;
                }
            }

            return false;
        }

        internal void HandleTextLabelUpdate(TMP_Text text)
        {
            if (text == null || !HasAnyCardOverrides() || _applyingLabelOverride)
                return;

            string label;
            try
            {
                label = text.text;
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(label))
                return;

            var entry = FindCardOverride(null, label, out var matchSource);
            if (entry == null)
                return;

            var allowGlobalOverride = entry.IsMeshOverride || matchSource == CardOverrideMatchSource.CardAlias;
            if (!allowGlobalOverride)
                return;

            var id = text.GetInstanceID();
            var targetLabel = entry.DisplayName ?? string.Empty;

            if (_lastAppliedMeshLabels.TryGetValue(id, out var lastApplied) &&
                string.Equals(lastApplied, targetLabel, StringComparison.Ordinal) &&
                string.Equals(label, targetLabel, StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(label, targetLabel, StringComparison.Ordinal))
            {
                CacheMeshLabelReference(text, id, targetLabel);
                return;
            }

            _applyingLabelOverride = true;
            try
            {
                ReplaceNameLabel(text, entry, label);
                CacheMeshLabelReference(text, id, targetLabel);

                if (_enableDebugLog != null && _enableDebugLog.Value)
                {
                    var path = GetTransformPath(text.transform, MeshLabelSearchParentDepth + 4);
                    SafeAppendDebug($"Global label override applied: '{label}' -> '{targetLabel}' @ {path}");
                }
            }
            finally
            {
                _applyingLabelOverride = false;
            }
        }

        private void CacheMeshLabelReference(TMP_Text text, int id, string targetLabel)
        {
            _lastAppliedMeshLabels[id] = targetLabel;
            if (!_meshLabelTextRefs.TryGetValue(id, out var weak) || !weak.TryGetTarget(out var existing) || !ReferenceEquals(existing, text))
            {
                _meshLabelTextRefs[id] = new WeakReference<TMP_Text>(text);
            }

            if (--_meshLabelCleanupBudget <= 0)
            {
                CleanupMeshLabelCache();
            }
        }

        private void CleanupMeshLabelCache()
        {
            _meshLabelCleanupScratch.Clear();
            foreach (var pair in _meshLabelTextRefs)
            {
                if (!pair.Value.TryGetTarget(out var target) || target == null)
                {
                    _meshLabelCleanupScratch.Add(pair.Key);
                }
            }

            if (_meshLabelCleanupScratch.Count > 0)
            {
                foreach (var id in _meshLabelCleanupScratch)
                {
                    _meshLabelTextRefs.Remove(id);
                    _lastAppliedMeshLabels.Remove(id);
                }
            }

            _meshLabelCleanupBudget = Math.Max(_meshLabelTextRefs.Count / 2, 128);
        }

        private void CollectCandidateTextures(List<Texture2D> destination)
        {
            destination.Clear();
            _collectionScratch.Clear();
            _textureNameCounts.Clear();
            _ambiguousTextureNames.Clear();

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
                {
                    destination.Add(tex);
                    var name = tex.name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        if (_textureNameCounts.TryGetValue(name, out var count))
                        {
                            count++;
                            _textureNameCounts[name] = count;
                            if (count == 2)
                            {
                                _ambiguousTextureNames.Add(name);
                            }
                        }
                        else
                        {
                            _textureNameCounts[name] = 1;
                        }
                    }
                }
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

            if (_ambiguousTextureNames.Count > 0)
            {
                var toRemove = new List<string>();
                foreach (var pair in _textureOverridesByName)
                {
                    if (_ambiguousTextureNames.Contains(pair.Key))
                    {
                        toRemove.Add(pair.Key);
                    }
                }

                foreach (var name in toRemove)
                {
                    _textureOverridesByName.Remove(name);
                }

                _ambiguousTextureLog.RemoveWhere(name => !_ambiguousTextureNames.Contains(name));

                foreach (var name in _ambiguousTextureNames)
                {
                    if (_ambiguousTextureLog.Add(name))
                    {
                        _textureNameCounts.TryGetValue(name, out var count);
                        var message = $"[CustomTextureReplacer] Multiple live textures share the name '{name}' ({count} instances). Skipping automatic PNG override for this name to avoid cross-bleeding between objects.";
                        _logger.LogWarning(message);
                        SafeAppendDebug($"Ambiguous texture name '{name}' detected ({count} instances); ignoring global override.");
                    }
                }
            }
            else if (_ambiguousTextureLog.Count > 0)
            {
                _ambiguousTextureLog.Clear();
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
            ScheduleMeshOverrideReapply(0.02f);
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

        internal sealed class MeshOverrideData
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
            public int DesiredInstanceCount = -1;
            public int DesiredShelfCapacity = -1;
            public bool ShelfAllocationsDirty = true;
            public HashSet<int> InstanceIds { get; } = new HashSet<int>();
            public List<WeakReference<GameObject>> InstanceReferences { get; } = new List<WeakReference<GameObject>>();
            public HashSet<int> HiddenInstanceIds { get; } = new HashSet<int>();
            public Dictionary<int, ShelfSlotState> ShelfSlots { get; } = new Dictionary<int, ShelfSlotState>();
            public CardTextOverride CardOverride;
            public bool AutoAdjustBoxColliders;
            public BoxColliderSearchScope BoxColliderScope = BoxColliderSearchScope.Self;
        }

        internal sealed class ShelfSlotState
        {
            public int DefaultCapacity = -1;
            public int DesiredCapacity = -1;
            public int LastAnchorCount = -1;
            public int LastItemCount = -1;
        }

        internal sealed class MaterialOverrideData
        {
            public int Slot;
            public string MaterialAssetName;
            public string SourceBundlePath;
            public Material MaterialInstance;
            public List<MaterialTextureAssignment> TextureAssignments { get; } = new List<MaterialTextureAssignment>();
        }

        internal sealed class MaterialTextureAssignment
        {
            public string PropertyName;
            public string FilePath;
            public Texture Texture;
        }

        internal sealed class PersistentSpriteOverride
        {
            public string SpriteName;
            public string TargetTextureName;
            public RectInt Region;
            public int Width;
            public int Height;
            public Texture2D Replacement;
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
                ReapplyPersistentSpriteOverrides(tex);
                RequestReapply(context);
            }
            else if (asset is Sprite sprite)
            {
                if (_logAssetLoads.Value)
                {
                    _logger.LogInfo($"[CustomTextureReplacer] {context}: Sprite '{sprite.name}' -> texture '{sprite.texture?.name}' {detail}");
                }

                SafeAppendDebug($"Asset load ({context}) sprite {sprite.name}");
                if (sprite.texture is Texture2D spriteTex)
                {
                    ReapplyPersistentSpriteOverrides(spriteTex);
                }
                ReapplyPersistentSpriteToSprite(sprite);
                RequestReapply(context);
            }
            else if (asset is Mesh mesh)
            {
                if (_logAssetLoads.Value)
                {
                    _logger.LogInfo($"[CustomTextureReplacer] {context}: Mesh '{mesh.name}' {detail}");
                }

                SafeAppendDebug($"Asset load ({context}) mesh {mesh.name}");
                ScheduleMeshOverrideReapply(0f);
                RequestReapply(context);
            }
            else if (asset is Material material)
            {
                if (_logAssetLoads.Value)
                {
                    _logger.LogInfo($"[CustomTextureReplacer] {context}: Material '{material.name}' {detail}");
                }

                SafeAppendDebug($"Asset load ({context}) material {material.name}");
                ScheduleMeshOverrideReapply(0f);
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

            if (_enableDebugLog != null && !_enableDebugLog.Value)
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

        internal bool ShouldSuppressLog(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;

            if (_suppressRectTransformWarning != null && _suppressRectTransformWarning.Value &&
                message.IndexOf("Parent of RectTransform is being set with parent property", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private bool HasAnyCardOverrides()
        {
            return _cardOverrides.Count > 0 || _meshCardOverrides.Count > 0;
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
            _cardOverrideAliasKeys.Add(trimmed);
            if (!string.IsNullOrEmpty(source))
            {
                SafeAppendDebug($"CardOverrides alias '{trimmed}' mapped to '{entry.Id}' via {source}.");
            }
            else
            {
                SafeAppendDebug($"CardOverrides alias '{trimmed}' mapped to '{entry.Id}'.");
            }
        }

        private void RegisterMeshCardOverride(CardTextOverride entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id))
                return;

            entry.IsMeshOverride = true;

            void AddAlias(string alias, bool trackAlias = true)
            {
                if (string.IsNullOrWhiteSpace(alias))
                    return;

                var trimmed = alias.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    return;

                _meshCardOverrides[trimmed] = entry;
                if (trackAlias)
                {
                    _meshCardOverrideAliasKeys.Add(trimmed);
                }
            }

            AddAlias(entry.Id, trackAlias: false);
            AddAlias(entry.DisplayName);

            if (entry.Aliases != null)
            {
                foreach (var alias in entry.Aliases)
                {
                    AddAlias(alias);
                }
            }
        }

        private void RegisterPersistentSpriteOverride(Sprite originalSprite, Texture2D replacement, int width, int height)
        {
            if (originalSprite == null || replacement == null)
                return;

            var targetTexture = originalSprite.texture;
            if (targetTexture == null)
                return;

            var rect = originalSprite.textureRect;
            var region = new RectInt(
                Mathf.RoundToInt(rect.x),
                Mathf.RoundToInt(rect.y),
                Mathf.RoundToInt(rect.width),
                Mathf.RoundToInt(rect.height));

            if (region.width <= 0 || region.height <= 0)
                return;

            var record = new PersistentSpriteOverride
            {
                SpriteName = originalSprite.name,
                TargetTextureName = targetTexture.name ?? string.Empty,
                Region = region,
                Width = width,
                Height = height,
                Replacement = replacement
            };

            _spritePersistence[record.SpriteName] = record;

            if (!string.IsNullOrEmpty(record.TargetTextureName))
            {
                if (!_spritePersistenceByTexture.TryGetValue(record.TargetTextureName, out var list))
                {
                    list = new List<PersistentSpriteOverride>();
                    _spritePersistenceByTexture[record.TargetTextureName] = list;
                }

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(list[i].SpriteName, record.SpriteName, StringComparison.OrdinalIgnoreCase))
                    {
                        list.RemoveAt(i);
                        break;
                    }
                }

                list.Add(record);
            }

            SafeAppendDebug($"Sprite persistence registered for '{record.SpriteName}' on texture '{record.TargetTextureName}' ({region.width}x{region.height} at {region.x},{region.y}).");
        }

        private void OnSpriteAtlasRegistered(SpriteAtlas atlas)
        {
            if (atlas == null)
                return;

            SafeAppendDebug($"Sprite atlas registered: {atlas.name}");
            ReapplyPersistentSpriteOverridesForAtlas(atlas);
            RequestReapply("SpriteAtlasRegistered");
            ScheduleSpriteAtlasRetry(atlas, MeshOverrideRescanIntervalApplied);
        }

        private void ReapplyExistingSpriteAtlases(string reason)
        {
            var foundAny = false;

            try
            {
                foreach (var atlas in Resources.FindObjectsOfTypeAll<SpriteAtlas>())
                {
                    if (atlas == null)
                        continue;

                    foundAny = true;
                    SafeAppendDebug($"Sprite atlas available: {atlas.name}");
                    ReapplyPersistentSpriteOverridesForAtlas(atlas);
                    ScheduleSpriteAtlasRetry(atlas, MeshOverrideRescanIntervalApplied);
                }
            }
            catch (Exception ex)
            {
                SafeAppendDebug($"Existing sprite atlas enumeration failed: {ex.Message}");
            }

            if (foundAny)
            {
                RequestReapply(reason);
            }
        }

        private void ReapplyPersistentSpriteOverridesForAtlas(SpriteAtlas atlas)
        {
            if (atlas == null)
                return;

            try
            {
                var spriteCount = atlas.spriteCount;
                if (spriteCount <= 0)
                    return;

                if (_spriteArray.Length < spriteCount)
                    Array.Resize(ref _spriteArray, spriteCount);

                var received = atlas.GetSprites(_spriteArray);
                for (var i = 0; i < received; i++)
                {
                    var sprite = _spriteArray[i];
                    if (sprite == null)
                        continue;

                    if (sprite.texture is Texture2D texture)
                    {
                        ReapplyPersistentSpriteOverrides(texture);
                    }

                    ReapplyPersistentSpriteToSprite(sprite);
                }
            }
            catch (Exception ex)
            {
                SafeAppendDebug($"Sprite atlas reapply failed ({atlas?.name}): {ex.Message}");
            }
        }

        private void ScheduleSpriteAtlasRetry(SpriteAtlas atlas, float initialDelay)
        {
            var identifier = atlas?.name;
            if (string.IsNullOrEmpty(identifier))
                return;

            if (!_pendingSpriteAtlasReapply.Add(identifier))
                return;

            StartCoroutine(ReapplySpriteAtlasDeferred(atlas, identifier, initialDelay));
        }

        private IEnumerator ReapplySpriteAtlasDeferred(SpriteAtlas atlas, string identifier, float initialDelay)
        {
            if (initialDelay > 0f)
                yield return new WaitForSecondsRealtime(initialDelay);

            const int attempts = 6;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (atlas == null)
                    break;

                ReapplyPersistentSpriteOverridesForAtlas(atlas);
                RequestReapply($"SpriteAtlasRetry{attempt}");

                yield return null;
                yield return new WaitForSecondsRealtime(0.1f);
            }

            _pendingSpriteAtlasReapply.Remove(identifier);
        }

        private void ReapplyPersistentSpriteOverrides(Texture2D target)
        {
            if (target == null)
                return;

            if (!_spritePersistenceByTexture.TryGetValue(target.name, out var records) || records == null || records.Count == 0)
                return;

            foreach (var record in records)
            {
                var replacement = record.Replacement;
                if (replacement == null)
                    continue;

                var region = record.Region;
                var width = Mathf.Min(record.Width, replacement.width);
                var height = Mathf.Min(record.Height, replacement.height);

                if (width <= 0 || height <= 0)
                    continue;

                if (TryBlitToTexture(replacement, target, width, height, out var message, out _, region.x, region.y))
                {
                    SafeAppendDebug($"Persistent sprite override '{record.SpriteName}' applied to texture '{target.name}' ({width}x{height} at {region.x},{region.y}).");
                    if (!string.IsNullOrEmpty(message))
                        _logger.LogWarning(message);
                }
                else
                {
                    SafeAppendDebug($"Persistent sprite override '{record.SpriteName}' failed for texture '{target.name}'.");
                }
            }

            try
            {
                foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sprite == null)
                        continue;

                    var texture = sprite.texture;
                    if (texture == null)
                        continue;

                    if (!string.Equals(texture.name, target.name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    ReapplyPersistentSpriteToSprite(sprite);
                }
            }
            catch (Exception ex)
            {
                SafeAppendDebug($"ReapplyPersistentSpriteOverrides enumeration failed: {ex.Message}");
            }
        }

        private void ReapplyPersistentSpriteToSprite(Sprite sprite)
        {
            if (sprite == null)
                return;

            if (_spritePersistence.TryGetValue(sprite.name, out var record) && record.Replacement != null)
            {
                if (TryCreateSpriteOverride(sprite, record.Replacement, record.Width, record.Height))
                {
                    SafeAppendDebug($"Persistent sprite override '{record.SpriteName}' re-applied to sprite instance.");
                }
            }
        }

        private void LoadCardOverrides(bool logDetails)
        {
            _cardOverrides.Clear();
            _cardOverrideAliasKeys.Clear();

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

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> dict, string key)
        {
            if (dict == null || string.IsNullOrEmpty(key))
                return null;

            if (!dict.TryGetValue(key, out var value) || value == null)
                return null;

            return value as Dictionary<string, object>;
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

        private static BoxColliderSearchScope ParseBoxColliderScope(string value)
        {
            if (string.IsNullOrEmpty(value))
                return BoxColliderSearchScope.Self;

            switch (value.Trim().ToLowerInvariant())
            {
                case "children":
                case "selfandchildren":
                case "self_children":
                case "self+children":
                case "selfchildren":
                    return BoxColliderSearchScope.SelfAndChildren;
                case "parent":
                case "parents":
                case "selfandparents":
                case "selfparents":
                    return BoxColliderSearchScope.Parents;
                default:
                    return BoxColliderSearchScope.Self;
            }
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

            string resolved = null;
            try
            {
                DateTime SafeGetLastWriteTimeUtc(string path)
                {
                    try
                    {
                        return File.GetLastWriteTimeUtc(path);
                    }
                    catch
                    {
                        return DateTime.MinValue;
                    }
                }

                var existing = candidates.Where(File.Exists).ToList();
                if (existing.Count > 0)
                {
                    resolved = existing
                        .OrderByDescending(SafeGetLastWriteTimeUtc)
                        .ThenBy(path => existing.IndexOf(path))
                        .FirstOrDefault();
                }
            }
            catch
            {
                resolved = null;
            }

            resolved ??= candidates.FirstOrDefault(File.Exists)
                        ?? candidates.FirstOrDefault()
                        ?? Path.Combine(Paths.PluginPath, "CardOverrides.json");

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
            if (!HasAnyCardOverrides())
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

        private void ReapplyStoreLabels()
        {
            if (!HasAnyCardOverrides())
                return;

            try
            {
                foreach (var itemBar in Resources.FindObjectsOfTypeAll<UI_CheckoutItemBar>())
                {
                    if (itemBar == null)
                        continue;

                    string label = null;
                    try
                    {
                        if (CheckoutItemBarBindings.NameTextField?.GetValue(itemBar) is TMP_Text nameText && nameText != null)
                            label = nameText.text;
                    }
                    catch { }

                    AdjustCheckoutItemName(itemBar, label);
                }

                foreach (var panel in Resources.FindObjectsOfTypeAll<CheckPricePanelUI>())
                {
                    ApplyCheckPricePanelOverrides(panel);
                }

                foreach (var screen in Resources.FindObjectsOfTypeAll<ItemPriceGraphScreen>())
                {
                    ApplyItemPriceGraphOverrides(screen);
                }

                ReapplyMeshInstanceNameOverrides();
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[CustomTextureReplacer] ReapplyStoreLabels failed: {ex.Message}");
                SafeAppendDebug($"ReapplyStoreLabels exception: {ex.Message}");
            }
        }

        internal void ApplyCardOverrides(CardUI cardUI)
        {
            if (cardUI == null || !HasAnyCardOverrides())
                return;

            try
            {
                var entry = FindCardOverride(cardUI, null, out _);
                if (entry == null)
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
            if (panel == null || !HasAnyCardOverrides())
                return;

            try
            {
                if (CheckPricePanelUIBindings.NameTextField?.GetValue(panel) is not TMP_Text nameText)
                    return;

                var originalLabel = nameText.text;
                var cardUI = CheckPricePanelUIBindings.CardUIField?.GetValue(panel) as CardUI;
                var entry = FindCardOverride(cardUI, originalLabel, out _);
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
            if (itemBar == null || !HasAnyCardOverrides())
                return;

            try
            {
                if (string.IsNullOrEmpty(originalLabel))
                    return;

                if (CheckoutItemBarBindings.NameTextField?.GetValue(itemBar) is not TMP_Text nameText)
                    return;

                var entry = FindCardOverride(null, originalLabel, out _);
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
            if (screen == null || !HasAnyCardOverrides())
                return;

            try
            {
                if (ItemPriceGraphScreenBindings.CardNameTextField?.GetValue(screen) is not TMP_Text nameText)
                    return;

                var originalLabel = nameText.text;
                var cardUI = ItemPriceGraphScreenBindings.CardUIField?.GetValue(screen) as CardUI;
                var entry = FindCardOverride(cardUI, originalLabel, out _);
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

        private CardTextOverride FindCardOverride(CardUI cardUI, string label, out CardOverrideMatchSource matchSource)
        {
            matchSource = CardOverrideMatchSource.None;
            if (!HasAnyCardOverrides())
                return null;

            // Prefer explicit card overrides over metadata embedded in mesh overrides.
            if (cardUI != null)
            {
                var key = ResolveMonsterKey(cardUI);
                if (!string.IsNullOrEmpty(key))
                {
                    if (_cardOverrides.TryGetValue(key, out var entry) && entry != null)
                    {
                        matchSource = _cardOverrideAliasKeys.Contains(key) ? CardOverrideMatchSource.CardAlias : CardOverrideMatchSource.CardId;
                        return entry;
                    }

                    if (_meshCardOverrides.TryGetValue(key, out var meshEntry) && meshEntry != null)
                    {
                        matchSource = _meshCardOverrideAliasKeys.Contains(key) ? CardOverrideMatchSource.MeshAlias : CardOverrideMatchSource.MeshId;
                        return meshEntry;
                    }
                }
            }

            if (!string.IsNullOrEmpty(label))
            {
                var trimmed = label.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    if (_cardOverrides.TryGetValue(trimmed, out var direct) && direct != null)
                    {
                        matchSource = _cardOverrideAliasKeys.Contains(trimmed) ? CardOverrideMatchSource.CardAlias : CardOverrideMatchSource.CardId;
                        return direct;
                    }

                    if (_meshCardOverrides.TryGetValue(trimmed, out var meshByLabel) && meshByLabel != null)
                    {
                        matchSource = _meshCardOverrideAliasKeys.Contains(trimmed) ? CardOverrideMatchSource.MeshAlias : CardOverrideMatchSource.MeshId;
                        return meshByLabel;
                    }
                }

                var prefix = GetNamePrefix(label);
                if (!string.IsNullOrEmpty(prefix))
                {
                    if (_cardOverrides.TryGetValue(prefix, out var fromLabel) && fromLabel != null)
                    {
                        matchSource = _cardOverrideAliasKeys.Contains(prefix) ? CardOverrideMatchSource.CardAlias : CardOverrideMatchSource.CardId;
                        return fromLabel;
                    }

                    if (_meshCardOverrides.TryGetValue(prefix, out var meshFromLabel) && meshFromLabel != null)
                    {
                        matchSource = _meshCardOverrideAliasKeys.Contains(prefix) ? CardOverrideMatchSource.MeshAlias : CardOverrideMatchSource.MeshId;
                        return meshFromLabel;
                    }
                }
            }

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

        private static string GetNameSuffix(string label)
        {
            if (string.IsNullOrEmpty(label))
                return string.Empty;

            var separatorIndex = label.IndexOf(" - ", StringComparison.Ordinal);
            return separatorIndex >= 0 ? label.Substring(separatorIndex) : string.Empty;
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

        internal class CardTextOverride
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
            public bool IsMeshOverride;
        }

        private enum CardOverrideMatchSource
        {
            None,
            CardId,
            CardAlias,
            MeshId,
            MeshAlias
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

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Resources), nameof(Resources.LoadAsync), new[] { typeof(string), typeof(Type) })]
        private static void ResourcesLoadAsync(string path, Type systemTypeInstance, ResourceRequest __result)
        {
            if (__result == null)
                return;

            void OnCompleted(AsyncOperation op)
            {
                __result.completed -= OnCompleted;
                var asset = __result.asset;
                if (asset != null)
                {
                    var detail = $"path '{path}', type '{systemTypeInstance?.Name}'";
                    ReplacerController.Instance?.HandleAssetLoad(asset, "Resources.LoadAsync", detail);
                }
            }

            __result.completed += OnCompleted;
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

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAssetAsync), new[] { typeof(string), typeof(Type) })]
        private static void LoadAssetAsync(AssetBundle __instance, string name, Type type, AssetBundleRequest __result)
        {
            if (__result == null)
                return;

            void OnCompleted(AsyncOperation op)
            {
                __result.completed -= OnCompleted;
                var asset = __result.asset;
                if (asset != null)
                {
                    var detail = $"name '{name}', type '{type?.Name}' from bundle '{__instance?.name}'";
                    ReplacerController.Instance?.HandleAssetLoad(asset, "AssetBundle.LoadAssetAsync", detail);
                }
            }

            __result.completed += OnCompleted;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAllAssetsAsync), new[] { typeof(Type) })]
        private static void LoadAllAssetsAsync(AssetBundle __instance, Type type, AssetBundleRequest __result)
        {
            if (__result == null)
                return;

            void OnCompleted(AsyncOperation op)
            {
                __result.completed -= OnCompleted;
                var assets = __result.allAssets;
                if (assets != null && assets.Length > 0)
                {
                    ReplacerController.Instance?.HandleAssetLoad(assets, $"AssetBundle.LoadAllAssetsAsync({type?.Name})");
                }
            }

            __result.completed += OnCompleted;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAssetWithSubAssetsAsync), new[] { typeof(string), typeof(Type) })]
        private static void LoadAssetWithSubAssetsAsync(AssetBundle __instance, string name, Type type, AssetBundleRequest __result)
        {
            if (__result == null)
                return;

            void OnCompleted(AsyncOperation op)
            {
                __result.completed -= OnCompleted;
                var assets = __result.allAssets;
                if (assets != null && assets.Length > 0)
                {
                    ReplacerController.Instance?.HandleAssetLoad(assets, $"AssetBundle.LoadAssetWithSubAssetsAsync({name},{type?.Name})");
                }
                else
                {
                    var asset = __result.asset;
                    if (asset != null)
                    {
                        var detail = $"name '{name}', type '{type?.Name}' from bundle '{__instance?.name}'";
                        ReplacerController.Instance?.HandleAssetLoad(asset, "AssetBundle.LoadAssetWithSubAssetsAsync", detail);
                    }
                }
            }

            __result.completed += OnCompleted;
        }
    }

    [HarmonyPatch(typeof(SpriteRenderer))]
    internal static class SpriteRendererPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("set_sprite")]
        private static void SetSpritePostfix(SpriteRenderer __instance)
        {
            if (__instance == null)
                return;

            var replacement = ReplacerController.Instance?.GetReplacementSprite(__instance.sprite);
            if (replacement != null && !ReferenceEquals(__instance.sprite, replacement))
            {
                __instance.sprite = replacement;
            }
        }
    }

    [HarmonyPatch(typeof(Image))]
    internal static class UIImagePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("set_sprite")]
        private static void OnSpriteSet(Image __instance, Sprite value)
        {
            ReplacerController.Instance?.ApplySpriteOverrideToImage(__instance, value);
        }

        [HarmonyPostfix]
        [HarmonyPatch("set_overrideSprite")]
        private static void OnOverrideSpriteSet(Image __instance, Sprite value)
        {
            ReplacerController.Instance?.ApplySpriteOverrideToImage(__instance, value);
        }
    }

    [HarmonyPatch(typeof(RawImage))]
    internal static class UIRawImagePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("set_texture")]
        private static void OnTextureSet(RawImage __instance, Texture value)
        {
            ReplacerController.Instance?.ApplyTextureOverrideToRawImage(__instance, value);
        }
    }
    [HarmonyPatch(typeof(ShelfCompartment))]
    internal static class ShelfCompartmentCalculatePositionListPatch
    {
        private struct SizeState
        {
            public bool HasOverride;
            public float OriginalItemSizeX;
            public float OriginalItemSizeY;
            public float OriginalItemSizeZ;
        }

        [HarmonyPrefix]
        [HarmonyPatch("CalculatePositionList")]
        private static void CalculatePositionListPrefix(ShelfCompartment __instance, ref SizeState __state)
        {
            __state = default;

            var controller = ReplacerController.Instance;
            if (controller == null)
                return;

            if (!controller.TryGetShelfOverride(__instance, out var data, out _))
                return;

            if (data == null || data.DesiredShelfCapacity < 0)
                return;

            __state.HasOverride = true;
            __state.OriginalItemSizeX = float.NaN;
            __state.OriginalItemSizeY = float.NaN;
            __state.OriginalItemSizeZ = float.NaN;

            if (ReplacerController.ShelfCompartmentCurrentItemSizeXField != null)
            {
                var value = ReplacerController.ShelfCompartmentCurrentItemSizeXField.GetValue(__instance);
                if (value is float f)
                    __state.OriginalItemSizeX = f;
            }

            if (ReplacerController.ShelfCompartmentCurrentItemSizeYField != null)
            {
                var value = ReplacerController.ShelfCompartmentCurrentItemSizeYField.GetValue(__instance);
                if (value is float f)
                    __state.OriginalItemSizeY = f;
            }

            if (ReplacerController.ShelfCompartmentCurrentItemSizeZField != null)
            {
                var value = ReplacerController.ShelfCompartmentCurrentItemSizeZField.GetValue(__instance);
                if (value is float f)
                    __state.OriginalItemSizeZ = f;
            }

            controller.ApplyShelfLayoutScaling(__instance, data, data.DesiredShelfCapacity);
        }

        [HarmonyPostfix]
        [HarmonyPatch("CalculatePositionList")]
        private static void CalculatePositionListPostfix(ShelfCompartment __instance, SizeState __state)
        {
            if (!__state.HasOverride)
                return;

            var controller = ReplacerController.Instance;
            if (controller == null)
            {
                RestoreOriginalShelfItemSizes(__instance, __state);
                return;
            }

            if (!controller.TryGetShelfOverride(__instance, out var data, out var meshName))
            {
                RestoreOriginalShelfItemSizes(__instance, __state);
                return;
            }

            var anchors = controller.GetShelfAnchorCount(__instance);
            var slotKey = controller.GetShelfSlotKey(__instance);
            controller.RecordShelfSlotSnapshot(__instance, data, meshName, slotKey, anchors);

            RestoreOriginalShelfItemSizes(__instance, __state);
        }

        private static void RestoreOriginalShelfItemSizes(ShelfCompartment compartment, SizeState state)
        {
            if (!state.HasOverride || compartment == null)
                return;

            if (!float.IsNaN(state.OriginalItemSizeX) && ReplacerController.ShelfCompartmentCurrentItemSizeXField != null)
            {
                ReplacerController.ShelfCompartmentCurrentItemSizeXField.SetValue(compartment, state.OriginalItemSizeX);
            }

            if (!float.IsNaN(state.OriginalItemSizeY) && ReplacerController.ShelfCompartmentCurrentItemSizeYField != null)
            {
                ReplacerController.ShelfCompartmentCurrentItemSizeYField.SetValue(compartment, state.OriginalItemSizeY);
            }

            if (!float.IsNaN(state.OriginalItemSizeZ) && ReplacerController.ShelfCompartmentCurrentItemSizeZField != null)
            {
                ReplacerController.ShelfCompartmentCurrentItemSizeZField.SetValue(compartment, state.OriginalItemSizeZ);
            }
        }
    }

    [HarmonyPatch(typeof(ShelfCompartment))]
    internal static class ShelfCompartmentPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("SpawnItem")]
        private static void SpawnItemPrefix(ShelfCompartment __instance, ref int amount)
        {
            ReplacerController.Instance?.AdjustShelfSpawn(__instance, ref amount);
        }
    }
    [HarmonyPatch(typeof(MeshFilter))]
    internal static class MeshFilterPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("set_sharedMesh")]
        private static void SharedMeshPostfix(MeshFilter __instance)
        {
            ReplacerController.Instance?.RequestMeshOverrideRescan(0f);
        }
    }

    [HarmonyPatch(typeof(SkinnedMeshRenderer))]
    internal static class SkinnedMeshRendererPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("set_sharedMesh")]
        private static void SharedMeshPostfix(SkinnedMeshRenderer __instance)
        {
            ReplacerController.Instance?.RequestMeshOverrideRescan(0f);
        }
    }

    [HarmonyPatch(typeof(MeshCollider))]
    internal static class MeshColliderPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("set_sharedMesh")]
        private static void SharedMeshPostfix(MeshCollider __instance)
        {
            ReplacerController.Instance?.RequestMeshOverrideRescan(0f);
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Object))]
    internal static class ObjectInstantiatePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(UnityEngine.Object.Instantiate), new[] { typeof(UnityEngine.Object) })]
        private static void InstantiateObject(UnityEngine.Object original, UnityEngine.Object __result)
        {
            HandleInstantiateResult(__result);
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(UnityEngine.Object.Instantiate), new[] { typeof(UnityEngine.Object), typeof(UnityEngine.Transform) })]
        private static void InstantiateWithParent(UnityEngine.Object original, UnityEngine.Transform parent, UnityEngine.Object __result)
        {
            HandleInstantiateResult(__result);
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(UnityEngine.Object.Instantiate), new[] { typeof(UnityEngine.Object), typeof(UnityEngine.Transform), typeof(bool) })]
        private static void InstantiateWithParentSpace(UnityEngine.Object original, UnityEngine.Transform parent, bool instantiateInWorldSpace, UnityEngine.Object __result)
        {
            HandleInstantiateResult(__result);
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(UnityEngine.Object.Instantiate), new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion) })]
        private static void InstantiateAtPosition(UnityEngine.Object original, Vector3 position, Quaternion rotation, UnityEngine.Object __result)
        {
            HandleInstantiateResult(__result);
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(UnityEngine.Object.Instantiate), new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion), typeof(UnityEngine.Transform) })]
        private static void InstantiateAtPositionWithParent(UnityEngine.Object original, Vector3 position, Quaternion rotation, UnityEngine.Transform parent, UnityEngine.Object __result)
        {
            HandleInstantiateResult(__result);
        }

        private static void HandleInstantiateResult(UnityEngine.Object instance)
        {
            if (instance == null)
                return;

            if (instance is MeshFilter || instance is SkinnedMeshRenderer || instance is MeshCollider)
            {
                ReplacerController.Instance?.RequestMeshOverrideRescan(0f);
                return;
            }

            if (instance is Component component)
            {
                if (ContainsTargetMeshes(component.gameObject))
                    ReplacerController.Instance?.RequestMeshOverrideRescan(0f);
                return;
            }

            if (instance is GameObject go && ContainsTargetMeshes(go))
            {
                ReplacerController.Instance?.RequestMeshOverrideRescan(0f);
            }
        }

        private static bool ContainsTargetMeshes(GameObject go)
        {
            if (go == null)
                return false;

            if (go.GetComponent<MeshFilter>() != null ||
                go.GetComponent<SkinnedMeshRenderer>() != null ||
                go.GetComponent<MeshCollider>() != null)
                return true;

            if (go.GetComponentInChildren<MeshFilter>(true) != null ||
                go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null ||
                go.GetComponentInChildren<MeshCollider>(true) != null)
                return true;

            return false;
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

    [HarmonyPatch]
    internal static class TMPTextPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var setter = AccessTools.PropertySetter(typeof(TMP_Text), "text");
            if (setter != null)
                yield return setter;

            var setTextBool = AccessTools.Method(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(bool) });
            if (setTextBool != null)
                yield return setTextBool;

            var setText = AccessTools.Method(typeof(TMP_Text), "SetText", new[] { typeof(string) });
            if (setText != null)
                yield return setText;
        }

        private static void Postfix(TMP_Text __instance)
        {
            ReplacerController.Instance?.HandleTextLabelUpdate(__instance);
        }
    }

    [HarmonyPatch(typeof(Debug))]
    internal static class DebugLogWarningPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Debug.LogWarning), new[] { typeof(object) })]
        private static bool LogWarningPrefix(object message)
        {
            return !ShouldSuppress(message);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Debug.LogWarning), new[] { typeof(object), typeof(UnityEngine.Object) })]
        private static bool LogWarningContextPrefix(object message, UnityEngine.Object context)
        {
            return !ShouldSuppress(message);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Debug.LogWarningFormat), new[] { typeof(string), typeof(object[]) })]
        private static bool LogWarningFormatPrefix(string format, params object[] args)
        {
            return !ShouldSuppress(format);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Debug.LogWarningFormat), new[] { typeof(UnityEngine.Object), typeof(string), typeof(object[]) })]
        private static bool LogWarningFormatContextPrefix(UnityEngine.Object context, string format, params object[] args)
        {
            return !ShouldSuppress(format);
        }

        private static bool ShouldSuppress(object candidate)
        {
            var controller = ReplacerController.Instance;
            if (controller == null)
                return false;

            var message = candidate as string ?? candidate?.ToString();
            return controller.ShouldSuppressLog(message);
        }
    }

}































