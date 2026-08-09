using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FontReplace
{
    public partial class FontReplacePlugin : BaseUnityPlugin
    {
        private void SetupTextMonitoring()
        {
            if (_textMonitorPatched)
            {
                return;
            }

            try
            {
                if (_harmony == null)
                {
                    _harmony = new Harmony("hiddenhiragi.Volcano.fontreplace");
                }

                var tmpPostfix = new HarmonyMethod(AccessTools.Method(typeof(FontReplacePlugin), nameof(OnTmpTextDirty)));
                var uiPostfix = new HarmonyMethod(AccessTools.Method(typeof(FontReplacePlugin), nameof(OnUiTextDirty)));

                var patched = new HashSet<MethodInfo>();

                // TMP：text setter（TextMeshProUGUI/TextMeshPro 均未重写 setter，补基类一处即可覆盖全部 TMP 文本）
                PatchOnce(AccessTools.PropertySetter(typeof(TMP_Text), "text"), tmpPostfix, patched);

                // TMP：OnEnable（对象池复用 / 重新激活时兜底，替代旧轮询的“补漏”角色）
                PatchOnce(AccessTools.Method(typeof(TextMeshProUGUI), "OnEnable"), tmpPostfix, patched);
                PatchOnce(AccessTools.Method(typeof(TextMeshPro), "OnEnable"), tmpPostfix, patched);

                // UnityEngine.UI.Text：text setter + OnEnable
                PatchOnce(AccessTools.PropertySetter(typeof(Text), "text"), uiPostfix, patched);
                PatchOnce(AccessTools.Method(typeof(Text), "OnEnable"), uiPostfix, patched);

                _textMonitorPatched = patched.Count > 0;

                if (_textMonitorPatched)
                {
                    Logger.LogInfo("[FontReplace] 已通过 Harmony 监听文本变化（用于英文字母/数字保留原版），共 patch " + patched.Count + " 个方法。");
                }
                else
                {
                    Logger.LogWarning("[FontReplace] 未能 patch 任何文本方法，英文字母/数字保留原版将不会自动生效。");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[FontReplace] Harmony 监听文本变化失败: " + e);
            }
        }

        private void PatchOnce(MethodInfo target, HarmonyMethod postfix, HashSet<MethodInfo> patched)
        {
            if (target == null || postfix == null || patched.Contains(target))
            {
                return;
            }

            _harmony.Patch(target, postfix: postfix);
            patched.Add(target);
        }

        private void TeardownTextMonitoring()
        {
            try
            {
                if (_textMonitorPatched && _harmony != null)
                {
                    _harmony.UnpatchSelf();
                }
            }
            catch
            {
                // 忽略
            }
            finally
            {
                _textMonitorPatched = false;
            }

            _dirtyTmpTexts.Clear();
            _dirtyUiTexts.Clear();
            _handledTmpContent.Clear();
            _handledUiContent.Clear();
            _handledTmpKeepOriginal.Clear();
            _handledUiKeepOriginal.Clear();
        }

        // ===== Harmony postfix（必须为静态方法，通过 s_instance 转回插件实例）=====

        private static void OnTmpTextDirty(TMP_Text __instance)
        {
            var p = s_instance;
            if (p != null)
            {
                p.MarkTmpTextDirty(__instance);
            }
        }

        private static void OnUiTextDirty(Text __instance)
        {
            var p = s_instance;
            if (p != null)
            {
                p.MarkUiTextDirty(__instance);
            }
        }

        // ===== 脏标记 + 帧末批量处理（避免页面切换时单帧内大量同步重建导致卡顿）=====

        private bool ShouldMonitorTexts()
        {
            if (_modEnabled != null && !_modEnabled.Value)
            {
                return false;
            }

            if (!_isChineseLocaleActive || _chineseFontAsset == null)
            {
                return false;
            }

            if (_keepOriginalLatin == null || _keepOriginalDigits == null)
            {
                return false;
            }

            return _keepOriginalLatin.Value || _keepOriginalDigits.Value;
        }

        private void MarkTmpTextDirty(TMP_Text text)
        {
            if (!ShouldMonitorTexts())
            {
                return;
            }

            if (text != null && _dirtyTmpTexts.Add(text))
            {
                ScheduleDirtyFlush();
            }
        }

        private void MarkUiTextDirty(Text text)
        {
            if (!ShouldMonitorTexts())
            {
                return;
            }

            if (text != null && _dirtyUiTexts.Add(text))
            {
                ScheduleDirtyFlush();
            }
        }

        private void ScheduleDirtyFlush()
        {
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
            StartCoroutine(FlushDirtyTextsAtEndOfFrame());
        }

        private IEnumerator FlushDirtyTextsAtEndOfFrame()
        {
            // 同一帧内的所有文本变化合并为帧末一次处理
            yield return WaitEndOfFrame;
            _flushScheduled = false;

            if (!ShouldMonitorTexts())
            {
                _dirtyTmpTexts.Clear();
                _dirtyUiTexts.Clear();
                yield break;
            }

            if (_dirtyTmpTexts.Count > 0)
            {
                var snapshot = new List<TMP_Text>(_dirtyTmpTexts);
                _dirtyTmpTexts.Clear();

                for (int i = 0; i < snapshot.Count; i++)
                {
                    ProcessTmpText(snapshot[i]);
                }
            }

            if (_dirtyUiTexts.Count > 0)
            {
                var snapshot = new List<Text>(_dirtyUiTexts);
                _dirtyUiTexts.Clear();

                for (int i = 0; i < snapshot.Count; i++)
                {
                    ProcessUiText(snapshot[i]);
                }
            }
        }

        // ===== 字幕模组字体作用域豁免 =====

        // 字幕模组的预览、屏幕字幕/弹幕和世界气泡均自行管理字体。
        // FontReplace 不再覆盖这些节点，避免大量台词与游戏/F12 共用同一旧 UGUI 字体图集。
        private const string SubtitlePreviewRootName = "SubtitlePreviewPane";
        private const string SubtitleRuntimeRootName = "SubtitleRoot";
        private const string SubtitleWorld3DRootName = "World3DBubble";

        private static bool IsInSubtitleFontScope(Transform transform)
        {
            var t = transform;
            while (t != null)
            {
                if (string.Equals(t.name, SubtitlePreviewRootName, StringComparison.Ordinal) ||
                    string.Equals(t.name, SubtitleRuntimeRootName, StringComparison.Ordinal) ||
                    string.Equals(t.name, SubtitleWorld3DRootName, StringComparison.Ordinal))
                {
                    return true;
                }

                t = t.parent;
            }

            return false;
        }

        private void ProcessTmpText(TMP_Text text)
        {
            if (text == null)
            {
                return;
            }

            // 字幕预览面板内的文本不替换字体（也不写入判定缓存，保证每次处理都跳过）
            if (IsInSubtitleFontScope(text.transform))
            {
                return;
            }

            int id = text.GetInstanceID();
            string curr = text.text ?? string.Empty;

            // 内容未变化时跳过字符串扫描，直接复用上次的判定结果（仅校验字体是否仍然正确）
            bool keep;
            string last;
            if (!(_handledTmpContent.TryGetValue(id, out last) &&
                  string.Equals(last, curr, StringComparison.Ordinal) &&
                  _handledTmpKeepOriginal.TryGetValue(id, out keep)))
            {
                CacheOriginalFontIfNeeded(text);
                keep = ShouldKeepOriginalFont(curr);
                _handledTmpContent[id] = curr;
                _handledTmpKeepOriginal[id] = keep;
            }

            var targetFont = keep ? GetOriginalFont(text) : _chineseFontAsset;
            if (targetFont != null && text.font != targetFont)
            {
                text.font = targetFont;
                text.havePropertiesChanged = true;
            }
        }

        private void ProcessUiText(Text text)
        {
            if (text == null)
            {
                return;
            }

            // 字幕预览面板内的文本不替换字体（也不写入判定缓存，保证每次处理都跳过）
            if (IsInSubtitleFontScope(text.transform))
            {
                return;
            }

            int id = text.GetInstanceID();
            string curr = text.text ?? string.Empty;

            bool keep;
            string last;
            if (!(_handledUiContent.TryGetValue(id, out last) &&
                  string.Equals(last, curr, StringComparison.Ordinal) &&
                  _handledUiKeepOriginal.TryGetValue(id, out keep)))
            {
                CacheOriginalFontIfNeeded(text);
                keep = ShouldKeepOriginalFont(curr);
                _handledUiContent[id] = curr;
                _handledUiKeepOriginal[id] = keep;
            }

            var targetFont = keep ? GetOriginalFont(text) : _chineseUnityFont;
            if (targetFont != null && text.font != targetFont)
            {
                text.font = targetFont;
            }
        }

    }
}
