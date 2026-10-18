using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace Bluey;

/// Ties everything together: the tray icon, the phone connection, the overlay, Gemini and the tools.
internal sealed class BlueyHost : IDisposable
{
    private readonly System.Windows.Application _app;
    private readonly Settings _settings = Settings.Load();
    private readonly PhoneServer _server = new();
    private Overlay _overlay = null!;
    private GeminiAgent _gemini = null!;
    private System.Windows.Forms.NotifyIcon _tray = null!;
    private System.Windows.Forms.ToolStripMenuItem _statusItem = null!;
    private HotKeys _hotkeys = null!;
    private DispatcherTimer _faceTimer = null!;

    private ScreenSnapshot? _snapshot;
    private bool _awake, _thinking, _hidden;
    private DateTime _stoppedUntil = DateTime.MinValue;
    private DateTime _talkingUntil = DateTime.MinValue;
    private DateTime _lastActivity = DateTime.UtcNow;
    private CancellationTokenSource? _hold;
    private readonly Queue<(WpfPoint Spot, WpfRect Thing)> _points = new();
    private bool _pumping;

    public BlueyHost(System.Windows.Application app) { _app = app; }

    // MARK: Start and stop

    public void Start()
    {
        _overlay = new Overlay();
        _overlay.Show();
        _gemini = new GeminiAgent(_settings, RunToolAsync);

        _server.PacketReceived += (client, packet) => _app.Dispatcher.InvokeAsync(() => OnPacket(client, packet));
        _server.PhonesChanged += _ => _app.Dispatcher.InvokeAsync(RefreshStatus);
        _server.Start();

        BuildTray();
        _hotkeys = new HotKeys();
        _hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x53, StopActions);                       // Ctrl+Alt+S
        _hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x48, ToggleHidden);                      // Ctrl+Alt+H
        _hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x50, PointAtMouse);                      // Ctrl+Alt+P
        _hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x20, () => _server.Broadcast(new Packet { Command = _awake ? "sleep" : "wake" })); // Ctrl+Alt+Space

        _faceTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _faceTimer.Tick += (_, _) => Tick();
        _faceTimer.Start();

        RefreshStatus();
        if (string.IsNullOrEmpty(_settings.ApiKey))
            _tray.ShowBalloonTip(8000, "Bluey is running", "Right-click my tray icon and choose “Set Gemini key…” to get started.", System.Windows.Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _faceTimer?.Stop();
        _hotkeys?.Dispose();
        _server.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
    }

    // MARK: Phone

    private void OnPacket(PhoneClient client, Packet p)
    {
        switch (p.Command)
        {
            case "awake": SetAwake(true); break;
            case "asleep": SetAwake(false); break;
            case "caption": case "captionDone": break;
            case "ask": _ = HandleAsk(client, p); break;
            case "notes": client.Send(SaveNotes(p)); break;
            case "transcribe": client.Send(new Packet { Command = "transcribed", CallID = p.CallID }); break;
            default:
                if (p.CallID != null) client.Send(new Packet { Command = "ok", CallID = p.CallID });
                break;
        }
    }

    private void SetAwake(bool on)
    {
        _awake = on;
        if (!on)
        {
            _points.Clear();
            _hold?.Cancel();
            _overlay.SetCaption(null);
            _overlay.Home();
            _gemini.ForgetEverything();
        }
    }

    private static Packet SaveNotes(Packet p)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bluey Notes");
            Directory.CreateDirectory(folder);
            var id = p.Text != null ? JsonNode.Parse(p.Text)?["id"]?.ToString() : null;
            var name = id ?? DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
            File.WriteAllText(Path.Combine(folder, $"session-{name}.json"), p.Text ?? "{}");
            return new Packet { Command = "notes", CallID = p.CallID, Text = folder };
        }
        catch { return new Packet { Command = "notes", CallID = p.CallID }; }
    }

    private async Task HandleAsk(PhoneClient client, Packet p)
    {
        void Finish(string heard, string reply, bool sleep)
        {
            var json = new JsonObject { ["heard"] = heard, ["reply"] = reply, ["sleep"] = sleep };
            client.Send(new Packet { Command = "askResult", CallID = p.CallID, Text = json.ToJsonString() });
        }

        if (string.IsNullOrEmpty(_settings.ApiKey))
        {
            ShowReply("I need a Gemini key. Right-click my tray icon.");
            Finish("", "", false);
            return;
        }
        if (_thinking) { Finish("", "", false); return; }

        _thinking = true;
        _overlay.SetCaption(null);
        _lastActivity = DateTime.UtcNow;
        try
        {
            var wav = Convert.FromBase64String(p.Audio ?? "");
            var answer = await _gemini.AskAsync(wav);
            if (!string.IsNullOrWhiteSpace(answer.Reply)) ShowReply(answer.Reply);
            else TidyAfter(0.5);
            if (answer.Sleep) SetAwakeAfterReply();
            Finish(answer.Heard, answer.Reply, answer.Sleep);
        }
        catch (Exception ex)
        {
            Log.Write("Ask failed: " + ex);
            var message = ex.Message switch
            {
                "NOKEY" => "I need a Gemini key. Right-click my tray icon.",
                "RATELIMIT" => "Google says I've used my free requests for now. Try again in a minute.",
                _ when ex is TaskCanceledException => "Gemini took too long. Try again.",
                _ when ex is System.Net.Http.HttpRequestException => "I can't reach Gemini. Is the internet on?",
                _ => ex.Message,
            };
            ShowReply(message);
            Finish("", "", false);
        }
        finally { _thinking = false; }
    }

    private async void SetAwakeAfterReply()
    {
        await Task.Delay(1500);
        _awake = false;
    }

    // MARK: Bubble and pointing

    private void ShowReply(string text)
    {
        _overlay.SetCaption(text);
        _lastActivity = DateTime.UtcNow;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var readTime = Math.Min(Math.Max(2.8, 1.2 + words * 0.32), 12) + 3;
        _talkingUntil = DateTime.UtcNow.AddSeconds(Math.Min(readTime, 4));
        TidyAfter(readTime);
    }

    /// Keeps the bubble (and his pointing) up long enough to read, then tidies away.
    private async void TidyAfter(double seconds)
    {
        _hold?.Cancel();
        var cts = _hold = new CancellationTokenSource();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
            while (_pumping || _points.Count > 0) await Task.Delay(200, cts.Token);
            _overlay.SetCaption(null);
            _overlay.Home();
        }
        catch (TaskCanceledException) { }
    }

    private void QueuePoint(WpfPoint spot, WpfRect thing)
    {
        _points.Enqueue((spot, thing));
        _lastActivity = DateTime.UtcNow;
        if (!_pumping) _ = Pump();
    }

    private async Task Pump()
    {
        _pumping = true;
        try
        {
            while (_points.Count > 0)
            {
                var (spot, thing) = _points.Dequeue();
                _overlay.PointAt(spot, thing);
                if (_points.Count > 0) await Task.Delay(2600);
            }
        }
        finally { _pumping = false; }
    }

    private void PointAtMouse()
    {
        var (x, y) = Native.Mouse();
        QueuePoint(new WpfPoint(x, y), new WpfRect(x - 16, y - 16, 32, 32));
        TidyAfter(4);
    }

    private void StopActions()
    {
        _stoppedUntil = DateTime.UtcNow.AddSeconds(6);
        _points.Clear();
        _overlay.Home();
        _overlay.Flash("Stopped", 1.6);
    }

    private void ToggleHidden()
    {
        _hidden = !_hidden;
        _overlay.Visibility = _hidden ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    // MARK: Face

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (_overlay.IsPointing && !_pumping && !_thinking && now - _lastActivity > TimeSpan.FromSeconds(10)) _overlay.Home();

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        double lookX, lookY;
        if (_overlay.IsPointing) { lookX = _overlay.Target.X; lookY = _overlay.Target.Y; }
        else { var m = Native.Mouse(); lookX = m.X; lookY = m.Y; }

        var talking = now < _talkingUntil;
        var face = new FaceState
        {
            GazeX = Math.Clamp((lookX - bounds.Width / 2.0) / (bounds.Width / 2.0), -1, 1),
            // The phone sits below the screen, so he looks up toward things higher on the screen.
            GazeY = Math.Clamp(-(1 - lookY / bounds.Height), -1, 1),
            Mood = _thinking ? "thinking" : talking ? "talking" : _overlay.IsPointing ? "pointing" : "listening",
            Talk = talking ? 0.35 + 0.35 * Math.Abs(Math.Sin(now.Ticks / 1e7 * 13)) : 0,
        };
        _server.Send(face);
    }

    // MARK: Tools

    private static string Str(JsonObject a, string key) => a[key]?.ToString()?.Trim() ?? "";

    private static double? Num(JsonObject a, string key)
    {
        var n = a[key];
        if (n == null) return null;
        try { return n.GetValue<double>(); } catch { }
        return double.TryParse(n.ToString(), out var d) ? d : null;
    }

    private static bool Flag(JsonObject a, string key)
    {
        try { return a[key]?.GetValue<bool>() ?? false; } catch { return a[key]?.ToString()?.ToLower() == "true"; }
    }

    private (int W, int H) ScreenSize => _snapshot != null ? (_snapshot.Width, _snapshot.Height)
        : (System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width, System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Height);

    private WpfPoint GridPoint(double x, double y)
    {
        var (w, h) = ScreenSize;
        return new WpfPoint(Math.Clamp(x, 0, 1000) / 1000 * w, Math.Clamp(y, 0, 1000) / 1000 * h);
    }

    private (WpfPoint Point, string Name)? Spot(JsonObject a, string idKey, string xKey, string yKey)
    {
        var id = Str(a, idKey);
        if (id.Length > 0 && _snapshot?.Find(id) is { } t)
            return (new WpfPoint(t.Rect.X + t.Rect.Width / 2, t.Rect.Y + t.Rect.Height / 2), t.Text);
        if (Num(a, xKey) is { } x && Num(a, yKey) is { } y) return (GridPoint(x, y), "that spot");
        return null;
    }

    private static readonly HashSet<string> ActionNames = new() { "click", "type_text", "press_keys", "scroll", "drag", "open_app", "open_url" };

    private async Task<ToolResult> RunToolAsync(string name, JsonObject a)
    {
        _lastActivity = DateTime.UtcNow;
        if (ActionNames.Contains(name))
        {
            if (!_settings.ComputerControl) return new ToolResult("The user turned off computer control. You can still look and point.");
            if (DateTime.UtcNow < _stoppedUntil) return new ToolResult("The user pressed stop. Don't do anything else until they ask again.");
            return await RunAction(name, a);
        }
        switch (name)
        {
            case "look_at_screen":
                return await Look(null);
            case "point_at":
            {
                var id = Str(a, "target_id");
                if (_snapshot == null) return new ToolResult("Call look_at_screen first.");
                var t = _snapshot.Find(id);
                if (t == null) return new ToolResult($"No target {id}. Use an id from the last look_at_screen.");
                QueuePoint(new WpfPoint(t.Rect.X + t.Rect.Width / 2, t.Rect.Bottom + 3), t.Rect);
                return new ToolResult($"Pointing at \"{t.Text}\".");
            }
            case "point_at_spot":
            {
                if (Num(a, "x") is not { } x || Num(a, "y") is not { } y) return new ToolResult("Give x and y.");
                var spot = GridPoint(x, y);
                QueuePoint(spot, new WpfRect(spot.X - 16, spot.Y - 16, 32, 32));
                return new ToolResult("Pointing there.");
            }
            case "stop_pointing":
                TidyAfter(1.5);
                return new ToolResult("Your cursor will head home once you finish talking.");
            case "go_to_sleep":
                return new ToolResult("Going to sleep. Say a very short goodbye.");
            default:
                return new ToolResult($"Unknown tool {name}.");
        }
    }

    private async Task<ToolResult> Look(string? prefix)
    {
        try
        {
            var shot = await ScreenReader.SnapshotAsync();
            _snapshot = shot;
            var (mx, my) = Native.Mouse();
            var text = shot.ListText;
            text += $"\nThe user's mouse pointer is at @{mx * 1000 / Math.Max(shot.Width, 1)},{my * 1000 / Math.Max(shot.Height, 1)}";
            if (shot.Near(mx, my) is { } under) text += $", on {under.Id} \"{under.Text}\"";
            text += ". When they say \"this\", \"that\" or \"here\", they mean what's at their mouse pointer.";
            if (prefix != null) text = prefix + "\nHere's the screen now (ids have changed):\n" + text;
            return new ToolResult(text, Convert.ToBase64String(shot.Jpeg));
        }
        catch (Exception ex)
        {
            Log.Write("Look failed: " + ex);
            return new ToolResult((prefix != null ? prefix + " " : "") + "I couldn't capture the screen: " + ex.Message);
        }
    }

    private async Task<ToolResult> RunAction(string name, JsonObject a)
    {
        _points.Clear();
        _hold?.Cancel();
        try
        {
            switch (name)
            {
                case "click":
                {
                    var target = Spot(a, "target_id", "x", "y");
                    if (target == null) return new ToolResult("Tell me what to click: a target id from look_at_screen, or x and y.");
                    var right = Flag(a, "right");
                    var count = Flag(a, "double") ? 2 : 1;
                    var saved = Native.Mouse();
                    await _overlay.FlyTo(target.Value.Point);
                    _overlay.ClickEffect(target.Value.Point);
                    await Task.Delay(90);
                    await ComputerControl.Click((int)target.Value.Point.X, (int)target.Value.Point.Y, right, count);
                    ComputerControl.Warp(saved.X, saved.Y);   // hand your pointer back where you left it
                    await Task.Delay(400);
                    var verb = right ? "Right-clicked" : count == 2 ? "Double-clicked" : "Clicked";
                    return await Look($"{verb} {(target.Value.Name == "that spot" ? "there" : $"\"{target.Value.Name}\"")}.");
                }
                case "type_text":
                {
                    var text = a["text"]?.ToString() ?? "";
                    if (text.Length == 0) return new ToolResult("Nothing to type.");
                    if (ComputerControl.FocusedIsPassword()) return new ToolResult("That's a password field. Ask the user to type it themselves.");
                    await ComputerControl.Type(text, _ => { });
                    if (Flag(a, "press_return"))
                    {
                        await Task.Delay(120);
                        ComputerControl.Press("enter");
                        _overlay.Flash("↩", 1);
                        await Task.Delay(600);
                        return await Look("Typed it and pressed Enter.");
                    }
                    return new ToolResult("Typed it.");
                }
                case "press_keys":
                {
                    var keys = Str(a, "keys");
                    if (ComputerControl.IsRefused(keys)) return new ToolResult("I won't press that one (it locks the PC or opens the security screen). Ask the user to do it.");
                    var label = ComputerControl.Press(keys);
                    _overlay.Flash(label, 1.2);
                    await Task.Delay(400);
                    return await Look($"Pressed {keys}.");
                }
                case "scroll":
                {
                    var direction = Str(a, "direction").ToLowerInvariant();
                    var amount = Math.Clamp(Num(a, "amount") ?? 3, 1, 10);
                    var (w, h) = ScreenSize;
                    var point = Spot(a, "target_id", "x", "y")?.Point ?? new WpfPoint(w / 2.0, h / 2.0);
                    var pixels = (int)(amount * 120);
                    var (dx, dy) = direction switch { "up" => (0, -pixels), "left" => (-pixels, 0), "right" => (pixels, 0), _ => (0, pixels) };
                    var saved = Native.Mouse();
                    await _overlay.FlyTo(point);
                    await ComputerControl.Scroll(dx, dy, (int)point.X, (int)point.Y);
                    ComputerControl.Warp(saved.X, saved.Y);
                    await Task.Delay(300);
                    return await Look($"Scrolled {direction}.");
                }
                case "drag":
                {
                    var from = Spot(a, "from_id", "from_x", "from_y");
                    var to = Spot(a, "to_id", "to_x", "to_y");
                    if (from == null || to == null) return new ToolResult("Tell me where to drag from and to (ids or x and y).");
                    var saved = Native.Mouse();
                    await _overlay.FlyTo(from.Value.Point);
                    ComputerControl.DragBegin((int)from.Value.Point.X, (int)from.Value.Point.Y);
                    await Task.Delay(120);
                    for (int i = 1; i <= 25; i++)
                    {
                        var f = i / 25.0;
                        var px = from.Value.Point.X + (to.Value.Point.X - from.Value.Point.X) * f;
                        var py = from.Value.Point.Y + (to.Value.Point.Y - from.Value.Point.Y) * f;
                        ComputerControl.DragMove((int)px, (int)py);
                        _overlay.PointAt(new WpfPoint(px, py), new WpfRect(px - 16, py - 16, 32, 32));
                        await Task.Delay(16);
                    }
                    ComputerControl.DragEnd((int)to.Value.Point.X, (int)to.Value.Point.Y);
                    ComputerControl.Warp(saved.X, saved.Y);
                    await Task.Delay(300);
                    return await Look("Dragged it.");
                }
                case "open_app":
                {
                    var app = Str(a, "name");
                    if (app.Length == 0) return new ToolResult("Which app?");
                    _overlay.Flash($"Opening {app}", 2);
                    var result = await ComputerControl.OpenApp(app);
                    await Task.Delay(1200);
                    return await Look(result);
                }
                case "open_url":
                {
                    var result = ComputerControl.OpenUrl(Str(a, "url"));
                    _overlay.Flash(result.StartsWith("Opened ") ? result[7..].TrimEnd('.') : "Hmm", 2);
                    await Task.Delay(1500);
                    return await Look(result);
                }
                default:
                    return new ToolResult($"Unknown action {name}.");
            }
        }
        catch (Exception ex)
        {
            return new ToolResult("That didn't work: " + ex.Message);
        }
        finally { _lastActivity = DateTime.UtcNow; }
    }

    // MARK: Tray

    private void BuildTray()
    {
        _statusItem = new System.Windows.Forms.ToolStripMenuItem("Phone: not connected") { Enabled = false };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Set Gemini key…", null, (_, _) =>
        {
            var key = Prompt.Ask("Gemini API key", "Paste your key from aistudio.google.com (it is stored encrypted for your Windows account):", "", secret: true);
            if (key != null) { _settings.ApiKey = key; RefreshStatus(); }
        });
        menu.Items.Add("Choose Gemini model…", null, (_, _) =>
        {
            var model = Prompt.Ask("Gemini model", "Model name (default: gemini-flash-latest):", _settings.Model, secret: false);
            if (!string.IsNullOrWhiteSpace(model)) { _settings.Model = model.Trim(); _settings.Save(); }
        });
        var control = new System.Windows.Forms.ToolStripMenuItem("Let him use the computer") { Checked = _settings.ComputerControl, CheckOnClick = true };
        control.CheckedChanged += (_, _) => { _settings.ComputerControl = control.Checked; _settings.Save(); };
        menu.Items.Add(control);
        var shots = new System.Windows.Forms.ToolStripMenuItem("Send screenshots to Gemini") { Checked = _settings.SendScreenshots, CheckOnClick = true };
        shots.CheckedChanged += (_, _) => { _settings.SendScreenshots = shots.Checked; _settings.Save(); };
        menu.Items.Add(shots);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Stop actions (Ctrl+Alt+S)", null, (_, _) => StopActions());
        menu.Items.Add("Point at mouse (Ctrl+Alt+P)", null, (_, _) => PointAtMouse());
        menu.Items.Add("Show / hide overlay (Ctrl+Alt+H)", null, (_, _) => ToggleHidden());
        menu.Items.Add("Open settings folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Settings.Folder);
            Process.Start(new ProcessStartInfo(Settings.Folder) { UseShellExecute = true });
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit Bluey", null, (_, _) => _app.Shutdown());

        _tray = new System.Windows.Forms.NotifyIcon { Icon = MakeIcon(), Text = "Bluey", Visible = true, ContextMenuStrip = menu };
    }

    private void RefreshStatus()
    {
        var phones = _server.PhoneNames;
        _statusItem.Text = phones.Count > 0 ? "Phone: " + string.Join(", ", phones) : "Phone: not connected (open Bluey on your iPhone)";
        _tray.Text = phones.Count > 0 ? "Bluey · phone connected" : "Bluey · waiting for phone";
    }

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(new SolidBrush(Color.FromArgb(0x4F, 0x8B, 0xFF)), 1, 1, 30, 30);
            g.FillEllipse(Brushes.White, 6, 8, 9, 11);
            g.FillEllipse(Brushes.White, 17, 8, 9, 11);
            g.FillEllipse(Brushes.Black, 9, 12, 4, 5);
            g.FillEllipse(Brushes.Black, 20, 12, 4, 5);
        }
        var handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        Native.DestroyIcon(handle);
        return icon;
    }
}
