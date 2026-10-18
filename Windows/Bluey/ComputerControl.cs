using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Bluey;

/// Clicking, typing, scrolling and launching on Windows. Coordinates are screen pixels.
internal static class ComputerControl
{
    private static readonly Dictionary<string, ushort> Named = new()
    {
        ["return"] = 0x0D, ["enter"] = 0x0D, ["escape"] = 0x1B, ["esc"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25,
        ["right"] = 0x27, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
    };
    private static readonly HashSet<ushort> Extended = new() { 0x26, 0x28, 0x25, 0x27, 0x24, 0x23, 0x21, 0x22, 0x2E };

    /// Shortcuts that lock the PC or open the security screen are left to the user.
    private static readonly HashSet<string> Refused = new() { "win+l", "ctrl+alt+delete", "ctrl+alt+del", "ctrl+shift+escape", "ctrl+shift+esc" };

    private static ushort? Modifier(string token) => token switch
    {
        "ctrl" or "control" or "cmd" or "command" => 0x11,
        "shift" => 0x10,
        "alt" or "option" => 0x12,
        "win" or "windows" or "super" or "meta" => 0x5B,
        _ => null,
    };

    public static void Warp(int x, int y) => Native.SetCursorPos(x, y);

    public static async Task Click(int x, int y, bool right, int count)
    {
        Native.SetCursorPos(x, y);
        await Task.Delay(40);
        for (int i = 0; i < count; i++)
        {
            Native.mouse_event(right ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(35);
            Native.mouse_event(right ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(60);
        }
    }

    public static async Task Scroll(int dx, int dy, int x, int y)
    {
        Native.SetCursorPos(x, y);
        await Task.Delay(30);
        if (dy != 0) Native.mouse_event(Native.MOUSEEVENTF_WHEEL, 0, 0, -dy, UIntPtr.Zero);   // down = negative wheel
        if (dx != 0) Native.mouse_event(Native.MOUSEEVENTF_HWHEEL, 0, 0, dx, UIntPtr.Zero);
    }

    public static void DragBegin(int x, int y)
    {
        Native.SetCursorPos(x, y);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    }
    public static void DragMove(int x, int y) => Native.SetCursorPos(x, y);
    public static void DragEnd(int x, int y)
    {
        Native.SetCursorPos(x, y);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    private static void Key(ushort vk, bool up)
    {
        uint flags = (up ? Native.KEYEVENTF_KEYUP : 0u) | (Extended.Contains(vk) ? Native.KEYEVENTF_EXTENDEDKEY : 0u);
        var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        input.u.ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = flags };
        Native.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    private static void Unicode(char c, bool up)
    {
        var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        input.u.ki = new Native.KEYBDINPUT { wScan = c, dwFlags = Native.KEYEVENTF_UNICODE | (up ? Native.KEYEVENTF_KEYUP : 0u) };
        Native.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    public static async Task Type(string text, Action<string> typed)
    {
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            if (c == '\n') { Key(0x0D, false); Key(0x0D, true); }
            else { Unicode(c, false); Unicode(c, true); }
            typed(c.ToString());
            await Task.Delay(8);
        }
    }

    /// Presses a shortcut like "ctrl+t" (cmd is treated as ctrl, since this is Windows). Returns a short label.
    public static string Press(string keys)
    {
        var tokens = keys.ToLowerInvariant().Replace(" ", "").Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new InvalidOperationException("No keys given.");
        var mods = new List<ushort>();
        ushort? main = null;
        foreach (var t in tokens)
        {
            var m = Modifier(t);
            if (m != null) { if (!mods.Contains(m.Value)) mods.Add(m.Value); continue; }
            if (Named.TryGetValue(t, out var vk)) main = vk;
            else if (t.Length >= 2 && t[0] == 'f' && int.TryParse(t.AsSpan(1), out var n) && n is >= 1 and <= 12) main = (ushort)(0x70 + n - 1);
            else if (t.Length == 1)
            {
                var scan = Native.VkKeyScan(t[0]);
                if (scan == -1) throw new InvalidOperationException($"I don't know the key \"{t}\".");
                main = (ushort)(scan & 0xFF);
            }
            else throw new InvalidOperationException($"I don't know the key \"{t}\".");
        }
        var normalized = string.Join("+", tokens);
        if (Refused.Contains(normalized)) throw new InvalidOperationException("REFUSED");
        foreach (var m in mods) Key(m, false);
        if (main != null) { Key(main.Value, false); Key(main.Value, true); }
        foreach (var m in Enumerable.Reverse(mods)) Key(m, true);
        return string.Join("+", tokens);
    }

    public static bool IsRefused(string keys)
    {
        var normalized = string.Join("+", keys.ToLowerInvariant().Replace(" ", "").Split('+', StringSplitOptions.RemoveEmptyEntries));
        return Refused.Contains(normalized);
    }

    public static async Task<string> OpenApp(string name)
    {
        try
        {
            Process.Start(new ProcessStartInfo(name) { UseShellExecute = true });
            return $"Opened {name}.";
        }
        catch (Win32Exception)
        {
            // Not a known program name: use the Start menu search instead.
            Key(0x5B, false); Key(0x5B, true);
            await Task.Delay(700);
            await Type(name, _ => { });
            await Task.Delay(700);
            Key(0x0D, false); Key(0x0D, true);
            return $"Searched the Start menu for {name} and pressed Enter.";
        }
    }

    public static string OpenUrl(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return "No address given.";
        if (!url.Contains("://")) url = "https://" + url;
        if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            return "I only open web addresses (http or https).";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return $"Opened {url}.";
        }
        catch (Exception ex) { return "Couldn't open it: " + ex.Message; }
    }

    /// Is the focused control a password box? (Bluey never types into those.)
    public static bool FocusedIsPassword()
    {
        try
        {
            var e = AutomationElement.FocusedElement;
            return e != null && (bool)e.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty);
        }
        catch { return false; }
    }
}
