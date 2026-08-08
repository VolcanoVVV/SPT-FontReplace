using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static EFT.ScenesPreset;

namespace FontReplace
{
    [BepInPlugin("hiddenhiragi.Volcano.fontreplace", "Volcano-FontReplace 火山家的中文字体切换", "1.2.2")]
    public partial class FontReplacePlugin : BaseUnityPlugin
    {
        private const string ChineseLocaleKey = "ch";
        private const string ModSection = "0. 模组";
        private const string FontSection = "1. 中文字体";
        private const string KeepOriginalSection = "2. 原版字符";
        private const string DefaultBundleName = "";
        private const string FontDirName = "Font";
        private const float FontHintMinHeight = 18f;

        public static readonly string dllPath = Assembly.GetExecutingAssembly().Location;
        public static readonly string pluginDir = Path.GetDirectoryName(dllPath);

        private TMP_FontAsset _chineseFontAsset;
        private Font _chineseUnityFont;

        private Action _unsubscribeLocaleUpdate = delegate { };
        private bool _hasLocaleListener;
        private bool _isApplying;
        private bool _sceneListenerRegistered;

        private static List<string> s_FontBundleNames = new List<string>();
        private static int s_SelectedFontIndex;
        private static bool s_FontListLoaded;
        private static string s_FontUiHint;
        private static float s_FontUiHintUntil;
        private static GUIStyle s_FontHintStyle;

        private ConfigEntry<bool> _modEnabled;
        private ConfigEntry<string> _fontBundleName;
        private ConfigEntry<float> _fontScale;
        private ConfigEntry<bool> _keepOriginalLatin;
        private ConfigEntry<bool> _keepOriginalDigits;

        // ===== 字体还原（用于“英文字母/数字保留原版”）=====
        private TMP_FontAsset _originalDefaultTmpFont;
        private readonly Dictionary<int, TMP_FontAsset> _originalTmpFonts = new Dictionary<int, TMP_FontAsset>();
        private readonly Dictionary<int, Font> _originalUnityFonts = new Dictionary<int, Font>();

        private static FontReplacePlugin s_instance;

        // ===== 文本变化监听（Harmony 补丁 + 帧末批量处理）=====
        private Harmony _harmony;
        private bool _textMonitorPatched;
        private bool _flushScheduled;
        private readonly HashSet<TMP_Text> _dirtyTmpTexts = new HashSet<TMP_Text>();
        private readonly HashSet<Text> _dirtyUiTexts = new HashSet<Text>();
        private readonly Dictionary<int, string> _handledTmpContent = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _handledUiContent = new Dictionary<int, string>();
        private readonly Dictionary<int, bool> _handledTmpKeepOriginal = new Dictionary<int, bool>();
        private readonly Dictionary<int, bool> _handledUiKeepOriginal = new Dictionary<int, bool>();
        private static readonly WaitForEndOfFrame WaitEndOfFrame = new WaitForEndOfFrame();

        // ===== 字体缩放（faceInfo.scale，游戏内可调）=====
        private float _baseFaceInfoScale = 1f;
        private bool _fontScaleApplyPending;

        private bool _isChineseLocaleActive;

        // 初始化配置
        private void Awake()
        {
            s_instance = this;

            InitConfig();

            // 记录“游戏原版 TMP 默认字体”，用于之后恢复英文字母/数字显示
            CacheOriginalDefaultFonts();

            // 根据当前配置加载字体资源
            LoadFontAsset(_fontBundleName.Value);

            RegisterLocaleListener();
            RegisterSceneListener();

            // 监听文本变化（用于动态内容：计数/计时/弹窗等）
            if (_modEnabled == null || _modEnabled.Value)
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

            TeardownTextMonitoring();
        }

    }
}
