using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FontReplace
{
    [BepInPlugin("hiddenhiragi.Volcano.fontreplace", "FontReplace 中文字体切换", "1.0.0")]
    public class FontReplacePlugin : BaseUnityPlugin
    {
        private const string ChineseLocaleKey = "ch";
        private const string FontSection = "1. 中文字体";
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

        private ConfigEntry<string> _fontBundleName;

        // 初始化配置
        private void Awake()
        {
            InitConfig();
            // 根据当前配置加载字体资源
            LoadFontAsset(_fontBundleName.Value);
            RegisterLocaleListener();
            RegisterSceneListener();
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
        }

        // 初始化 BepInEx 配置项与 UI
        private void InitConfig()
        {
            _fontBundleName = Config.Bind(
                FontSection,
                "字体切换",
                DefaultBundleName,
                new ConfigDescription(
                    "从 BepInEx\\plugins\\FontReplace\\Font 读取字体文件并切换应用。",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "字体切换\n",
                        CustomDrawer = DrawFontBundlePicker,
                        HideDefaultButton = true
                    }));

            // 启动时扫描字体资源
            ScanFontBundles(true);
        }

        // 从 AssetBundle 加载 TMP_FontAsset
        private void LoadFontAsset(string bundleName)
        {
            var picked = string.IsNullOrEmpty(bundleName) ? DefaultBundleName : bundleName;
            if (string.IsNullOrEmpty(picked))
            {
                Logger.LogWarning("[FontReplace] 未选择任何字体资源，跳过加载");
                return;
            }

            var fontDir = Path.Combine(pluginDir, FontDirName);
            var bundlePath = Path.Combine(fontDir, picked);
            AssetBundle ab = AssetBundle.LoadFromFile(bundlePath);
            if (ab == null)
            {
                Logger.LogError("[FontReplace] 加载 AssetBundle 失败: " + bundlePath);
                return;
            }

            var fontAssetName = Path.GetFileNameWithoutExtension(picked);
            TMP_FontAsset asset = ab.LoadAsset<TMP_FontAsset>(fontAssetName);
            if (asset == null)
            {
                var assets = ab.LoadAllAssets<TMP_FontAsset>();
                if (assets == null || assets.Length == 0)
                {
                    Logger.LogError("[FontReplace] AssetBundle 中未找到任何 TMP_FontAsset");
                    ab.Unload(false);
                    return;
                }

                asset = assets[0];
            }

            if (asset == null)
            {
                Logger.LogError("[FontReplace] AssetBundle 中未找到任何 TMP_FontAsset");
                ab.Unload(false);
                return;
            }

            _chineseFontAsset = asset;
            _chineseUnityFont = asset.sourceFontFile;

            Logger.LogInfo("[FontReplace] 已加载字体资源: " + asset.sourceFontFile + " (" + picked + ")");
            Logger.LogInfo("[FontReplace] 字体名=" + asset.name + ", 字体家族=" + asset.faceInfo.familyName + ", 样式=" + asset.faceInfo.styleName);
            Logger.LogInfo("[FontReplace] " + asset.atlasPopulationMode);
            Logger.LogInfo("[FontReplace] " + asset.atlasRenderMode);

            ab.Unload(false);
        }

        // LocaleManager 的语言更新监听
        private void RegisterLocaleListener()
        {
            var localeManager = LocaleManagerCompat.GetInstance(Logger);
            if (localeManager == null)
            {
                Logger.LogWarning("[FontReplace] 未找到 LocaleManager；仅在场景加载时生效。");
                return;
            }

            _unsubscribeLocaleUpdate = LocaleManagerCompat.TrySubscribeLocaleUpdate(localeManager, OnLocaleUpdated, Logger);
            _hasLocaleListener = _unsubscribeLocaleUpdate != null;

            if (!_hasLocaleListener)
            {
                _unsubscribeLocaleUpdate = delegate { };
            }

            ConfigureFallbacks(localeManager);
            TryApplyChineseFont(localeManager, "initial");
        }

        // Unity 场景加载监听
        private void RegisterSceneListener()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            _sceneListenerRegistered = true;
        }

        // 场景加载完成回调
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var localeManager = LocaleManagerCompat.GetInstance(Logger);
            if (localeManager != null)
            {
                TryApplyChineseFont(localeManager, "sceneLoaded");
            }
            else
            {
                // 没有 LocaleManager 的情况下，仍然尝试刷新 TMP 默认字体
                ApplyDefaultFontAndRefresh("sceneLoaded(noLocaleManager)");
            }
        }

        // LocaleManager 语言更新回调
        private void OnLocaleUpdated()
        {
            var localeManager = LocaleManagerCompat.GetInstance(Logger);
            if (localeManager == null)
            {
                return;
            }

            ConfigureFallbacks(localeManager);
            TryApplyChineseFont(localeManager, "update");
        }

        // 尝试在当前语言环境下应用中文字体
        private void TryApplyChineseFont(LocaleManagerClass localeManager, string reason)
        {
            if (_isApplying)
            {
                Logger.LogInfo("[FontReplace] 跳过应用（重新申请）: " + reason);
                return;
            }

            if (_chineseFontAsset == null)
            {
                Logger.LogWarning("[FontReplace] Font asset not loaded yet (" + reason + ").");
                return;
            }

            var currentLang = LocaleManagerCompat.GetCurrentLanguage(localeManager);
            var appliedLang = LocaleManagerCompat.GetAppliedLanguage(localeManager);
            Logger.LogInfo("[FontReplace] CurrentLanguage=" + currentLang + ", AppliedLanguage=" + appliedLang + " (" + reason + ")");

            if (!string.Equals(currentLang, ChineseLocaleKey, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInfo("[FontReplace] ??????????????");
                return;
            }

            _isApplying = true;
            try
            {
                // 1) 尝试把 ch 对应的字体写回 LocaleManager 的字体映射（旧/新版本字段名可能不同）
                LocaleManagerCompat.TrySetLocaleFont(localeManager, ChineseLocaleKey, _chineseFontAsset, Logger);

                // 2) 尝试同步 "已应用语言"，避免 UpdateApplicationLanguage 逻辑被短路
                LocaleManagerCompat.TrySetAppliedLanguage(localeManager, currentLang, Logger);

                // 3) 让 LocaleManager 按当前 locale 重新构建 fallback（如果该版本支持）
                LocaleManagerCompat.TryApplyLocaleInternal(localeManager, currentLang, Logger);

                // 4) 兜底：把 TMP 默认字体替换 + 扫一遍现有 Text
                ApplyDefaultFontAndRefresh(reason);

                // 5) 初次应用时，尽量触发一次 Locale 更新事件（如果该版本存在 BindableEvent）
                if (string.Equals(reason, "initial", StringComparison.OrdinalIgnoreCase))
                {
                    LocaleManagerCompat.TryInvokeLocaleUpdated(localeManager, Logger);
                }

                Logger.LogInfo("[FontReplace] ????????");
                LogSampleTextFonts(reason);
            }
            finally
            {
                _isApplying = false;
            }
        }

        // 配置中文字体的 fallback 字体列表
        private void ConfigureFallbacks(LocaleManagerClass localeManager)
        {
            if (_chineseFontAsset == null)
            {
                return;
            }

            if (_chineseFontAsset.fallbackFontAssetTable == null)
            {
                _chineseFontAsset.fallbackFontAssetTable = new List<TMP_FontAsset>();
            }

            var existing = new HashSet<int>();
            foreach (var fb in _chineseFontAsset.fallbackFontAssetTable)
            {
                if (fb != null)
                {
                    existing.Add(fb.GetInstanceID());
                }
            }

            int added = 0;

            // 优先把 en/ru 的主字体加入 fallback（如果能从 LocaleManager 里读到的话）
            var en = LocaleManagerCompat.TryGetLocaleFont(localeManager, "en");
            if (en != null && en != _chineseFontAsset && existing.Add(en.GetInstanceID()))
            {
                _chineseFontAsset.fallbackFontAssetTable.Add(en);
                added++;
            }

            var ru = LocaleManagerCompat.TryGetLocaleFont(localeManager, "ru");
            if (ru != null && ru != _chineseFontAsset && existing.Add(ru.GetInstanceID()))
            {
                _chineseFontAsset.fallbackFontAssetTable.Add(ru);
                added++;
            }

            // 再把 UI/Fonts 目录里所有 TMP_FontAsset 加入 fallback
            var uiFonts = Resources.LoadAll<TMP_FontAsset>("UI/Fonts");
            if (uiFonts != null)
            {
                for (int i = 0; i < uiFonts.Length; i++)
                {
                    var font = uiFonts[i];
                    if (font == null || font == _chineseFontAsset)
                    {
                        continue;
                    }

                    if (existing.Add(font.GetInstanceID()))
                    {
                        _chineseFontAsset.fallbackFontAssetTable.Add(font);
                        added++;
                    }
                }
            }

            if (added > 0)
            {
                Logger.LogInfo("[FontReplace] Fallback fonts added: " + added);
            }
        }

        // 替换 TMP_Settings 的默认字体，并刷新当前已存在的文本组件
        private void ApplyDefaultFontAndRefresh(string reason)
        {
            if (_chineseFontAsset == null)
            {
                return;
            }

            // 替换 TMP 默认字体（部分版本字段名不同，所以用反射）
            var settings = TMP_Settings.instance;
            if (settings != null)
            {
                var field = typeof(TMP_Settings).GetField("m_defaultFontAsset", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(settings, _chineseFontAsset);
                }
                else
                {
                    Logger.LogWarning("[FontReplace] TMP_Settings 中未找到 m_defaultFontAsset 字段，无法替换默认字体");
                }
            }
            else
            {
                Logger.LogWarning("[FontReplace] TMP_Settings 实例为空，无法替换默认字体");
            }

            int updated = 0;

            var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            if (texts != null)
            {
                for (int i = 0; i < texts.Length; i++)
                {
                    var text = texts[i];
                    if (text == null)
                    {
                        continue;
                    }

                    if (text.font != _chineseFontAsset)
                    {
                        text.font = _chineseFontAsset;
                        updated++;
                    }
                }
            }

            var unityTexts = Resources.FindObjectsOfTypeAll<Text>();
            if (unityTexts != null)
            {
                if (_chineseUnityFont != null)
                {
                    for (int i = 0; i < unityTexts.Length; i++)
                    {
                        var text = unityTexts[i];
                        if (text == null)
                        {
                            continue;
                        }

                        if (text.font != _chineseUnityFont)
                        {
                            text.font = _chineseUnityFont;
                            updated++;
                        }
                    }
                }
                else
                {
                    Logger.LogWarning("[FontReplace] sourceFontFile 失效 UnityEngine.UI.Text");
                }
            }

            Logger.LogInfo("[FontReplace] 已刷新文本组件数量： " + updated + " (" + reason + ")");
        }

        // 打印部分 TMP_Text 使用的字体信息，用于调试确认字体是否生效
        private void LogSampleTextFonts(string reason)
        {
            var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            if (texts == null || texts.Length == 0)
            {
                Logger.LogInfo("[FontReplace] 未找到任何 TMP_Text（原因：" + reason + ").");
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int logged = 0;
            for (int i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text == null || text.font == null)
                {
                    continue;
                }

                var key = text.font.name;
                if (seen.Add(key))
                {
                    Logger.LogInfo("[FontReplace] 示例 TMP_Text 使用字体：" + text.font.name + " (示例, " + reason + ")");
                    logged++;
                    if (logged >= 5)
                    {
                        break;
                    }
                }
            }
        }

        // F12 自定义字体选择 UI
        private void DrawFontBundlePicker(ConfigEntryBase entry)
        {
            if (!s_FontListLoaded)
            {
            ScanFontBundles(true);
            }

            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("<", GUILayout.Width(28)))
            {
                if (s_FontBundleNames.Count > 0)
                {
                    s_SelectedFontIndex = (s_SelectedFontIndex - 1 + s_FontBundleNames.Count) % s_FontBundleNames.Count;
                }
            }

            var label = (s_FontBundleNames.Count > 0 && s_SelectedFontIndex >= 0 && s_SelectedFontIndex < s_FontBundleNames.Count)
                ? s_FontBundleNames[s_SelectedFontIndex]
                : "(无任何字体资源)";
            GUILayout.Label(label, GUILayout.ExpandWidth(true));

            if (GUILayout.Button(">", GUILayout.Width(28)))
            {
                if (s_FontBundleNames.Count > 0)
                {
                    s_SelectedFontIndex = (s_SelectedFontIndex + 1) % s_FontBundleNames.Count;
                }
            }

            if (GUILayout.Button("刷新", GUILayout.Width(64)))
            {
            ScanFontBundles(true);
                Logger.LogInfo("[FontReplace] 已重新扫描字体目录，发现字体数量=" + s_FontBundleNames.Count);
                ShowFontUiHint("已刷新到: " + s_FontBundleNames.Count + " 个字体资源");
            }

            if (GUILayout.Button("应用", GUILayout.Width(64)))
            {
                if (s_FontBundleNames.Count > 0)
                {
                    var pick = s_FontBundleNames[s_SelectedFontIndex];
                    _fontBundleName.Value = pick;
                    ApplyFontBundleByName(pick, "ui");
                    ShowFontUiHint("已成功应用： " + pick);
                }
            }

            GUILayout.EndHorizontal();

            if (s_FontHintStyle == null)
            {
                s_FontHintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    wordWrap = true,
                    alignment = TextAnchor.UpperLeft
                };
                s_FontHintStyle.normal.textColor = new Color(0.7f, 1f, 0.7f, 1f);
            }

            GUILayout.Space(4);
            string hintMsg = (s_FontUiHintUntil > 0f && Time.realtimeSinceStartup < s_FontUiHintUntil)
                ? ("> " + s_FontUiHint)
                : " ";
            GUILayout.Label(hintMsg, s_FontHintStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(FontHintMinHeight));

            GUILayout.EndVertical();
        }

        // F12UI 在配置界面中显示短暂提示文本
        private void ShowFontUiHint(string msg, float seconds = 2f)
        {
            s_FontUiHint = msg ?? "";
            s_FontUiHintUntil = Time.realtimeSinceStartup + Mathf.Max(0.5f, seconds);
        }

        // 扫描字体目录，获取所有可用的 AssetBundle 文件名
        private void ScanFontBundles(bool resetSelectionToCurrent)
        {
            try
            {
                var list = new List<string>();
                // ??????
            var fontDir = Path.Combine(pluginDir, FontDirName);
                if (Directory.Exists(fontDir))
                {
                    var files = Directory.GetFiles(fontDir, "*", SearchOption.TopDirectoryOnly);
                    for (int i = 0; i < files.Length; i++)
                    {
                        var name = Path.GetFileName(files[i]);
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }

                        // .NET Framework 下 List.Contains 没有带 comparer 的重载，用 Any 替代
                        if (!list.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            list.Add(name);
                        }
                    }
                }

                s_FontBundleNames = list;
                s_FontListLoaded = true;

                if (resetSelectionToCurrent)
                {
                    var current = _fontBundleName != null ? (_fontBundleName.Value ?? DefaultBundleName) : DefaultBundleName;
                    int idx = s_FontBundleNames.FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        s_SelectedFontIndex = idx;
                    }
                    else if (s_FontBundleNames.Count > 0)
                    {
                        s_SelectedFontIndex = 0;
                        if (_fontBundleName != null)
                        {
                            _fontBundleName.Value = s_FontBundleNames[0];
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[FontReplace] 扫描字体目录时发生异常" + e);
                s_FontBundleNames = new List<string>();
                s_SelectedFontIndex = 0;
                s_FontListLoaded = true;
            }
        }

        // 根据名称加载并立即应用指定字体Bundle文件
        private void ApplyFontBundleByName(string bundleName, string reason)
        {
            LoadFontAsset(bundleName);
            var localeManager = LocaleManagerCompat.GetInstance(Logger);
            if (localeManager != null)
            {
                ConfigureFallbacks(localeManager);
                TryApplyChineseFont(localeManager, reason);
            }
            else
            {
                ApplyDefaultFontAndRefresh(reason + "(noLocaleManager)");
            }
        }

        /// <summary>
        /// 兼容层：用反射读取/写入 LocaleManagerClass 的成员。
        /// 老版本（或不同分支）里经常因为混淆/字段改名导致：String_1 / Dictionary_1 / BindableEvent_0 等不存在，从而编译报 CS1061。
        /// 这里统一用“按名称优先 + 按类型兜底”的方式处理。
        /// </summary>
        private static class LocaleManagerCompat
        {
            private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private static readonly BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            private static PropertyInfo s_singletonProp;
            private static FieldInfo s_singletonField;

            private static MemberInfo s_currentLangMember;
            private static MemberInfo s_appliedLangMember;

            private static MemberInfo s_fontMapMember;
            private static MemberInfo s_bindableEventMember;

            public static LocaleManagerClass GetInstance(BepInEx.Logging.ManualLogSource logger)
            {
                try
                {
                    var t = typeof(LocaleManagerClass);

                    // 1) 优先按常见名字取单例
                    if (s_singletonProp == null)
                    {
                        s_singletonProp = t.GetProperty("LocaleManagerClass", AnyStatic);
                        if (s_singletonProp == null || s_singletonProp.PropertyType != t)
                        {
                            // 2) 兜底：找到任意一个返回 LocaleManagerClass 的静态属性
                            var props = t.GetProperties(AnyStatic);
                            for (int i = 0; i < props.Length; i++)
                            {
                                var p = props[i];
                                if (p.PropertyType == t && p.GetIndexParameters().Length == 0)
                                {
                                    s_singletonProp = p;
                                    break;
                                }
                            }
                        }
                    }

                    if (s_singletonProp != null)
                    {
                        var v = s_singletonProp.GetValue(null, null) as LocaleManagerClass;
                        if (v != null)
                        {
                            return v;
                        }
                    }

                    // 3) 再兜底：静态字段
                    if (s_singletonField == null)
                    {
                        var fields = t.GetFields(AnyStatic);
                        for (int i = 0; i < fields.Length; i++)
                        {
                            var f = fields[i];
                            if (f.FieldType == t)
                            {
                                s_singletonField = f;
                                break;
                            }
                        }
                    }

                    if (s_singletonField != null)
                    {
                        return s_singletonField.GetValue(null) as LocaleManagerClass;
                    }
                }
                catch (Exception e)
                {
                    if (logger != null)
                    {
                        logger.LogWarning("[FontReplace] GetInstance(LocaleManager) 失效: " + e);
                    }
                }

                return null;
            }

            public static string GetCurrentLanguage(LocaleManagerClass lm)
            {
                // 源码属性 String_0 (默认 en)
                string v;
                if (TryGetString(lm, ref s_currentLangMember, new[] { "String_0", "CurrentLanguage", "Language", "Locale" }, out v))
                {
                    return v;
                }
                return "en";
            }

            public static string GetAppliedLanguage(LocaleManagerClass lm)
            {
                // 源码字段 String_1
                string v;
                if (TryGetString(lm, ref s_appliedLangMember, new[] { "String_1", "AppliedLanguage", "CurrentAppliedLanguage" }, out v))
                {
                    return v;
                }
                return string.Empty;
            }

            public static void TrySetAppliedLanguage(LocaleManagerClass lm, string lang, BepInEx.Logging.ManualLogSource logger)
            {
                if (!TrySetString(lm, ref s_appliedLangMember, new[] { "String_1", "AppliedLanguage", "CurrentAppliedLanguage" }, lang))
                {
                    // 没有也不算致命
                    if (logger != null)
                    {
                        logger.LogDebug("[FontReplace] 未找到 AppliedLanguage 成员；跳过应用。");
                    }
                }
            }

            public static TMP_FontAsset TryGetLocaleFont(LocaleManagerClass lm, string locale)
            {
                var map = GetLocaleFontMap(lm);
                if (map == null)
                {
                    return null;
                }

                TMP_FontAsset font;
                if (map.TryGetValue(locale, out font))
                {
                    return font;
                }

                return null;
            }

            public static void TrySetLocaleFont(LocaleManagerClass lm, string locale, TMP_FontAsset font, BepInEx.Logging.ManualLogSource logger)
            {
                var map = GetLocaleFontMap(lm);
                if (map == null)
                {
                    if (logger != null)
                    {
                        logger.LogWarning("[FontReplace] 未找到本地化字体映射；仅回退到 TMP_Settings。");
                    }
                    return;
                }

                map[locale] = font;

                // 打印一下确认
                if (logger != null)
                {
                    logger.LogInfo("[FontReplace] 设置本地化字体： " + locale + " -> " + (font != null ? font.name : "(null)"));
                }
            }

            public static void TryApplyLocaleInternal(LocaleManagerClass lm, string locale, BepInEx.Logging.ManualLogSource logger)
            {
                try
                {
                    // 你提供的源码里是 public void method_1(string localeType)
                    var m = lm.GetType().GetMethod("method_1", AnyInstance, null, new[] { typeof(string) }, null);
                    if (m != null)
                    {
                        m.Invoke(lm, new object[] { locale });
                        return;
                    }

                    // 兜底：有些版本叫 UpdateApplicationLanguage
                    var m2 = lm.GetType().GetMethod("UpdateApplicationLanguage", AnyInstance, null, Type.EmptyTypes, null);
                    if (m2 != null)
                    {
                        m2.Invoke(lm, null);
                    }
                }
                catch (Exception e)
                {
                    if (logger != null)
                    {
                        logger.LogWarning("[FontReplace] TryApplyLocaleInternal 失败: " + e);
                    }
                }
            }

            public static void TryInvokeLocaleUpdated(LocaleManagerClass lm, BepInEx.Logging.ManualLogSource logger)
            {
                try
                {
                    var evt = GetBindableEvent(lm);
                    if (evt == null)
                    {
                        return;
                    }

                    // BindableEvent.Invoke()
                    var invoke = evt.GetType().GetMethod("Invoke", AnyInstance, null, Type.EmptyTypes, null);
                    if (invoke != null)
                    {
                        invoke.Invoke(evt, null);
                    }
                }
                catch (Exception e)
                {
                    if (logger != null)
                    {
                        logger.LogDebug("[FontReplace] TryInvokeLocaleUpdated 失败: " + e);
                    }
                }
            }

            public static Action TrySubscribeLocaleUpdate(LocaleManagerClass lm, Action callback, BepInEx.Logging.ManualLogSource logger)
            {
                try
                {
                    // 你提供的源码里：public Action AddLocaleUpdateListener(Action callback)
                    var m = lm.GetType().GetMethod("AddLocaleUpdateListener", AnyInstance, null, new[] { typeof(Action) }, null);
                    if (m != null)
                    {
                        var ret = m.Invoke(lm, new object[] { callback }) as Action;
                        if (ret != null)
                        {
                            return ret;
                        }
                    }
                }
                catch (Exception e)
                {
                    if (logger != null)
                    {
                        logger.LogWarning("[FontReplace] TrySubscribeLocaleUpdate 失败: " + e);
                    }
                }

                return null;
            }

            private static IDictionary<string, TMP_FontAsset> GetLocaleFontMap(LocaleManagerClass lm)
            {
                if (lm == null)
                {
                    return null;
                }

                // 你提供的源码里是 public Dictionary<string, TMP_FontAsset> Dictionary_1
                if (s_fontMapMember == null)
                {
                    var t = lm.GetType();

                    // 1) 常见名字
                    var fNamed = t.GetField("Dictionary_1", AnyInstance);
                    if (fNamed != null && typeof(IDictionary<string, TMP_FontAsset>).IsAssignableFrom(fNamed.FieldType))
                    {
                        s_fontMapMember = fNamed;
                    }

                    if (s_fontMapMember == null)
                    {
                        var pNamed = t.GetProperty("Dictionary_1", AnyInstance);
                        if (pNamed != null && typeof(IDictionary<string, TMP_FontAsset>).IsAssignableFrom(pNamed.PropertyType) && pNamed.GetIndexParameters().Length == 0)
                        {
                            s_fontMapMember = pNamed;
                        }
                    }

                    // 2) 兜底：按类型找 Dictionary<string, TMP_FontAsset>
                    if (s_fontMapMember == null)
                    {
                        var fields = t.GetFields(AnyInstance);
                        for (int i = 0; i < fields.Length; i++)
                        {
                            var f = fields[i];
                            if (typeof(IDictionary<string, TMP_FontAsset>).IsAssignableFrom(f.FieldType))
                            {
                                s_fontMapMember = f;
                                break;
                            }
                        }
                    }

                    if (s_fontMapMember == null)
                    {
                        var props = t.GetProperties(AnyInstance);
                        for (int i = 0; i < props.Length; i++)
                        {
                            var p = props[i];
                            if (p.GetIndexParameters().Length != 0 || !p.CanRead)
                            {
                                continue;
                            }

                            if (typeof(IDictionary<string, TMP_FontAsset>).IsAssignableFrom(p.PropertyType))
                            {
                                s_fontMapMember = p;
                                break;
                            }
                        }
                    }
                }

                if (s_fontMapMember is FieldInfo)
                {
                    var fi = (FieldInfo)s_fontMapMember;
                    return fi.GetValue(lm) as IDictionary<string, TMP_FontAsset>;
                }

                if (s_fontMapMember is PropertyInfo)
                {
                    var pi = (PropertyInfo)s_fontMapMember;
                    return pi.GetValue(lm, null) as IDictionary<string, TMP_FontAsset>;
                }

                return null;
            }

            private static object GetBindableEvent(LocaleManagerClass lm)
            {
                if (lm == null)
                {
                    return null;
                }

                // 你提供的源码里是 public BindableEvent BindableEvent_0
                if (s_bindableEventMember == null)
                {
                    var t = lm.GetType();

                    // 1) 常见名字
                    var fNamed = t.GetField("BindableEvent_0", AnyInstance);
                    if (fNamed != null)
                    {
                        s_bindableEventMember = fNamed;
                    }

                    if (s_bindableEventMember == null)
                    {
                        var pNamed = t.GetProperty("BindableEvent_0", AnyInstance);
                        if (pNamed != null && pNamed.GetIndexParameters().Length == 0)
                        {
                            s_bindableEventMember = pNamed;
                        }
                    }

                    // 2) 兜底：按类型名包含 BindableEvent 的字段/属性
                    if (s_bindableEventMember == null)
                    {
                        var fields = t.GetFields(AnyInstance);
                        for (int i = 0; i < fields.Length; i++)
                        {
                            var f = fields[i];
                            if (f.FieldType != null && f.FieldType.Name.IndexOf("BindableEvent", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                s_bindableEventMember = f;
                                break;
                            }
                        }
                    }

                    if (s_bindableEventMember == null)
                    {
                        var props = t.GetProperties(AnyInstance);
                        for (int i = 0; i < props.Length; i++)
                        {
                            var p = props[i];
                            if (p.GetIndexParameters().Length != 0 || !p.CanRead)
                            {
                                continue;
                            }

                            if (p.PropertyType != null && p.PropertyType.Name.IndexOf("BindableEvent", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                s_bindableEventMember = p;
                                break;
                            }
                        }
                    }
                }

                if (s_bindableEventMember is FieldInfo)
                {
                    return ((FieldInfo)s_bindableEventMember).GetValue(lm);
                }

                if (s_bindableEventMember is PropertyInfo)
                {
                    return ((PropertyInfo)s_bindableEventMember).GetValue(lm, null);
                }

                return null;
            }

            private static bool TryGetString(object obj, ref MemberInfo cachedMember, string[] names, out string value)
            {
                value = null;
                if (obj == null)
                {
                    return false;
                }

                try
                {
                    var t = obj.GetType();

                    if (cachedMember == null)
                    {
                        for (int i = 0; i < names.Length; i++)
                        {
                            var n = names[i];
                            var p = t.GetProperty(n, AnyInstance);
                            if (p != null && p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && p.CanRead)
                            {
                                cachedMember = p;
                                break;
                            }

                            var f = t.GetField(n, AnyInstance);
                            if (f != null && f.FieldType == typeof(string))
                            {
                                cachedMember = f;
                                break;
                            }
                        }

                        // 兜底：找第一个 string 的属性/字段（风险较大，所以仅作为最后手段）
                        if (cachedMember == null)
                        {
                            var props = t.GetProperties(AnyInstance);
                            for (int i = 0; i < props.Length; i++)
                            {
                                var p = props[i];
                                if (p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && p.CanRead)
                                {
                                    cachedMember = p;
                                    break;
                                }
                            }
                        }

                        if (cachedMember == null)
                        {
                            var fields = t.GetFields(AnyInstance);
                            for (int i = 0; i < fields.Length; i++)
                            {
                                var f = fields[i];
                                if (f.FieldType == typeof(string))
                                {
                                    cachedMember = f;
                                    break;
                                }
                            }
                        }
                    }

                    if (cachedMember is PropertyInfo)
                    {
                        value = (string)((PropertyInfo)cachedMember).GetValue(obj, null);
                        return true;
                    }

                    if (cachedMember is FieldInfo)
                    {
                        value = (string)((FieldInfo)cachedMember).GetValue(obj);
                        return true;
                    }
                }
                catch
                {
                    // 忽略
                }

                return false;
            }

            private static bool TrySetString(object obj, ref MemberInfo cachedMember, string[] names, string value)
            {
                if (obj == null)
                {
                    return false;
                }

                try
                {
                    var t = obj.GetType();

                    if (cachedMember == null)
                    {
                        for (int i = 0; i < names.Length; i++)
                        {
                            var n = names[i];

                            var p = t.GetProperty(n, AnyInstance);
                            if (p != null && p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && p.CanWrite)
                            {
                                cachedMember = p;
                                break;
                            }

                            var f = t.GetField(n, AnyInstance);
                            if (f != null && f.FieldType == typeof(string) && !f.IsInitOnly)
                            {
                                cachedMember = f;
                                break;
                            }
                        }
                    }

                    if (cachedMember is PropertyInfo)
                    {
                        ((PropertyInfo)cachedMember).SetValue(obj, value, null);
                        return true;
                    }

                    if (cachedMember is FieldInfo)
                    {
                        ((FieldInfo)cachedMember).SetValue(obj, value);
                        return true;
                    }
                }
                catch
                {
                    // 忽略
                }

                return false;
            }
        }
    }
}
