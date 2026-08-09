using LanSwitch.Windows.Input;

namespace LanSwitch.Agent.Infrastructure;

public static class HotkeyConfiguration
{
    public const string DefaultLocalHotkey = "Ctrl+Alt+Shift+F11";
    public const string DefaultToggleHotkey = "Ctrl+Alt+Shift+F12";
    public const string EmergencyHotkey = "Ctrl+Alt+Shift+Esc";

    private const ushort EscapeVirtualKey = 0x1B;

    public static HotkeySettingsView GetView(AgentSettings settings) => new(
        settings.LocalHotkey ?? DefaultLocalHotkey,
        settings.ToggleHotkey,
        EmergencyHotkey);

    public static HotkeyBindings ToBindings(AgentSettings settings)
    {
        var normalized = Normalize(settings);
        _ = TryParse(normalized.LocalHotkey!, out var local, out _);
        _ = TryParse(normalized.ToggleHotkey, out var toggle, out _);
        return new HotkeyBindings(
            local,
            toggle,
            new HotkeyGesture(EscapeVirtualKey, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift));
    }

    public static HotkeySettingsView ValidateUserBindings(string? switchToLocal, string? toggleRemote)
    {
        if (IsEmergencyShortcut(switchToLocal) || IsEmergencyShortcut(toggleRemote))
            throw new ArgumentException($"{EmergencyHotkey} 是不可修改的紧急回本机快捷键，不能重复使用。");
        if (!TryParse(switchToLocal, out var local, out var localError))
            throw new ArgumentException($"切到本机快捷键无效：{localError}");
        if (!TryParse(toggleRemote, out var toggle, out var toggleError))
            throw new ArgumentException($"切换目标快捷键无效：{toggleError}");
        if (local == toggle)
            throw new ArgumentException("切到本机和切换目标不能使用同一个快捷键。");

        var emergency = new HotkeyGesture(
            EscapeVirtualKey,
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
        if (local == emergency || toggle == emergency)
            throw new ArgumentException($"{EmergencyHotkey} 是不可修改的紧急回本机快捷键，不能重复使用。");

        return new HotkeySettingsView(Format(local), Format(toggle), EmergencyHotkey);
    }

    public static AgentSettings Normalize(AgentSettings settings)
    {
        var localText = settings.LocalHotkey;
        if (!TryParse(localText, out var local, out _))
        {
            localText = DefaultLocalHotkey;
            _ = TryParse(localText, out local, out _);
        }

        var toggleText = settings.ToggleHotkey;
        if (!TryParse(toggleText, out var toggle, out _) || toggle == local)
        {
            toggleText = DefaultToggleHotkey;
            _ = TryParse(toggleText, out toggle, out _);
        }

        if (toggle == local)
        {
            toggleText = "Ctrl+Alt+Shift+F10";
            _ = TryParse(toggleText, out toggle, out _);
        }

        return settings with
        {
            LocalHotkey = Format(local),
            ToggleHotkey = Format(toggle),
            EmergencyHotkey = EmergencyHotkey
        };
    }

    internal static bool TryParse(string? text, out HotkeyGesture gesture, out string error)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请按下至少两个修饰键和一个字母、数字或 F1-F24。";
            return false;
        }

        var modifiers = KeyModifiers.None;
        ushort virtualKey = 0;
        var keyCount = 0;
        foreach (var rawPart in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.ToUpperInvariant();
            switch (part)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= KeyModifiers.Control;
                    continue;
                case "ALT":
                    modifiers |= KeyModifiers.Alt;
                    continue;
                case "SHIFT":
                    modifiers |= KeyModifiers.Shift;
                    continue;
                case "WIN":
                case "WINDOWS":
                    error = "为避免覆盖 Windows 系统快捷键，不允许使用 Win 键。";
                    return false;
            }

            keyCount++;
            if (keyCount > 1 || !TryParseMainKey(part, out virtualKey))
            {
                error = "主键只支持 A-Z、0-9 或 F1-F24，且只能有一个。";
                return false;
            }
        }

        var modifierCount = (modifiers.HasFlag(KeyModifiers.Control) ? 1 : 0) +
                            (modifiers.HasFlag(KeyModifiers.Alt) ? 1 : 0) +
                            (modifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0);
        if (modifierCount < 2)
        {
            error = "至少需要 Ctrl、Alt、Shift 中的两个修饰键，避免误触和覆盖普通按键。";
            return false;
        }
        if (keyCount != 1)
        {
            error = "缺少主键；请选择 A-Z、0-9 或 F1-F24。";
            return false;
        }

        gesture = new HotkeyGesture(virtualKey, modifiers);
        error = string.Empty;
        return true;
    }

    internal static string Format(HotkeyGesture gesture)
    {
        var parts = new List<string>(4);
        if (gesture.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(FormatMainKey(gesture.VirtualKey));
        return string.Join('+', parts);
    }

    private static bool TryParseMainKey(string part, out ushort virtualKey)
    {
        virtualKey = 0;
        if (part.Length == 1 && part[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            virtualKey = part[0];
            return true;
        }
        if (part.Length >= 2 && part[0] == 'F' &&
            int.TryParse(part.AsSpan(1), out var functionKey) && functionKey is >= 1 and <= 24)
        {
            virtualKey = checked((ushort)(0x70 + functionKey - 1));
            return true;
        }
        return false;
    }

    private static bool IsEmergencyShortcut(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tokens = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static token => token.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "CTRL" : token.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
        return tokens.Count == 4 && tokens.Contains("CTRL") && tokens.Contains("ALT") &&
               tokens.Contains("SHIFT") && (tokens.Contains("ESC") || tokens.Contains("ESCAPE"));
    }

    private static string FormatMainKey(ushort virtualKey)
    {
        if (virtualKey is >= 0x70 and <= 0x87) return $"F{virtualKey - 0x70 + 1}";
        return ((char)virtualKey).ToString();
    }
}

public sealed record HotkeySettingsView(string SwitchToLocal, string ToggleRemote, string Emergency);
