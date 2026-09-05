using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
#if GAME_4_1
using LocalizationManager = EFT.LocalizationManager;
#else
using LocalizationManager = LocaleManagerClass;
#endif

namespace FontReplace
{
    public partial class FontReplacePlugin
    {
        private static class LocaleManagerCompat
        {
            private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private static readonly BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            private static PropertyInfo? s_singletonProperty;
            private static FieldInfo? s_singletonField;
            private static MemberInfo? s_currentLanguageMember;
            private static MemberInfo? s_fontMapMember;

            public static LocalizationManager? GetInstance(ManualLogSource logger)
            {
                try
                {
                    var type = typeof(LocalizationManager);
                    if (s_singletonProperty == null)
                    {
                        foreach (var name in new[] { "Instance", "LocalizationManager", "LocaleManagerClass" })
                        {
                            var property = type.GetProperty(name, AnyStatic);
                            if (property != null && property.PropertyType == type && property.GetIndexParameters().Length == 0)
                            {
                                s_singletonProperty = property;
                                break;
                            }
                        }

                        if (s_singletonProperty == null)
                        {
                            foreach (var property in type.GetProperties(AnyStatic))
                            {
                                if (property.PropertyType == type && property.GetIndexParameters().Length == 0)
                                {
                                    s_singletonProperty = property;
                                    break;
                                }
                            }
                        }
                    }

                    if (s_singletonProperty?.GetValue(null, null) is LocalizationManager propertyValue)
                    {
                        return propertyValue;
                    }

                    if (s_singletonField == null)
                    {
                        foreach (var field in type.GetFields(AnyStatic))
                        {
                            if (field.FieldType == type)
                            {
                                s_singletonField = field;
                                break;
                            }
                        }
                    }

                    return s_singletonField?.GetValue(null) as LocalizationManager;
                }
                catch (Exception e)
                {
                    logger.LogWarning("[FontReplace] 获取 LocaleManager 实例失败: " + e);
                    return null;
                }
            }

