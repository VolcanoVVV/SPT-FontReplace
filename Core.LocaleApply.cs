using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if GAME_4_1
using LocalizationManager = EFT.LocalizationManager;
#else
using LocalizationManager = LocaleManagerClass;
#endif

namespace FontReplace
{
    public partial class FontReplacePlugin
    {
        private bool TryLoadFontAsset(string bundleName, bool forceReload, out LoadedFont loaded)
        {
            loaded = null!;
            var picked = bundleName?.Trim() ?? string.Empty;
            if (picked.Length == 0)
            {
                return false;
            }

            if (!string.Equals(Path.GetFileName(picked), picked, StringComparison.Ordinal) ||
                picked.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            {
                Logger.LogError("[FontReplace] 拒绝不安全的字体包路径: " + picked);
                return false;
            }

            if (!forceReload && _loadedFonts.TryGetValue(picked, out loaded))
            {
                return loaded.TmpFont != null;
            }

            AssetBundle? bundle = null;
            try
            {
                var fontDir = Path.GetFullPath(Path.Combine(pluginDir, FontDirName));
                var bundlePath = Path.GetFullPath(Path.Combine(fontDir, picked));
                var expectedPrefix = fontDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!bundlePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(bundlePath))
                {
                    Logger.LogError("[FontReplace] 字体包不存在或路径不合法: " + bundlePath);
                    return false;
                }

                bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    Logger.LogError("[FontReplace] 加载 AssetBundle 失败: " + bundlePath);
                    return false;
                }

                string expectedAssetName = Path.GetFileNameWithoutExtension(picked);
                var asset = bundle.LoadAsset<TMP_FontAsset>(expectedAssetName);
                if (asset == null)
                {
                    var assets = bundle.LoadAllAssets<TMP_FontAsset>();
                    if (assets == null || assets.Length == 0)
                    {
                        Logger.LogError("[FontReplace] AssetBundle 中未找到 TMP_FontAsset: " + picked);
                        return false;
                    }

                    asset = assets[0];
                }

                loaded = new LoadedFont
                {
                    TmpFont = asset,
                    UnityFont = asset.sourceFontFile,
                    BaseScale = asset.faceInfo.scale
                };

                _loadedFonts[picked] = loaded;
                _replacementTmpFontIds.Add(asset.GetInstanceID());
                if (loaded.UnityFont != null)
                {
                    _replacementUnityFontIds.Add(loaded.UnityFont.GetInstanceID());
                }

                Logger.LogInfo(
                    "[FontReplace] 已加载字体资源: bundle=" + picked +
                    ", asset=" + asset.name +
                    ", family=" + asset.faceInfo.familyName +
                    ", style=" + asset.faceInfo.styleName +
                    ", atlas=" + asset.atlasPopulationMode);

                if (loaded.UnityFont == null)
                {
                    Logger.LogWarning("[FontReplace] 字体包没有 sourceFontFile；TMP 可用，但旧版 Unity UI.Text 无法使用该字体。");
                }

                return true;
            }
            catch (Exception e)
            {
                Logger.LogError("[FontReplace] 加载字体包异常 (" + picked + "): " + e);
                return false;
            }
            finally
            {
                if (bundle != null)
                {
                    bundle.Unload(false);
                }
            }
        }

        private void QueueFontScaleApply()
        {
            if (_fontScaleApplyPending)
            {
                return;
            }

            _fontScaleApplyPending = true;
            StartCoroutine(ApplyFontScaleAtEndOfFrame());
        }

        private IEnumerator ApplyFontScaleAtEndOfFrame()
        {
            yield return WaitEndOfFrame;
            _fontScaleApplyPending = false;

            ApplyFontScaleToAsset();
            RefreshReplacementFontTexts();
        }

        private void ApplyFontScaleToAsset()
        {
            if (_replacementFontAsset == null ||
                !_fontProfiles.TryGetValue(_activeLocaleKey, out var profile) ||
                !_loadedFonts.TryGetValue(_activeBundleName, out var loaded))
            {
                return;
            }

            var faceInfo = _replacementFontAsset.faceInfo;
            faceInfo.scale = loaded.BaseScale * profile.Scale.Value;
            _replacementFontAsset.faceInfo = faceInfo;
        }

