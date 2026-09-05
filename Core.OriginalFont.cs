using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FontReplace
{
    public partial class FontReplacePlugin
    {
        private void CacheOriginalDefaultFonts()
        {
            if (_originalDefaultTmpFont != null)
            {
                return;
            }

            try
            {
                var settings = TMP_Settings.instance;
                if (settings == null)
                {
                    return;
                }

                TMP_FontAsset? current = null;
                var field = typeof(TMP_Settings).GetField("m_defaultFontAsset", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    current = field.GetValue(settings) as TMP_FontAsset;
                }
                else
                {
                    var property = typeof(TMP_Settings).GetProperty("defaultFontAsset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property != null && property.CanRead)
                    {
                        current = property.GetValue(settings, null) as TMP_FontAsset;
                    }
                }

                if (current != null && !_replacementTmpFontIds.Contains(current.GetInstanceID()))
                {
                    _originalDefaultTmpFont = current;
                }
            }
            catch (Exception e)
            {
                Logger.LogDebug("[FontReplace] 缓存 TMP 默认字体失败: " + e);
            }
        }

        private void OnKeepOriginalSettingChanged(object sender, EventArgs e)
        {
            if (!_modEnabled.Value || !_isLocaleFontActive || _replacementFontAsset == null)
            {
                return;
            }

            ClearHandledTextCaches();
            ApplyDefaultFontAndRefresh("characterPolicyChanged");
        }

        private void OnModEnabledSettingChanged(object sender, EventArgs e)
        {
            if (_modEnabled.Value)
            {
                Logger.LogInfo("[FontReplace] 模组已启用。");
                SetupTextMonitoring();
                ApplyConfiguredFontForCurrentLocale("modEnabled", false);
            }
            else
            {
                Logger.LogInfo("[FontReplace] 模组已禁用，开始恢复原版字体。");
                DeactivateFontOverride(LocaleManagerCompat.GetInstance(Logger), "modDisabled");
                TeardownTextMonitoring();
            }
        }

        private void RestoreOriginalFonts()
        {
            var settings = TMP_Settings.instance;
            if (settings != null && _originalDefaultTmpFont != null)
            {
                var field = typeof(TMP_Settings).GetField("m_defaultFontAsset", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(settings, _originalDefaultTmpFont);
                }
                else
                {
                    var property = typeof(TMP_Settings).GetProperty("defaultFontAsset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property != null && property.CanWrite)
                    {
                        property.SetValue(settings, _originalDefaultTmpFont, null);
                    }
                }
            }

            foreach (var record in _originalTmpFonts.Values)
            {
                var text = record.Text;
                if (text != null && text.font != null &&
                    _replacementTmpFontIds.Contains(text.font.GetInstanceID()) && record.Font != null)
                {
                    text.font = record.Font;
                    text.havePropertiesChanged = true;
                }
            }

            foreach (var record in _originalUnityFonts.Values)
            {
                var text = record.Text;
                if (text != null && text.font != null &&
                    _replacementUnityFontIds.Contains(text.font.GetInstanceID()) && record.Font != null)
                {
                    text.font = record.Font;
                }
            }

            _originalTmpFonts.Clear();
            _originalUnityFonts.Clear();
        }

        private void RestoreFontsInExcludedScopes()
        {
            foreach (var record in _originalTmpFonts.Values)
            {
                var text = record.Text;
                if (text != null && IsExcludedFontScope(text.transform) && text.font != null &&
                    _replacementTmpFontIds.Contains(text.font.GetInstanceID()))
                {
                    text.font = record.Font;
                    text.havePropertiesChanged = true;
                }
            }

            foreach (var record in _originalUnityFonts.Values)
            {
                var text = record.Text;
                if (text != null && IsExcludedFontScope(text.transform) && text.font != null &&
                    _replacementUnityFontIds.Contains(text.font.GetInstanceID()))
                {
                    text.font = record.Font;
                }
            }
        }

        private void RestoreOriginalFontIfTracked(TMP_Text text)
        {
            if (_originalTmpFonts.TryGetValue(text.GetInstanceID(), out var record) &&
                ReferenceEquals(record.Text, text) && text.font != null &&
                _replacementTmpFontIds.Contains(text.font.GetInstanceID()))
            {
                text.font = record.Font;
                text.havePropertiesChanged = true;
            }
        }

        private void RestoreOriginalFontIfTracked(Text text)
        {
            if (_originalUnityFonts.TryGetValue(text.GetInstanceID(), out var record) &&
                ReferenceEquals(record.Text, text) && text.font != null &&
                _replacementUnityFontIds.Contains(text.font.GetInstanceID()))
            {
                text.font = record.Font;
            }
        }

        private bool ShouldKeepOriginalFont(string? value)
        {
            bool checkLatin = _keepOriginalLatin.Value;
            bool checkDigits = _keepOriginalDigits.Value;
            if ((!checkLatin && !checkDigits) || string.IsNullOrEmpty(value))
            {
                return false;
            }

            bool inTag = false;
            bool foundLatin = false;
            bool foundDigit = false;
            bool foundNonAscii = false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '<')
                {
                    inTag = true;
                    continue;
                }
                if (inTag)
                {
                    if (c == '>')
                    {
                        inTag = false;
                    }
                    continue;
                }

                if (c > 127 && !char.IsWhiteSpace(c))
                {
                    foundNonAscii = true;
                    continue;
                }

                if (checkLatin && ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
                {
                    foundLatin = true;
                }
                if (checkDigits && c >= '0' && c <= '9')
                {
                    foundDigit = true;
                }
            }

            return !foundNonAscii && ((checkLatin && foundLatin) || (checkDigits && foundDigit));
        }

        private void CacheOriginalFontIfNeeded(TMP_Text? text)
        {
            if (text == null || text.font == null || _replacementTmpFontIds.Contains(text.font.GetInstanceID()))
            {
                return;
            }

            int id = text.GetInstanceID();
            if (_originalTmpFonts.TryGetValue(id, out var existing) && ReferenceEquals(existing.Text, text))
            {
                return;
            }

            _originalTmpFonts[id] = new OriginalTmpFontRecord { Text = text, Font = text.font };
        }

        private void CacheOriginalFontIfNeeded(Text? text)
        {
            if (text == null || text.font == null || _replacementUnityFontIds.Contains(text.font.GetInstanceID()))
            {
                return;
            }

            int id = text.GetInstanceID();
            if (_originalUnityFonts.TryGetValue(id, out var existing) && ReferenceEquals(existing.Text, text))
            {
                return;
            }

            _originalUnityFonts[id] = new OriginalUiFontRecord { Text = text, Font = text.font };
        }

        private TMP_FontAsset? GetOriginalFont(TMP_Text? text)
        {
            if (text != null &&
                _originalTmpFonts.TryGetValue(text.GetInstanceID(), out var record) &&
                ReferenceEquals(record.Text, text) && record.Font != null)
            {
                return record.Font;
            }

            return _originalDefaultTmpFont ?? text?.font;
        }

        private Font? GetOriginalFont(Text? text)
        {
            if (text != null &&
                _originalUnityFonts.TryGetValue(text.GetInstanceID(), out var record) &&
                ReferenceEquals(record.Text, text) && record.Font != null)
            {
                return record.Font;
            }

            return text?.font;
        }

        private void PruneOriginalFontCaches()
        {
            var deadTmpIds = new List<int>();
            foreach (var pair in _originalTmpFonts)
            {
                if (pair.Value.Text == null)
                {
                    deadTmpIds.Add(pair.Key);
                }
            }
            for (int i = 0; i < deadTmpIds.Count; i++)
            {
                _originalTmpFonts.Remove(deadTmpIds[i]);
            }

            var deadUiIds = new List<int>();
            foreach (var pair in _originalUnityFonts)
            {
                if (pair.Value.Text == null)
                {
                    deadUiIds.Add(pair.Key);
                }
            }
            for (int i = 0; i < deadUiIds.Count; i++)
            {
                _originalUnityFonts.Remove(deadUiIds[i]);
            }
        }
    }
}