            public static string GetCurrentLanguage(LocalizationManager localeManager)
            {
                if (TryGetNamedString(
                    localeManager,
                    ref s_currentLanguageMember,
                    new[] { "Culture", "String_0", "CurrentLanguage", "Language", "Locale", "_culture", "string_2" },
                    out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                return EnglishLocaleKey;
            }

            public static TMP_FontAsset? TryGetLocaleFont(LocalizationManager localeManager, string locale)
            {
                var map = GetLocaleFontMap(localeManager);
                return map != null && map.TryGetValue(locale, out var font) ? font : null;
            }

            public static void TrySetLocaleFont(
                LocalizationManager localeManager,
                string locale,
                TMP_FontAsset font,
                ManualLogSource logger)
            {
                var map = GetLocaleFontMap(localeManager);
                if (map == null)
                {
                    logger.LogWarning("[FontReplace] 未找到本地化字体映射；仅应用 TMP/UGUI 文本覆盖。");
                    return;
                }

                map[locale] = font;
            }

            public static void TryRemoveLocaleFont(LocalizationManager localeManager, string locale, ManualLogSource logger)
            {
                var map = GetLocaleFontMap(localeManager);
                if (map == null)
                {
                    logger.LogDebug("[FontReplace] 无法移除本地化字体映射: " + locale);
                    return;
                }

                map.Remove(locale);
            }

            public static void TryApplyLocaleInternal(LocalizationManager localeManager, string locale, ManualLogSource logger)
            {
                try
                {
                    var type = localeManager.GetType();

                    // 4.1 的明确入口。必须优先于 UpdateApplicationLanguage，后者会在语言未变化时提前返回。
                    var updateFonts = type.GetMethod("UpdateFonts", AnyInstance, null, new[] { typeof(string) }, null);
                    if (updateFonts != null)
                    {
                        updateFonts.Invoke(localeManager, new object[] { locale });
                        return;
                    }

                    // 3.11/4.0 的明确入口。
                    var legacyUpdateFonts = type.GetMethod("method_1", AnyInstance, null, new[] { typeof(string) }, null);
                    if (legacyUpdateFonts != null)
                    {
                        legacyUpdateFonts.Invoke(localeManager, new object[] { locale });
                        return;
                    }

                    var updateLanguage = type.GetMethod("UpdateApplicationLanguage", AnyInstance, null, Type.EmptyTypes, null);
                    if (updateLanguage != null)
                    {
                        updateLanguage.Invoke(localeManager, null);
                        return;
                    }

                    logger.LogWarning("[FontReplace] 当前游戏版本没有可识别的字体 fallback 更新入口。");
                }
                catch (Exception e)
                {
                    logger.LogWarning("[FontReplace] 更新本地化字体 fallback 失败: " + e);
                }
            }

            public static Action? TrySubscribeLocaleUpdate(
                LocalizationManager localeManager,
                Action callback,
                ManualLogSource logger)
            {
                try
                {
                    var method = localeManager.GetType().GetMethod(
                        "AddLocaleUpdateListener",
                        AnyInstance,
                        null,
                        new[] { typeof(Action) },
                        null);
                    return method?.Invoke(localeManager, new object[] { callback }) as Action;
                }
                catch (Exception e)
                {
                    logger.LogWarning("[FontReplace] 订阅语言更新事件失败: " + e);
                    return null;
                }
            }

            private static IDictionary<string, TMP_FontAsset>? GetLocaleFontMap(LocalizationManager localeManager)
            {
                if (s_fontMapMember == null)
                {
                    var type = localeManager.GetType();
                    foreach (var name in new[] { "_languageSpecificFallBacks", "Dictionary_1", "dictionary_1" })
                    {
                        var field = type.GetField(name, AnyInstance);
                        if (field != null && typeof(IDictionary<string, TMP_FontAsset>).IsAssignableFrom(field.FieldType))
                        {
                            s_fontMapMember = field;
                            break;
                        }

                        var property = type.GetProperty(name, AnyInstance);
                        if (property != null && property.CanRead && property.GetIndexParameters().Length == 0 &&
                            typeof(IDictionary<string, TMP_FontAsset>).IsAssignableFrom(property.PropertyType))
                        {
                            s_fontMapMember = property;
                            break;
                        }
                    }

                    if (s_fontMapMember == null)
                    {
                        foreach (var field in type.GetFields(AnyInstance))
                        {
                            if (typeof(IDictionary<string, TMP_FontAsset>).IsAssignableFrom(field.FieldType))
                            {
                                s_fontMapMember = field;
                                break;
                            }
                        }
                    }
                }

                if (s_fontMapMember is FieldInfo fieldInfo)
                {
                    return fieldInfo.GetValue(localeManager) as IDictionary<string, TMP_FontAsset>;
                }

                if (s_fontMapMember is PropertyInfo propertyInfo)
                {
                    return propertyInfo.GetValue(localeManager, null) as IDictionary<string, TMP_FontAsset>;
                }

                return null;
            }

            private static bool TryGetNamedString(
                object instance,
                ref MemberInfo? cachedMember,
                string[] names,
                out string value)
            {
                value = string.Empty;
                try
                {
                    var type = instance.GetType();
                    if (cachedMember == null)
                    {
                        for (int i = 0; i < names.Length; i++)
                        {
                            var property = type.GetProperty(names[i], AnyInstance);
                            if (property != null && property.PropertyType == typeof(string) &&
                                property.CanRead && property.GetIndexParameters().Length == 0)
                            {
                                cachedMember = property;
                                break;
                            }

                            var field = type.GetField(names[i], AnyInstance);
                            if (field != null && field.FieldType == typeof(string))
                            {
                                cachedMember = field;
                                break;
                            }
                        }
                    }

                    if (cachedMember is PropertyInfo propertyInfo)
                    {
                        value = propertyInfo.GetValue(instance, null) as string ?? string.Empty;
                        return true;
                    }

                    if (cachedMember is FieldInfo fieldInfo)
                    {
                        value = fieldInfo.GetValue(instance) as string ?? string.Empty;
                        return true;
                    }
                }
                catch
                {
                    // 调用方会使用英文作为安全回退。
                }

                return false;
            }
        }
    }
}