        private void RefreshReplacementFontTexts()
        {
            if (_replacementFontAsset == null)
            {
                return;
            }

            var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text != null && text.font == _replacementFontAsset)
                {
                    text.havePropertiesChanged = true;
                }
            }
        }

        private void RegisterLocaleListener()
        {
            var localeManager = LocaleManagerCompat.GetInstance(Logger);
            if (localeManager == null)
            {
                Logger.LogWarning("[FontReplace] LocaleManager 尚未就绪；将在场景加载时重试。");
                _currentLocaleRaw = LocaleFromSystemLanguage(Application.systemLanguage);
                _currentLocaleKey = NormalizeLocaleKey(_currentLocaleRaw);
                ApplyConfigUiLocalization(_currentLocaleKey);
                return;
            }

            var unsubscribe = LocaleManagerCompat.TrySubscribeLocaleUpdate(localeManager, OnLocaleUpdated, Logger);
            _hasLocaleListener = unsubscribe != null;
            _unsubscribeLocaleUpdate = unsubscribe ?? delegate { };
            ApplyConfiguredFont(localeManager, LocaleManagerCompat.GetCurrentLanguage(localeManager), "initial", false);
        }

        private void RegisterSceneListener()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            _sceneListenerRegistered = true;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ClearHandledTextCaches();
            PruneOriginalFontCaches();

            var localeManager = LocaleManagerCompat.GetInstance(Logger);
            if (localeManager != null)
            {
                if (!_hasLocaleListener)
                {
                    var unsubscribe = LocaleManagerCompat.TrySubscribeLocaleUpdate(localeManager, OnLocaleUpdated, Logger);
                    _hasLocaleListener = unsubscribe != null;
                    _unsubscribeLocaleUpdate = unsubscribe ?? delegate { };
                }

                ApplyConfiguredFont(localeManager, LocaleManagerCompat.GetCurrentLanguage(localeManager), "sceneLoaded", false);
            }
            else if (_isLocaleFontActive)
            {
                ApplyDefaultFontAndRefresh("sceneLoaded(noLocaleManager)");
            }
        }

        private void OnLocaleUpdated()
        {
            var localeManager = LocaleManagerCompat.GetInstance(Logger);
            if (localeManager != null)
            {
                ApplyConfiguredFont(localeManager, LocaleManagerCompat.GetCurrentLanguage(localeManager), "localeUpdated", false);
            }
        }

        private void ApplyConfiguredFontForCurrentLocale(string reason, bool forceReload)
        {
            var localeManager = LocaleManagerCompat.GetInstance(Logger);
            string rawLocale = localeManager != null
                ? LocaleManagerCompat.GetCurrentLanguage(localeManager)
                : _currentLocaleRaw;

            if (string.IsNullOrWhiteSpace(rawLocale))
            {
                Logger.LogWarning("[FontReplace] 无法确定当前游戏语言，暂不应用字体。");
                return;
            }

            ApplyConfiguredFont(localeManager, rawLocale, reason, forceReload);
        }

        private void ApplyConfiguredFont(LocalizationManager? localeManager, string rawLocale, string reason, bool forceReload)
        {
            string normalizedLocale = NormalizeLocaleKey(rawLocale);
            _currentLocaleRaw = rawLocale;
            _currentLocaleKey = normalizedLocale;
            ApplyConfigUiLocalization(normalizedLocale);

            if (!_modEnabled.Value)
            {
                DeactivateFontOverride(localeManager, reason + "(disabled)");
                return;
            }

            if (_isApplying)
            {
                Logger.LogDebug("[FontReplace] 跳过重复字体应用: " + reason);
                return;
            }

            if (!_fontProfiles.TryGetValue(normalizedLocale, out var profile) ||
                string.IsNullOrWhiteSpace(profile.Bundle.Value))
            {
                DeactivateFontOverride(localeManager, reason + "(noProfile)");
                return;
            }

            if (!TryLoadFontAsset(profile.Bundle.Value, forceReload, out var loaded))
            {
                Logger.LogWarning("[FontReplace] 当前语言字体包加载失败，保留上一次有效状态: locale=" + rawLocale);
                if (!string.Equals(_activeLocaleKey, normalizedLocale, StringComparison.OrdinalIgnoreCase))
                {
                    DeactivateFontOverride(localeManager, reason + "(loadFailed)");
                }
                return;
            }

            _isApplying = true;
            try
            {
                CacheOriginalDefaultFonts();

                if (localeManager != null &&
                    _isLocaleFontActive &&
                    !string.Equals(_activeLocaleRaw, rawLocale, StringComparison.OrdinalIgnoreCase))
                {
                    RestoreLocaleFontMapping(localeManager, _activeLocaleRaw);
                }

                _replacementFontAsset = loaded.TmpFont;
                _replacementUnityFont = loaded.UnityFont;
                _activeLocaleKey = normalizedLocale;
                _activeLocaleRaw = rawLocale;
                _activeBundleName = profile.Bundle.Value;
                _isLocaleFontActive = true;

                if (localeManager != null)
                {
                    CaptureOriginalLocaleFont(localeManager, rawLocale);
                    ConfigureFallbacks(localeManager, rawLocale, loaded.TmpFont);
                    LocaleManagerCompat.TrySetLocaleFont(localeManager, rawLocale, loaded.TmpFont, Logger);
                    LocaleManagerCompat.TryApplyLocaleInternal(localeManager, rawLocale, Logger);
                }

                ApplyFontScaleToAsset();
                ClearHandledTextCaches();
                ApplyDefaultFontAndRefresh(reason);
                Logger.LogInfo("[FontReplace] 已应用语言字体: locale=" + rawLocale + ", bundle=" + profile.Bundle.Value);
            }
            catch (Exception e)
            {
                Logger.LogError("[FontReplace] 应用语言字体失败: locale=" + rawLocale + ", error=" + e);
                try
                {
                    DeactivateFontOverride(localeManager, reason + "(exception)");
                }
                catch (Exception restoreError)
                {
                    Logger.LogError("[FontReplace] 应用失败后的字体恢复也失败: " + restoreError);
                }
            }
            finally
            {
                _isApplying = false;
            }
        }

        private void CaptureOriginalLocaleFont(LocalizationManager localeManager, string rawLocale)
        {
            if (_originalLocaleFonts.ContainsKey(rawLocale) || _localeFontKeysOriginallyMissing.Contains(rawLocale))
            {
                return;
            }

            var original = LocaleManagerCompat.TryGetLocaleFont(localeManager, rawLocale);
            if (original != null && !_replacementTmpFontIds.Contains(original.GetInstanceID()))
            {
                _originalLocaleFonts[rawLocale] = original;
            }
            else
            {
                _localeFontKeysOriginallyMissing.Add(rawLocale);
            }
        }

        private void RestoreLocaleFontMapping(LocalizationManager localeManager, string rawLocale)
        {
            if (string.IsNullOrWhiteSpace(rawLocale))
            {
                return;
            }

            if (_originalLocaleFonts.TryGetValue(rawLocale, out var original))
            {
                LocaleManagerCompat.TrySetLocaleFont(localeManager, rawLocale, original, Logger);
            }
            else if (_localeFontKeysOriginallyMissing.Contains(rawLocale))
            {
                LocaleManagerCompat.TryRemoveLocaleFont(localeManager, rawLocale, Logger);
            }
        }

        private void DeactivateFontOverride(LocalizationManager? localeManager, string reason)
        {
            if (!_isLocaleFontActive)
            {
                return;
            }

            string previousLocale = _activeLocaleRaw;
            if (localeManager != null)
            {
                RestoreLocaleFontMapping(localeManager, previousLocale);
            }

            RestoreOriginalFonts();
            _isLocaleFontActive = false;
            _activeLocaleKey = string.Empty;
            _activeLocaleRaw = string.Empty;
            _activeBundleName = string.Empty;
            ClearHandledTextCaches();

            if (localeManager != null)
            {
                string currentLocale = LocaleManagerCompat.GetCurrentLanguage(localeManager);
                LocaleManagerCompat.TryApplyLocaleInternal(localeManager, currentLocale, Logger);
            }

            Logger.LogInfo("[FontReplace] 已恢复游戏原版字体 (" + reason + ")");
        }

        private void ConfigureFallbacks(LocalizationManager localeManager, string rawLocale, TMP_FontAsset fontAsset)
        {
            if (fontAsset.fallbackFontAssetTable == null)
            {
                fontAsset.fallbackFontAssetTable = new List<TMP_FontAsset>();
            }

            var existing = new HashSet<int>();
            foreach (var fallback in fontAsset.fallbackFontAssetTable)
            {
                if (fallback != null)
                {
                    existing.Add(fallback.GetInstanceID());
                }
            }

            AddFallback(_originalLocaleFonts.TryGetValue(rawLocale, out var original) ? original : null);
            AddFallback(LocaleManagerCompat.TryGetLocaleFont(localeManager, EnglishLocaleKey));
            AddFallback(LocaleManagerCompat.TryGetLocaleFont(localeManager, RussianLocaleKey));

            void AddFallback(TMP_FontAsset? fallback)
            {
                if (fallback != null && fallback != fontAsset && existing.Add(fallback.GetInstanceID()))
                {
                    fontAsset.fallbackFontAssetTable.Add(fallback);
                }
            }
        }

        private void ApplyDefaultFontAndRefresh(string reason)
        {
            if (!_modEnabled.Value || !_isLocaleFontActive || _replacementFontAsset == null)
            {
                return;
            }

            CacheOriginalDefaultFonts();

            var settings = TMP_Settings.instance;
            if (settings != null)
            {
                var field = typeof(TMP_Settings).GetField("m_defaultFontAsset", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(settings, _replacementFontAsset);
                }
                else
                {
                    var property = typeof(TMP_Settings).GetProperty("defaultFontAsset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property != null && property.CanWrite)
                    {
                        property.SetValue(settings, _replacementFontAsset, null);
                    }
                }
            }

            int updated = 0;
            var tmpTexts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < tmpTexts.Length; i++)
            {
                var text = tmpTexts[i];
                if (text == null || IsExcludedFontScope(text.transform))
                {
                    continue;
                }

                CacheOriginalFontIfNeeded(text);
                var target = ShouldKeepOriginalFont(text.text) ? GetOriginalFont(text) : _replacementFontAsset;
                if (target != null && text.font != target)
                {
                    text.font = target;
                    text.havePropertiesChanged = true;
                    updated++;
                }
            }

            var uiTexts = Resources.FindObjectsOfTypeAll<Text>();
            if (_replacementUnityFont != null)
            {
                for (int i = 0; i < uiTexts.Length; i++)
                {
                    var text = uiTexts[i];
                    if (text == null || IsExcludedFontScope(text.transform))
                    {
                        continue;
                    }

                    CacheOriginalFontIfNeeded(text);
                    var target = ShouldKeepOriginalFont(text.text) ? GetOriginalFont(text) : _replacementUnityFont;
                    if (target != null && text.font != target)
                    {
                        text.font = target;
                        updated++;
                    }
                }
            }

            Logger.LogInfo("[FontReplace] 已刷新文本组件数量=" + updated + " (" + reason + ")");
        }

        private void ClearHandledTextCaches()
        {
            _handledTmpContent.Clear();
            _handledUiContent.Clear();
            _handledTmpKeepOriginal.Clear();
            _handledUiKeepOriginal.Clear();
        }

        private static string NormalizeLocaleKey(string? locale)
        {
            string value = (locale ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
            if (value == "ch" || value == "cn" || value.StartsWith("zh", StringComparison.Ordinal))
            {
                return ChineseLocaleKey;
            }
            if (value == "en" || value.StartsWith("en-", StringComparison.Ordinal))
            {
                return EnglishLocaleKey;
            }
            if (value == "ru" || value.StartsWith("ru-", StringComparison.Ordinal))
            {
                return RussianLocaleKey;
            }
            if (value == "jp" || value == "ja" || value.StartsWith("ja-", StringComparison.Ordinal))
            {
                return JapaneseLocaleKey;
            }
            if (value == "kr" || value == "ko" || value.StartsWith("ko-", StringComparison.Ordinal))
            {
                return KoreanLocaleKey;
            }

            int separator = value.IndexOf('-');
            return separator > 0 ? value.Substring(0, separator) : value;
        }

        private static string LocaleFromSystemLanguage(SystemLanguage language)
        {
            return language switch
            {
                SystemLanguage.Chinese => ChineseLocaleKey,
                SystemLanguage.ChineseSimplified => ChineseLocaleKey,
                SystemLanguage.ChineseTraditional => ChineseLocaleKey,
                SystemLanguage.Russian => RussianLocaleKey,
                SystemLanguage.Japanese => JapaneseLocaleKey,
                SystemLanguage.Korean => KoreanLocaleKey,
                _ => EnglishLocaleKey
            };
        }
    }
}
