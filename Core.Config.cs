using BepInEx.Configuration;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FontReplace
{
    public partial class FontReplacePlugin
    {
        private readonly Dictionary<string, ConfigurationManagerAttributes> _localizedUi =
            new Dictionary<string, ConfigurationManagerAttributes>(StringComparer.Ordinal);

        private void InitConfig()
        {
            _modEnabled = BindLocalized(
                ModSection, "启用模组", true, null, "enabled",
                new ConfigurationManagerAttributes { HideDefaultButton = false });

            BindLanguageProfile(ChineseLocaleKey, ChineseFontSection);
            BindLanguageProfile(EnglishLocaleKey, EnglishFontSection);
            BindLanguageProfile(RussianLocaleKey, RussianFontSection);
            BindLanguageProfile(JapaneseLocaleKey, JapaneseFontSection);
            BindLanguageProfile(KoreanLocaleKey, KoreanFontSection);

            _keepOriginalLatin = BindLocalized(
                KeepOriginalSection, "显示原版字母", false, null, "keepLatin",
                new ConfigurationManagerAttributes { HideDefaultButton = false });

            _keepOriginalDigits = BindLocalized(
                KeepOriginalSection, "显示原版数字", false, null, "keepDigits",
                new ConfigurationManagerAttributes { HideDefaultButton = false });

            _excludeVolcanoSubtitle = BindLocalized(
                ExclusionSection, "排除 VolcanoSubtitle 全部界面", true, null, "excludeVolcano",
                new ConfigurationManagerAttributes { HideDefaultButton = false, IsAdvanced = true });

            _excludeConfiguredModGui = BindLocalized(
                ExclusionSection, "排除指定 Mod GUI", false, null, "excludeModGui",
                new ConfigurationManagerAttributes { HideDefaultButton = false, IsAdvanced = true });

            _excludedGuiRootNames = BindLocalized(
                ExclusionSection, "额外排除的 GUI 根节点", string.Empty, null, "excludedRoots",
                new ConfigurationManagerAttributes { HideDefaultButton = false, IsAdvanced = true });

            _excludedAssemblyKeywords = BindLocalized(
                ExclusionSection, "排除的程序集或类型关键字", string.Empty, null, "excludedAssemblies",
                new ConfigurationManagerAttributes { HideDefaultButton = false, IsAdvanced = true });

            _modEnabled.SettingChanged += OnModEnabledSettingChanged;
            _keepOriginalLatin.SettingChanged += OnKeepOriginalSettingChanged;
            _keepOriginalDigits.SettingChanged += OnKeepOriginalSettingChanged;
            _excludeVolcanoSubtitle.SettingChanged += OnExclusionSettingChanged;
            _excludeConfiguredModGui.SettingChanged += OnExclusionSettingChanged;
            _excludedGuiRootNames.SettingChanged += OnExclusionSettingChanged;
            _excludedAssemblyKeywords.SettingChanged += OnExclusionSettingChanged;

            RebuildExclusionRules();
            ApplyConfigUiLocalization(_uiLocaleKey);
            ScanFontBundles(true);
        }

        private ConfigEntry<T> BindLocalized<T>(
            string section,
            string key,
            T defaultValue,
            AcceptableValueBase? acceptableValues,
            string uiId,
            ConfigurationManagerAttributes attributes)
        {
            _localizedUi[uiId] = attributes;
            return Config.Bind(
                section,
                key,
                defaultValue,
                new ConfigDescription(BaseConfigDescription(uiId), acceptableValues, attributes));
        }

        private void BindLanguageProfile(string localeKey, string section)
        {
            var profile = new LanguageFontProfile
            {
                LocaleKey = localeKey,
                ConfigSection = section,
                BundleUi = new ConfigurationManagerAttributes
                {
                    CustomDrawer = DrawFontBundlePicker,
                    HideDefaultButton = true
                },
                ScaleUi = new ConfigurationManagerAttributes
                {
                    HideDefaultButton = false
                }
            };

            _localizedUi[localeKey + ".bundle"] = profile.BundleUi;
            _localizedUi[localeKey + ".scale"] = profile.ScaleUi;

            profile.Bundle = Config.Bind(
                section,
                "字体切换",
                DefaultBundleName,
                new ConfigDescription("从 Font 目录选择该游戏语言使用的字体资源包；留空时保持游戏原版字体。", null, profile.BundleUi));

            profile.Scale = Config.Bind(
                section,
                "字体缩放",
                1.0f,
                new ConfigDescription(
                    "调整该游戏语言字体的显示比例（1.0 = 字体资源原始大小）。",
                    new AcceptableValueRange<float>(0.5f, 2.0f),
                    profile.ScaleUi));

            profile.Bundle.SettingChanged += OnLanguageBundleSettingChanged;
            profile.Scale.SettingChanged += OnLanguageScaleSettingChanged;

            _fontProfiles[localeKey] = profile;
            _profileByEntry[profile.Bundle] = profile;
            _profileByEntry[profile.Scale] = profile;
        }

        private static string BaseConfigDescription(string uiId)
        {
            return uiId switch
            {
                "enabled" => "关闭后恢复游戏原版字体，不进行任何字体覆盖。",
                "keepLatin" => "仅含 ASCII 且包含 A-Z/a-z 的整段文本保留原版字体。",
                "keepDigits" => "仅含 ASCII 且包含 0-9 的整段文本保留原版字体。",
                "excludeVolcano" => "不修改 VolcanoSubtitle 的字幕、弹幕、3D气泡和全部设置/过滤界面。",
                "excludeModGui" => "按额外根节点和程序集/类型关键字排除其他 Mod GUI。",
                "excludedRoots" => "使用分号、逗号或换行分隔，匹配文本对象任意祖先的完整 GameObject 名称。",
                "excludedAssemblies" => "使用分号、逗号或换行分隔，匹配祖先组件的程序集名或完整类型名。",
                _ => string.Empty
            };
        }

        private void UnsubscribeConfigEvents()
        {
            if (_modEnabled != null)
            {
                _modEnabled.SettingChanged -= OnModEnabledSettingChanged;
                _keepOriginalLatin.SettingChanged -= OnKeepOriginalSettingChanged;
                _keepOriginalDigits.SettingChanged -= OnKeepOriginalSettingChanged;
                _excludeVolcanoSubtitle.SettingChanged -= OnExclusionSettingChanged;
                _excludeConfiguredModGui.SettingChanged -= OnExclusionSettingChanged;
                _excludedGuiRootNames.SettingChanged -= OnExclusionSettingChanged;
                _excludedAssemblyKeywords.SettingChanged -= OnExclusionSettingChanged;
            }

            foreach (var profile in _fontProfiles.Values)
            {
                profile.Bundle.SettingChanged -= OnLanguageBundleSettingChanged;
                profile.Scale.SettingChanged -= OnLanguageScaleSettingChanged;
            }
        }

        private void OnLanguageBundleSettingChanged(object sender, EventArgs e)
        {
            if (sender is ConfigEntryBase entry &&
                _profileByEntry.TryGetValue(entry, out var profile) &&
                string.Equals(profile.LocaleKey, _currentLocaleKey, StringComparison.OrdinalIgnoreCase))
            {
                ApplyConfiguredFontForCurrentLocale("bundleChanged", false);
            }
        }

        private void OnLanguageScaleSettingChanged(object sender, EventArgs e)
        {
            if (sender is ConfigEntryBase entry &&
                _profileByEntry.TryGetValue(entry, out var profile) &&
                string.Equals(profile.LocaleKey, _activeLocaleKey, StringComparison.OrdinalIgnoreCase))
            {
                QueueFontScaleApply();
            }
        }

        private void DrawFontBundlePicker(ConfigEntryBase entry)
        {
            if (!_fontListLoaded)
            {
                ScanFontBundles(true);
            }

            if (!_selectedFontIndices.TryGetValue(entry, out var selectedIndex))
            {
                selectedIndex = FindBundleIndex(entry.BoxedValue as string);
                _selectedFontIndices[entry] = selectedIndex;
            }

            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("<", GUILayout.Width(28)) && _fontBundleNames.Count > 0)
            {
                selectedIndex = (selectedIndex - 1 + _fontBundleNames.Count) % _fontBundleNames.Count;
            }

            var label = _fontBundleNames.Count > 0 && selectedIndex >= 0 && selectedIndex < _fontBundleNames.Count
                ? (string.IsNullOrEmpty(_fontBundleNames[selectedIndex]) ? UiText("gameDefault") : _fontBundleNames[selectedIndex])
                : UiText("noFonts");
            GUILayout.Label(label, GUILayout.ExpandWidth(true));

            if (GUILayout.Button(">", GUILayout.Width(28)) && _fontBundleNames.Count > 0)
            {
                selectedIndex = (selectedIndex + 1) % _fontBundleNames.Count;
            }

            if (GUILayout.Button(UiText("refresh"), GUILayout.Width(64)))
            {
                ScanFontBundles(true);
                _forceReloadEntries.Add(entry);
                selectedIndex = _selectedFontIndices.TryGetValue(entry, out var refreshedIndex) ? refreshedIndex : 0;
                int bundleCount = Math.Max(0, _fontBundleNames.Count - 1);
                Logger.LogInfo("[FontReplace] 已重新扫描字体目录，发现字体数量=" + bundleCount);
                ShowFontUiHint(string.Format(UiText("refreshed"), bundleCount));
            }

            if (GUILayout.Button(UiText("apply"), GUILayout.Width(64)) &&
                _fontBundleNames.Count > 0 &&
                selectedIndex >= 0 &&
                selectedIndex < _fontBundleNames.Count)
            {
                var picked = _fontBundleNames[selectedIndex];
                bool forceReload = _forceReloadEntries.Remove(entry);
                if (string.IsNullOrEmpty(picked) || TryLoadFontAsset(picked, forceReload, out _))
                {
                    bool changed = !string.Equals(entry.BoxedValue as string, picked, StringComparison.OrdinalIgnoreCase);
                    entry.BoxedValue = picked;

                    if (_profileByEntry.TryGetValue(entry, out var profile) &&
                        string.Equals(profile.LocaleKey, _currentLocaleKey, StringComparison.OrdinalIgnoreCase) &&
                        (!changed || !_isLocaleFontActive))
                    {
                        ApplyConfiguredFontForCurrentLocale("ui", false);
                    }

                    ShowFontUiHint(UiText("saved") + (string.IsNullOrEmpty(picked) ? UiText("gameDefault") : picked));
                }
                else
                {
                    ShowFontUiHint(UiText("loadFailed") + picked, 4f);
                }
            }

            _selectedFontIndices[entry] = selectedIndex;
            GUILayout.EndHorizontal();

            if (_fontHintStyle == null)
            {
                _fontHintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    wordWrap = true,
                    alignment = TextAnchor.UpperLeft
                };
                _fontHintStyle.normal.textColor = new Color(0.7f, 1f, 0.7f, 1f);
            }

            GUILayout.Space(4);
            string hintMsg = _fontUiHintUntil > 0f && Time.realtimeSinceStartup < _fontUiHintUntil
                ? "> " + _fontUiHint
                : " ";
            GUILayout.Label(hintMsg, _fontHintStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(FontHintMinHeight));
            GUILayout.EndVertical();
        }

        private void ShowFontUiHint(string message, float seconds = 2f)
        {
            _fontUiHint = message ?? string.Empty;
            _fontUiHintUntil = Time.realtimeSinceStartup + Mathf.Max(0.5f, seconds);
        }

        private void ScanFontBundles(bool resetSelections)
        {
            try
            {
                _fontBundleNames.Clear();
                _fontBundleNames.Add(string.Empty);
                var fontDir = Path.Combine(pluginDir, FontDirName);
                if (Directory.Exists(fontDir))
                {
                    foreach (var file in Directory.GetFiles(fontDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(file);
                        if (!string.IsNullOrWhiteSpace(name) &&
                            !_fontBundleNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            _fontBundleNames.Add(name);
                        }
                    }
                }

                _fontBundleNames.Sort(StringComparer.OrdinalIgnoreCase);
                _fontListLoaded = true;

                if (resetSelections)
                {
                    foreach (var profile in _fontProfiles.Values)
                    {
                        _selectedFontIndices[profile.Bundle] = FindBundleIndex(profile.Bundle.Value);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[FontReplace] 扫描字体目录时发生异常: " + e);
                _fontBundleNames.Clear();
                _selectedFontIndices.Clear();
                _fontListLoaded = true;
            }
        }

        private int FindBundleIndex(string? current)
        {
            int index = _fontBundleNames.FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : 0;
        }

        private void ApplyConfigUiLocalization(string locale)
        {
            string nextLocale = NormalizeLocaleKey(locale);
            bool localeChanged = !string.Equals(_uiLocaleKey, nextLocale, StringComparison.OrdinalIgnoreCase);
            _uiLocaleKey = nextLocale;

            foreach (var profile in _fontProfiles.Values)
            {
                bool visible = string.Equals(profile.LocaleKey, _uiLocaleKey, StringComparison.OrdinalIgnoreCase);
                profile.BundleUi.Browsable = visible;
                profile.ScaleUi.Browsable = visible;
            }

            SetUi("enabled", L("启用模组", "Enable Mod", "Включить мод", "Modを有効化", "모드 활성화"),
                L("关闭后恢复游戏原版字体。", "Restore the original game fonts when disabled.", "При отключении восстанавливает оригинальные шрифты игры。", "無効にするとゲーム標準フォントへ戻します。", "비활성화하면 게임 기본 글꼴로 복원합니다。"), Category("mod"));

            foreach (var key in new[] { ChineseLocaleKey, EnglishLocaleKey, RussianLocaleKey, JapaneseLocaleKey, KoreanLocaleKey })
            {
                SetUi(key + ".bundle", L("字体切换\n", "Font Bundle\n", "Пакет шрифта\n", "フォントバンドル\n", "글꼴 번들\n"),
                    L("从 Font 目录选择该游戏语言使用的字体资源包。留空时不覆盖该语言。", "Choose the font bundle used for this game language. Leave empty to keep the game font.", "Выберите пакет шрифта для этого языка. Пустое значение сохраняет шрифт игры。", "このゲーム言語で使うフォントを選びます。空欄なら標準フォントを使用します。", "이 게임 언어에 사용할 글꼴 번들을 선택합니다. 비워 두면 게임 기본 글꼴을 사용합니다。"),
                    ProfileCategory(key));
                SetUi(key + ".scale", L("字体缩放", "Font Scale", "Масштаб шрифта", "フォント倍率", "글꼴 배율"),
                    L("仅调整该语言字体的显示比例，修改后即时生效。", "Adjust only this language's font scale; changes apply immediately.", "Изменяет масштаб шрифта только для этого языка。", "この言語のフォント倍率だけを変更します。", "이 언어의 글꼴 배율만 조정합니다。"),
                    ProfileCategory(key));
            }

            SetUi("keepLatin", L("显示原版字母", "Keep Original Latin", "Сохранять латиницу", "英字を標準フォントで表示", "영문 기본 글꼴 유지"),
                L("仅含 ASCII 且包含 A-Z/a-z 的整段文本保留原版字体。", "Keep the original font for ASCII-only text containing A-Z/a-z.", "Сохраняет оригинальный шрифт для ASCII-текста с латинскими буквами。", "ASCIIのみで英字を含むテキストは標準フォントを使います。", "ASCII 영문이 포함된 텍스트는 게임 기본 글꼴을 사용합니다。"), Category("keep"));
            SetUi("keepDigits", L("显示原版数字", "Keep Original Digits", "Сохранять цифры", "数字を標準フォントで表示", "숫자 기본 글꼴 유지"),
                L("仅含 ASCII 且包含 0-9 的整段文本保留原版字体。", "Keep the original font for ASCII-only text containing 0-9.", "Сохраняет оригинальный шрифт для ASCII-текста с цифрами。", "ASCIIのみで数字を含むテキストは標準フォントを使います。", "ASCII 숫자가 포함된 텍스트는 게임 기본 글꼴을 사용합니다。"), Category("keep"));

            SetUi("excludeVolcano", L("排除 VolcanoSubtitle 全部界面", "Exclude all VolcanoSubtitle UI", "Исключить весь UI VolcanoSubtitle", "VolcanoSubtitle UIをすべて除外", "VolcanoSubtitle UI 전체 제외"),
                L("不修改字幕、弹幕、3D气泡、设置窗口、过滤面板和调试界面的字体。", "Do not change fonts in subtitles, danmaku, 3D bubbles, settings, filters, or debug panels.", "Не изменяет шрифты субтитров, панелей настроек, фильтров и отладки。", "字幕・弾幕・3D吹き出し・設定・フィルター・デバッグ画面を変更しません。", "자막, 탄막, 3D 말풍선, 설정, 필터 및 디버그 패널의 글꼴을 변경하지 않습니다。"), Category("exclude"));
            SetUi("excludeModGui", L("排除指定 Mod GUI", "Exclude selected mod GUIs", "Исключить GUI выбранных модов", "指定Mod GUIを除外", "지정 모드 GUI 제외"),
                L("按下方根节点和程序集/类型关键字排除其他 Mod 界面。", "Exclude other mod interfaces using the root names and assembly/type keywords below.", "Исключает интерфейсы других модов по именам корней и ключевым словам。", "下のルート名とアセンブリ/型キーワードで他ModのUIを除外します。", "아래 루트 이름과 어셈블리/형식 키워드로 다른 모드 UI를 제외합니다。"), Category("exclude"));
            SetUi("excludedRoots", L("额外排除的 GUI 根节点", "Excluded GUI Root Names", "Имена исключённых корней GUI", "除外するGUIルート名", "제외할 GUI 루트 이름"),
                L("使用分号、逗号或换行分隔；匹配文本对象任意祖先的完整名称。", "Separate with semicolons, commas, or new lines; exact-match any ancestor name.", "Разделяйте точкой с запятой, запятой или новой строкой。", "セミコロン・カンマ・改行で区切り、祖先名と完全一致します。", "세미콜론, 쉼표 또는 줄바꿈으로 구분하며 상위 개체 이름과 정확히 일치합니다。"), Category("exclude"));
            SetUi("excludedAssemblies", L("排除的程序集或类型关键字", "Excluded Assembly/Type Keywords", "Ключевые слова сборки/типа", "除外するアセンブリ/型キーワード", "제외할 어셈블리/형식 키워드"),
                L("使用分号、逗号或换行分隔；匹配祖先组件的程序集名或完整类型名。该检测开销较高，仅在开关启用时执行。", "Match ancestor component assembly names or full type names. This check runs only when enabled.", "Сопоставляет сборки или полные имена типов компонентов-предков。", "祖先コンポーネントのアセンブリ名または完全型名に一致します。有効時のみ検査します。", "상위 컴포넌트의 어셈블리 이름 또는 전체 형식 이름을 검사하며 활성화된 경우에만 실행합니다。"), Category("exclude"));

            if (localeChanged)
            {
                QueueConfigurationManagerRefresh();
            }
        }

        private void QueueConfigurationManagerRefresh()
        {
            if (_configManagerRefreshPending)
            {
                return;
            }

            _configManagerRefreshPending = true;
            StartCoroutine(RefreshConfigurationManagerAtEndOfFrame());
        }

        private IEnumerator RefreshConfigurationManagerAtEndOfFrame()
        {
            yield return WaitEndOfFrame;
            _configManagerRefreshPending = false;

            try
            {
                Type? managerType = Type.GetType("ConfigurationManager.ConfigurationManager, ConfigurationManager", false);
                if (managerType == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (string.Equals(assembly.GetName().Name, "ConfigurationManager", StringComparison.OrdinalIgnoreCase))
                        {
                            managerType = assembly.GetType("ConfigurationManager.ConfigurationManager", false);
                            break;
                        }
                    }
                }

                if (managerType == null)
                {
                    yield break;
                }

                var buildSettingList = managerType.GetMethod(
                    "BuildSettingList",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (buildSettingList == null)
                {
                    yield break;
                }

                var managers = Resources.FindObjectsOfTypeAll(managerType);
                for (int i = 0; i < managers.Length; i++)
                {
                    if (managers[i] != null)
                    {
                        buildSettingList.Invoke(managers[i], null);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogDebug("[FontReplace] 刷新 Configuration Manager 设置列表失败: " + e);
            }
        }

        private void SetUi(string id, string name, string description, string category)
        {
            if (_localizedUi.TryGetValue(id, out var attributes))
            {
                attributes.DispName = name;
                attributes.Description = description;
                attributes.Category = category;
            }
        }

        private string ProfileCategory(string localeKey)
        {
            string language = localeKey switch
            {
                ChineseLocaleKey => L("中文", "Chinese", "Китайский", "中国語", "중국어"),
                EnglishLocaleKey => L("英文", "English", "Английский", "英語", "영어"),
                RussianLocaleKey => L("俄文", "Russian", "Русский", "ロシア語", "러시아어"),
                JapaneseLocaleKey => L("日文", "Japanese", "Японский", "日本語", "일본어"),
                KoreanLocaleKey => L("韩文", "Korean", "Корейский", "韓国語", "한국어"),
                _ => localeKey
            };
            return "1. " + language + " " + L("字体", "Font", "шрифт", "フォント", "글꼴");
        }

        private string Category(string id)
        {
            return id switch
            {
                "mod" => "0. " + L("模组", "Mod", "Мод", "Mod", "모드"),
                "keep" => "2. " + L("原版字符", "Original Characters", "Оригинальные символы", "標準文字", "기본 문자"),
                "exclude" => "3. " + L("GUI 排除", "GUI Exclusions", "Исключения GUI", "GUI除外", "GUI 제외"),
                _ => id
            };
        }

        private string UiText(string id)
        {
            return id switch
            {
                "noFonts" => L("(无任何字体资源)", "(No font bundles)", "(Нет пакетов шрифтов)", "(フォントなし)", "(글꼴 번들 없음)"),
                "gameDefault" => L("(游戏原版字体)", "(Original game font)", "(Оригинальный шрифт игры)", "(ゲーム標準フォント)", "(게임 기본 글꼴)"),
                "refresh" => L("刷新", "Refresh", "Обновить", "更新", "새로고침"),
                "apply" => L("应用", "Apply", "Применить", "適用", "적용"),
                "refreshed" => L("已刷新到: {0} 个字体资源", "Found {0} font bundle(s)", "Найдено пакетов: {0}", "{0}個のフォントを検出", "글꼴 번들 {0}개 발견"),
                "saved" => L("已保存：", "Saved: ", "Сохранено: ", "保存しました: ", "저장됨: "),
                "loadFailed" => L("加载失败：", "Load failed: ", "Ошибка загрузки: ", "読み込み失敗: ", "불러오기 실패: "),
                _ => id
            };
        }

        private string L(string chinese, string english, string russian, string japanese, string korean)
        {
            return _uiLocaleKey switch
            {
                ChineseLocaleKey => chinese,
                RussianLocaleKey => russian,
                JapaneseLocaleKey => japanese,
                KoreanLocaleKey => korean,
                _ => english
            };
        }
    }
}
