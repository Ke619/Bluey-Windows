using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Bluey;

internal sealed class Target
{
    public string Id = "";
    public string Text = "";
    public System.Windows.Rect Rect;   // screen pixels
}

internal sealed class ScreenSnapshot
{
    public int Width, Height;
    public List<Target> Targets = new();
    public byte[] Jpeg = Array.Empty<byte>();
    public string ListText = "";

    public Target? Find(string id) =>
        Targets.FirstOrDefault(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// The smallest target that contains the point (used for "this" and "that" at the mouse pointer).
    public Target? Near(int x, int y) =>
        Targets.Where(t => t.Rect.Contains(x, y)).OrderBy(t => t.Rect.Width * t.Rect.Height).FirstOrDefault();
}

/// Takes a screenshot of the main screen and finds text (Windows OCR) and clickable controls (UI Automation).
internal static class ScreenReader
{
    public static async Task<ScreenSnapshot> SnapshotAsync()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var snap = new ScreenSnapshot { Width = bounds.Width, Height = bounds.Height };
        var hwnd = Native.ForegroundWindowHandle();

        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

        // JPEG for the model, at most 1280 pixels wide.
        var scale = Math.Min(1.0, 1280.0 / bmp.Width);
        using (var small = new Bitmap(bmp, new Size((int)(bmp.Width * scale), (int)(bmp.Height * scale))))
        using (var ms = new MemoryStream())
        {
            small.Save(ms, ImageFormat.Jpeg);
            snap.Jpeg = ms.ToArray();
        }

        var controlsTask = Task.Run(() => ReadControls(hwnd, bounds));
        var lines = await ReadText(bmp);
        var controls = await Task.WhenAny(controlsTask, Task.Delay(3000)) == controlsTask ? controlsTask.Result : new List<Target>();

        var sb = new StringBuilder();
        sb.AppendLine($"Front window: \"{Native.ForegroundTitle()}\". Positions are on a 0-1000 grid (x right, y down).");
        int c = 0;
        if (controls.Count > 0) sb.AppendLine("Clickable controls:");
        foreach (var t in controls)
        {
            t.Id = "C" + (++c);
            snap.Targets.Add(t);
            sb.AppendLine($"{t.Id} \"{Trim(t.Text, 50)}\" @{Gx(t.Rect, snap)}");
        }

        int l = 0, w = 0;
        if (lines.Count > 0) sb.AppendLine("Text on screen (lines L, words W):");
        foreach (var (lineText, lineRect, words) in lines.Take(80))
        {
            var lt = new Target { Id = "L" + (++l), Text = lineText, Rect = lineRect };
            snap.Targets.Add(lt);
            var wordIds = new List<string>();
            foreach (var (wt, wr) in words)
            {
                var wtgt = new Target { Id = "W" + (++w), Text = wt, Rect = wr };
                snap.Targets.Add(wtgt);
                wordIds.Add($"{wtgt.Id}={wt}");
            }
            sb.Append($"{lt.Id} @{Gx(lineRect, snap)} \"{Trim(lineText, 90)}\"");
            if (words.Count is > 1 and <= 8) sb.Append("  words: " + string.Join(" ", wordIds));
            sb.AppendLine();
        }
        snap.ListText = sb.ToString();
        return snap;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string Gx(System.Windows.Rect r, ScreenSnapshot s) =>
        $"{(int)Math.Clamp(r.X + r.Width / 2, 0, s.Width) * 1000 / Math.Max(s.Width, 1)},{(int)Math.Clamp(r.Y + r.Height / 2, 0, s.Height) * 1000 / Math.Max(s.Height, 1)}";

    private static async Task<List<(string Text, System.Windows.Rect Rect, List<(string, System.Windows.Rect)> Words)>> ReadText(Bitmap bmp)
    {
        var result = new List<(string, System.Windows.Rect, List<(string, System.Windows.Rect)>)>();
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) return result;
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            var decoder = await BitmapDecoder.CreateAsync(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));
            using var soft = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var ocr = await engine.RecognizeAsync(soft);
            foreach (var line in ocr.Lines)
            {
                var words = new List<(string, System.Windows.Rect)>();
                double x1 = double.MaxValue, y1 = double.MaxValue, x2 = 0, y2 = 0;
                foreach (var word in line.Words)
                {
                    var r = word.BoundingRect;
                    var wr = new System.Windows.Rect(r.X, r.Y, r.Width, r.Height);
                    words.Add((word.Text, wr));
                    x1 = Math.Min(x1, wr.Left); y1 = Math.Min(y1, wr.Top);
                    x2 = Math.Max(x2, wr.Right); y2 = Math.Max(y2, wr.Bottom);
                }
                if (words.Count == 0) continue;
                result.Add((line.Text, new System.Windows.Rect(x1, y1, x2 - x1, y2 - y1), words));
            }
        }
        catch (Exception ex) { Log.Write("OCR failed: " + ex.Message); }
        return result;
    }

    private static List<Target> ReadControls(IntPtr hwnd, Rectangle screen)
    {
        var list = new List<Target>();
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var types = new[]
            {
                ControlType.Button, ControlType.MenuItem, ControlType.Hyperlink, ControlType.Edit, ControlType.TabItem,
                ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.ListItem, ControlType.SplitButton,
            };
            var cond = new OrCondition(types.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray());
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
            {
                if (list.Count >= 80) break;
                try
                {
                    var info = e.Current;
                    if (info.IsOffscreen || string.IsNullOrWhiteSpace(info.Name)) continue;
                    var r = info.BoundingRectangle;
                    if (r.IsEmpty || r.Width < 4 || r.Height < 4) continue;
                    if (!screen.IntersectsWith(new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height))) continue;
                    list.Add(new Target { Text = info.Name.Replace("\n", " "), Rect = r });
                }
                catch { /* an element vanished while reading: skip it */ }
            }
        }
        catch (Exception ex) { Log.Write("UI Automation failed: " + ex.Message); }
        return list;
    }
}
