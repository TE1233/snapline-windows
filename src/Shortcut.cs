using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace Snapline
{
    sealed class ShortcutSpec
    {
        internal readonly uint Modifiers, Key;
        internal ShortcutSpec(uint modifiers, uint key) { Modifiers = modifiers; Key = key; }
        internal static ShortcutSpec From(Preferences settings)
        {
            if (settings.CaptureShortcut == "None") return null;
            if (settings.CaptureShortcut == "Custom") return new ShortcutSpec(settings.CustomCaptureModifiers, settings.CustomCaptureKey);
            if (settings.CaptureShortcut == "CtrlPrintScreen") return new ShortcutSpec(2, 0x2C);
            if (settings.CaptureShortcut == "ShiftPrintScreen") return new ShortcutSpec(4, 0x2C);
            return new ShortcutSpec(2, 0x42);
        }
        internal static string Validate(uint modifiers, uint key)
        {
            if ((modifiers & 8) != 0) return "Win 组合键由 Windows 使用，请改用 Ctrl、Shift 或 Alt。";
            if (modifiers == 0 || (modifiers & ~7u) != 0) return "请加上 Ctrl、Shift 或 Alt，避免影响正常输入。";
            if (key == 0x7B) return "F12 是系统调试保留键，请换一个按键。";
            if (modifiers == 3 && key == 0x54) return "Ctrl + Alt + T 已用于展开晾台，请换一组。";
            if (modifiers == 1 && key == 0x73) return "Alt + F4 用于关闭窗口，请换一组。";
            bool supported = (key >= 0x41 && key <= 0x5A) || (key >= 0x30 && key <= 0x39) ||
                (key >= 0x60 && key <= 0x6F) || (key >= 0x70 && key <= 0x87) || key == 0x2C ||
                (key >= 0xBA && key <= 0xC0) || (key >= 0xDB && key <= 0xDF) || key == 0xE2;
            return supported ? null : "请选择字母、数字、符号、功能键或 PrtSc。";
        }
        internal static string ModifierLabel(uint modifiers)
        {
            var parts = new List<string>();
            if ((modifiers & 2) != 0) parts.Add("Ctrl");
            if ((modifiers & 1) != 0) parts.Add("Alt");
            if ((modifiers & 4) != 0) parts.Add("Shift");
            if ((modifiers & 8) != 0) parts.Add("Win");
            return String.Join(" + ", parts);
        }
        internal string Label
        {
            get
            {
                string name;
                switch (Key)
                {
                    case 0x2C: name = "PrtSc"; break;
                    case 0xBA: name = ";"; break; case 0xBB: name = "="; break;
                    case 0xBC: name = ","; break; case 0xBD: name = "-"; break;
                    case 0xBE: name = "."; break; case 0xBF: name = "/"; break;
                    case 0xC0: name = "`"; break; case 0xDB: name = "["; break;
                    case 0xDC: name = "\\"; break; case 0xDD: name = "]"; break;
                    case 0xDE: name = "'"; break;
                    default:
                        name = Key >= 0x30 && Key <= 0x39 ? ((char)Key).ToString() : ((Forms.Keys)Key).ToString();
                        break;
                }
                return ModifierLabel(Modifiers) + " + " + name;
            }
        }
        internal string Warning
        {
            get
            {
                if (Modifiers == 2 && "ABCFNOPRSTUVWXYZ".Contains((char)Key))
                    return "这组键也常用于软件操作，启用后会优先用于截图。";
                if (Modifiers == 6 && "BCIJNTV".Contains((char)Key))
                    return "这组键在部分软件中已有用途，启用后会优先用于截图。";
                return "只收集这组键发起的晾台截图，其他软件截图不会加入。";
            }
        }
        internal static bool IsModifier(Key key)
        {
            return key == System.Windows.Input.Key.LeftCtrl || key == System.Windows.Input.Key.RightCtrl ||
                key == System.Windows.Input.Key.LeftAlt || key == System.Windows.Input.Key.RightAlt ||
                key == System.Windows.Input.Key.LeftShift || key == System.Windows.Input.Key.RightShift ||
                key == System.Windows.Input.Key.LWin || key == System.Windows.Input.Key.RWin;
        }
        internal static uint ModifiersFrom(ModifierKeys modifiers)
        {
            return ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0u) |
                ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0u) |
                ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0u) |
                ((modifiers & ModifierKeys.Windows) != 0 ? 8u : 0u);
        }
    }

    sealed partial class Shelf
    {
        int captureHotkeyId = 2;
        uint captureTriggerKey;
        bool editingShortcut;
        internal bool ShortcutCollects { get { return store.Settings.CaptureShortcut == "CtrlB" || store.Settings.CaptureShortcut == "Custom"; } }
        internal int CaptureHotkeyId { get { return captureHotkeyId; } }

        internal bool TrySelectCaptureShortcut(string mode, uint modifiers, uint key, out string error)
        {
            error = null;
            if (mode != "CtrlB" && mode != "CtrlPrintScreen" && mode != "ShiftPrintScreen" && mode != "None" && mode != "Custom")
            { error = "未知快捷键选项。"; return false; }
            if (mode == "Custom" && (error = ShortcutSpec.Validate(modifiers, key)) != null) return false;
            string oldMode = store.Settings.CaptureShortcut;
            uint oldModifiers = store.Settings.CustomCaptureModifiers, oldKey = store.Settings.CustomCaptureKey;
            var oldSpec = ShortcutSpec.From(store.Settings);
            var next = mode == "None" ? null : mode == "Custom" ? new ShortcutSpec(modifiers, key) :
                mode == "CtrlB" ? new ShortcutSpec(2, 0x42) : new ShortcutSpec(mode == "ShiftPrintScreen" ? 4u : 2u, 0x2C);
            bool sameRegistration = CaptureHotkeyRegistered && oldSpec != null && next != null &&
                oldSpec.Modifiers == next.Modifiers && oldSpec.Key == next.Key;
            int nextId = captureHotkeyId == 2 ? 3 : 2;
            bool staged = false;
            if (next != null && !sameRegistration)
            {
                if (!Native.RegisterHotKey(handle, nextId, next.Modifiers | 0x4000u, next.Key))
                { error = next.Label + " 无法启用，可能已被其他程序占用。请换一组。"; return false; }
                staged = true;
            }
            store.Settings.CaptureShortcut = mode;
            if (mode == "Custom") { store.Settings.CustomCaptureModifiers = modifiers; store.Settings.CustomCaptureKey = key; }
            try { store.SaveSettings(); }
            catch (Exception ex)
            {
                store.Settings.CaptureShortcut = oldMode;
                store.Settings.CustomCaptureModifiers = oldModifiers; store.Settings.CustomCaptureKey = oldKey;
                if (staged) Native.UnregisterHotKey(handle, nextId);
                store.Log("Save capture shortcut", ex);
                error = "设置保存失败，原快捷键已保留。请重试。"; return false;
            }
            if (!sameRegistration)
            {
                Native.UnregisterHotKey(handle, captureHotkeyId);
                if (staged) captureHotkeyId = nextId;
                CaptureHotkeyRegistered = staged;
            }
            UpdateShortcutMenu(); Render();
            return true;
        }
        internal void EditCaptureShortcut()
        {
            if (dialogOpen || capturePending || shuttingDown) return;
            dialogOpen = true; editingShortcut = true;
            CloseImagePreview(); CloseNotePreview();
            if (helpTip != null) helpTip.IsOpen = false;
            // Let this application's current hotkey reach the recorder instead of starting capture.
            Native.UnregisterHotKey(handle, captureHotkeyId); CaptureHotkeyRegistered = false;
            try
            {
                ShortcutSpec candidate = ShortcutSpec.Validate(store.Settings.CustomCaptureModifiers, store.Settings.CustomCaptureKey) == null ?
                    new ShortcutSpec(store.Settings.CustomCaptureModifiers, store.Settings.CustomCaptureKey) : ShortcutSpec.From(store.Settings);
                var editor = new ShortcutEditor(candidate, delegate(ShortcutSpec choice)
                {
                    string error;
                    return TrySelectCaptureShortcut("Custom", choice.Modifiers, choice.Key, out error) ? null : error;
                });
                editor.Owner = this;
                editor.ShowDialog();
            }
            finally
            {
                editingShortcut = false; dialogOpen = false;
                if (!CaptureHotkeyRegistered) RegisterCaptureShortcut();
                UpdateShortcutMenu(); Render();
                keepUntil = DateTime.Now.AddSeconds(3);
            }
        }
    }
}
