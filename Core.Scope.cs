using System;
using System.Collections.Generic;
using UnityEngine;

namespace FontReplace
{
    public partial class FontReplacePlugin
    {
        private static readonly HashSet<string> VolcanoSubtitleRootNames =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Subtitle.DebugUI",
                "SubtitleStandaloneCanvas",
                "SubtitleRoot",
                "SubtitleStackPanel",
                "DanmakuLayer",
                "World3DBubble",
                "SubtitleSettingsWindow",
                "SettingsWindowCanvas",
                "SubtitlePreviewPane",
                "PhraseFilterPanel",
                "PhraseFilterCanvas"
            };

        private void OnExclusionSettingChanged(object sender, EventArgs e)
        {
            RebuildExclusionRules();

            if (_isLocaleFontActive)
            {
                RestoreFontsInExcludedScopes();
                ApplyDefaultFontAndRefresh("exclusionChanged");
            }
        }

        private void RebuildExclusionRules()
        {
            _customExcludedRootNames.Clear();
            _customExcludedAssemblyKeywords.Clear();
            _excludedComponentTypeCache.Clear();

            AddTokens(_excludedGuiRootNames?.Value, _customExcludedRootNames);

            var assemblyTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTokens(_excludedAssemblyKeywords?.Value, assemblyTokens);
            _customExcludedAssemblyKeywords.AddRange(assemblyTokens);
        }

        private static void AddTokens(string? value, HashSet<string> destination)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var tokens = value.Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i].Trim();
                if (token.Length > 0)
                {
                    destination.Add(token);
                }
            }
        }

        private bool IsExcludedFontScope(Transform? transform)
        {
            var current = transform;
            bool checkConfiguredMods = _excludeConfiguredModGui != null && _excludeConfiguredModGui.Value;
            bool checkVolcano = _excludeVolcanoSubtitle == null || _excludeVolcanoSubtitle.Value;

            while (current != null)
            {
                if (checkVolcano && VolcanoSubtitleRootNames.Contains(current.name))
                {
                    return true;
                }

                if (checkConfiguredMods)
                {
                    if (_customExcludedRootNames.Contains(current.name))
                    {
                        return true;
                    }

                    if (_customExcludedAssemblyKeywords.Count > 0 && HasExcludedComponent(current))
                    {
                        return true;
                    }
                }

                current = current.parent;
            }

            return false;
        }

        private bool HasExcludedComponent(Transform transform)
        {
            var behaviours = transform.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null)
                {
                    continue;
                }

                var type = behaviour.GetType();
                if (!_excludedComponentTypeCache.TryGetValue(type, out var excluded))
                {
                    string assemblyName = type.Assembly.GetName().Name ?? string.Empty;
                    string fullName = type.FullName ?? type.Name;
                    excluded = ContainsConfiguredKeyword(assemblyName) || ContainsConfiguredKeyword(fullName);
                    _excludedComponentTypeCache[type] = excluded;
                }

                if (excluded)
                {
                    return true;
                }
            }

            return false;
        }

        private bool ContainsConfiguredKeyword(string value)
        {
            for (int i = 0; i < _customExcludedAssemblyKeywords.Count; i++)
            {
                if (value.IndexOf(_customExcludedAssemblyKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
