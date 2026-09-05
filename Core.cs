using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FontReplace
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public partial class FontReplacePlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "hiddenhiragi.Volcano.fontreplace";
        private const string PluginName = "Volcano-FontReplace 多语言字体切换";
        private const string PluginVersion = "1.3.0";

        private const string ChineseLocaleKey = "ch";
        private const string EnglishLocaleKey = "en";
        private const string RussianLocaleKey = "ru";
        private const string JapaneseLocaleKey = "jp";
        private const string KoreanLocaleKey = "kr";

        private const string ModSection = "0. 模组";
        private const string ChineseFontSection = "1. 中文字体";
        private const string EnglishFontSection = "1. 英文字体";
        private const string RussianFontSection = "1. 俄文字体";
        private const string JapaneseFontSection = "1. 日文字体";
        private const string KoreanFontSection = "1. 韩文字体";
        private const string KeepOriginalSection = "2. 原版字符";
        private const string ExclusionSection = "3. GUI 排除";
        private const string DefaultBundleName = "";
        private const string FontDirName = "Font";
        private const float FontHintMinHeight = 18f;

        public static readonly string dllPath = Assembly.GetExecutingAssembly().Location;
        public static readonly string pluginDir = Path.GetDirectoryName(dllPath) ?? AppDomain.CurrentDomain.BaseDirectory;

        private TMP_FontAsset? _replacementFontAsset;
        private Font? _replacementUnityFont;
        private string _currentLocaleKey = string.Empty;
        private string _currentLocaleRaw = string.Empty;
        private string _activeLocaleKey = string.Empty;
        private string _activeLocaleRaw = string.Empty;
        private string _activeBundleName = string.Empty;

        private Action _unsubscribeLocaleUpdate = delegate { };
        private bool _hasLocaleListener;
        private bool _isApplying;
        private bool _sceneListenerRegistered;

        private readonly List<string> _fontBundleNames = new List<string>();
        private readonly Dictionary<ConfigEntryBase, int> _selectedFontIndices = new Dictionary<ConfigEntryBase, int>();
        private readonly HashSet<ConfigEntryBase> _forceReloadEntries = new HashSet<ConfigEntryBase>();
        private bool _fontListLoaded;
        private string _fontUiHint = string.Empty;
        private float _fontUiHintUntil;
        private GUIStyle? _fontHintStyle;

        private ConfigEntry<bool> _modEnabled = null!;
        private ConfigEntry<bool> _keepOriginalLatin = null!;
        private ConfigEntry<bool> _keepOriginalDigits = null!;
        private ConfigEntry<bool> _excludeVolcanoSubtitle = null!;
        private ConfigEntry<bool> _excludeConfiguredModGui = null!;
        private ConfigEntry<string> _excludedGuiRootNames = null!;
        private ConfigEntry<string> _excludedAssemblyKeywords = null!;

        private sealed class LanguageFontProfile
        {
            public string LocaleKey = string.Empty;
            public string ConfigSection = string.Empty;
            public ConfigEntry<string> Bundle = null!;
            public ConfigEntry<float> Scale = null!;
            public ConfigurationManagerAttributes BundleUi = null!;
            public ConfigurationManagerAttributes ScaleUi = null!;
        }

        private readonly Dictionary<string, LanguageFontProfile> _fontProfiles =
            new Dictionary<string, LanguageFontProfile>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ConfigEntryBase, LanguageFontProfile> _profileByEntry =
            new Dictionary<ConfigEntryBase, LanguageFontProfile>();

        private sealed class LoadedFont
        {
            public TMP_FontAsset TmpFont = null!;
            public Font? UnityFont;
            public float BaseScale;
        }

        private readonly Dictionary<string, LoadedFont> _loadedFonts =
            new Dictionary<string, LoadedFont>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _replacementTmpFontIds = new HashSet<int>();
        private readonly HashSet<int> _replacementUnityFontIds = new HashSet<int>();

        private TMP_FontAsset? _originalDefaultTmpFont;

        private sealed class OriginalTmpFontRecord
        {
            public TMP_Text Text = null!;
            public TMP_FontAsset Font = null!;
        }

        private sealed class OriginalUiFontRecord
        {
            public Text Text = null!;
            public Font Font = null!;
        }

        private readonly Dictionary<int, OriginalTmpFontRecord> _originalTmpFonts =
            new Dictionary<int, OriginalTmpFontRecord>();
        private readonly Dictionary<int, OriginalUiFontRecord> _originalUnityFonts =
            new Dictionary<int, OriginalUiFontRecord>();
        private readonly Dictionary<string, TMP_FontAsset> _originalLocaleFonts =
            new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _localeFontKeysOriginallyMissing =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static FontReplacePlugin? s_instance;

        private Harmony? _harmony;
        private bool _textMonitorPatched;
        private bool _flushScheduled;
        private readonly HashSet<TMP_Text> _dirtyTmpTexts = new HashSet<TMP_Text>();
        private readonly HashSet<Text> _dirtyUiTexts = new HashSet<Text>();
        private readonly Dictionary<int, string> _handledTmpContent = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _handledUiContent = new Dictionary<int, string>();
        private readonly Dictionary<int, bool> _handledTmpKeepOriginal = new Dictionary<int, bool>();
        private readonly Dictionary<int, bool> _handledUiKeepOriginal = new Dictionary<int, bool>();
        private static readonly WaitForEndOfFrame WaitEndOfFrame = new WaitForEndOfFrame();

        private bool _fontScaleApplyPending;
        private bool _configManagerRefreshPending;
        private bool _isLocaleFontActive;
        private string _uiLocaleKey = EnglishLocaleKey;

        private readonly HashSet<string> _customExcludedRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _customExcludedAssemblyKeywords = new List<string>();
        private readonly Dictionary<Type, bool> _excludedComponentTypeCache = new Dictionary<Type, bool>();

        private void Awake()
        {
            s_instance = this;

            InitConfig();
            CacheOriginalDefaultFonts();
            RegisterLocaleListener();
            RegisterSceneListener();

            if (_modEnabled.Value)
            {
                SetupTextMonitoring();
            }
        }

        private void OnDestroy()
        {
            if (_hasLocaleListener)
            {
                _unsubscribeLocaleUpdate();
                _hasLocaleListener = false;
            }

            if (_sceneListenerRegistered)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                _sceneListenerRegistered = false;
            }

            try
            {
                DeactivateFontOverride(LocaleManagerCompat.GetInstance(Logger), "pluginDestroyed");
            }
            catch (Exception e)
            {
                Logger.LogDebug("[FontReplace] 插件卸载时恢复字体失败: " + e);
            }

            UnsubscribeConfigEvents();
            TeardownTextMonitoring();
            s_instance = null;
        }
    }
}
