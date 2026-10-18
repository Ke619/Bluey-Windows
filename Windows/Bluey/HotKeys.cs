using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Bluey;

/// Global hotkeys through a hidden message window.
internal sealed class HotKeys : NativeWindow, IDisposable
{
    private readonly Dictionary<int, Action> _actions = new();
    private int _next = 1;

    public HotKeys() => CreateHandle(new CreateParams());

    public void Register(uint modifiers, uint vk, Action action)
    {
        var id = _next++;
        if (Native.RegisterHotKey(Handle, id, modifiers | Native.MOD_NOREPEAT, vk)) _actions[id] = action;
        else Log.Write($"Couldn't register hotkey {modifiers}+{vk} (another app may be using it).");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var action))
        {
            try { action(); } catch (Exception ex) { Log.Write("Hotkey error: " + ex); }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(Handle, id);
        DestroyHandle();
    }
}

/// A tiny text-entry dialog (WinForms has no built-in one).
internal static class Prompt
{
    public static string? Ask(string title, string label, string initial, bool secret)
    {
        using var form = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ClientSize = new System.Drawing.Size(460, 130), TopMost = true,
        };
        var text = new Label { Left = 14, Top = 12, Width = 432, Height = 36, Text = label };
        var box = new TextBox { Left = 14, Top = 54, Width = 432, Text = initial, UseSystemPasswordChar = secret };
        var ok = new Button { Text = "OK", Left = 290, Top = 90, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 371, Top = 90, Width = 75, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { text, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
