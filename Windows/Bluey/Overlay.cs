using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Bluey;

/// A transparent, click-through window over the main screen. It draws Bluey's blue cursor, a ring around
/// whatever he is pointing at, and his speech bubble. Positions come in as screen pixels.
internal sealed class Overlay : Window
{
    private static readonly Color Blue = Color.FromRgb(0x4F, 0x8B, 0xFF);

    private readonly Canvas _canvas = new();
    private readonly Path _cursor;
    private readonly Border _ring;
    private readonly Border _bubble;
    private readonly TextBlock _bubbleText;
    private readonly double _scale;
    private readonly int _screenW, _screenH;

    private Point _pos;                 // cursor tip, pixels
    private Point _target;
    private Rect _thing;                // what is being pointed at, pixels
    private bool _pointing;
    private string? _caption;
    private string? _temp;
    private DateTime _tempUntil = DateTime.MinValue;
    private DateTime _ringUntil = DateTime.MinValue;
    private DateTime _last = DateTime.UtcNow;

    public bool IsPointing => _pointing;
    public Point Target => _target;
    public bool Arrived => !_pointing || (Math.Abs(_pos.X - _target.X) < 5 && Math.Abs(_pos.Y - _target.Y) < 5);

    public Overlay()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        _screenW = screen.Width;
        _screenH = screen.Height;
        _scale = Math.Max(1.0, Native.GetDpiForSystem() / 96.0);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Left = screen.Left / _scale;
        Top = screen.Top / _scale;
        Width = _screenW / _scale;
        Height = _screenH / _scale;
        Content = _canvas;

        _cursor = new Path
        {
            Data = Geometry.Parse("M0,0 L0,22 L6,17 L10,27 L14,25 L10,15.5 L18,15.5 Z"),
            Fill = new SolidColorBrush(Blue),
            Stroke = Brushes.White,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = new ScaleTransform(1.5, 1.5),
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.4 },
        };

        _ring = new Border
        {
            BorderBrush = new SolidColorBrush(Blue),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(8),
            Visibility = Visibility.Collapsed,
        };

        _bubbleText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x3B)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        };
        _bubble = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 9, 14, 10),
            Child = _bubbleText,
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.35 },
        };

        _canvas.Children.Add(_ring);
        _canvas.Children.Add(_cursor);
        _canvas.Children.Add(_bubble);

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            Native.SetWindowLong(h, Native.GWL_EXSTYLE,
                style | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        };
        CompositionTarget.Rendering += (_, _) => OnFrame();
    }

    // Everything below must be called on the UI thread.

    public void PointAt(Point spot, Rect thing)
    {
        if (!_pointing)
        {
            var (mx, my) = Native.Mouse();
            _pos = new Point(mx, my);
        }
        _pointing = true;
        _target = spot;
        _thing = thing;
    }

    public void Home()
    {
        _pointing = false;
        _cursor.Visibility = Visibility.Collapsed;
        _ring.Visibility = Visibility.Collapsed;
    }

    public void SetCaption(string? text) => _caption = string.IsNullOrWhiteSpace(text) ? null : text;

    /// A short message that replaces the caption for a moment (like "Stopped" or "↩").
    public void Flash(string text, double seconds)
    {
        _temp = text;
        _tempUntil = DateTime.UtcNow.AddSeconds(seconds);
    }

    public void ClickEffect(Point spot)
    {
        _thing = new Rect(spot.X - 18, spot.Y - 18, 36, 36);
        _ringUntil = DateTime.UtcNow.AddMilliseconds(450);
    }

    public async Task FlyTo(Point spot)
    {
        PointAt(spot, new Rect(spot.X - 16, spot.Y - 16, 32, 32));
        for (int i = 0; i < 80 && !Arrived; i++) await Task.Delay(25);
    }

    private void OnFrame()
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;

        if (_pointing)
        {
            var k = 1 - Math.Exp(-dt * 9);
            _pos = new Point(_pos.X + (_target.X - _pos.X) * k, _pos.Y + (_target.Y - _pos.Y) * k);
            _cursor.Visibility = Visibility.Visible;
            Canvas.SetLeft(_cursor, _pos.X / _scale);
            Canvas.SetTop(_cursor, _pos.Y / _scale);
        }

        var ringOn = _pointing || now < _ringUntil;
        if (ringOn && !_thing.IsEmpty)
        {
            var pulse = 0.65 + 0.35 * Math.Sin(now.Ticks / 1e7 * 5);
            var r = new Rect(_thing.X - 5, _thing.Y - 4, _thing.Width + 10, _thing.Height + 8);
            _ring.Visibility = Visibility.Visible;
            _ring.Width = Math.Max(10, r.Width / _scale);
            _ring.Height = Math.Max(10, r.Height / _scale);
            _ring.Opacity = pulse;
            Canvas.SetLeft(_ring, r.X / _scale);
            Canvas.SetTop(_ring, r.Y / _scale);
        }
        else _ring.Visibility = Visibility.Collapsed;

        var text = now < _tempUntil ? _temp : _caption;
        if (string.IsNullOrEmpty(text)) { _bubble.Visibility = Visibility.Collapsed; return; }
        if (_bubbleText.Text != text) _bubbleText.Text = text;
        _bubble.Visibility = Visibility.Visible;
        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var bw = _bubble.DesiredSize.Width;
        var bh = _bubble.DesiredSize.Height;
        double x, y;
        if (_pointing && !_thing.IsEmpty)
        {
            x = (_thing.X + _thing.Width / 2) / _scale - bw / 2;
            y = _thing.Y / _scale - bh - 16;
            if (y < 8) y = (_thing.Bottom) / _scale + 34;   // no room above: go below
        }
        else
        {
            x = Width / 2 - bw / 2;
            y = Height - bh - 96;
        }
        x = Math.Clamp(x, 8, Math.Max(8, Width - bw - 8));
        y = Math.Clamp(y, 8, Math.Max(8, Height - bh - 8));
        Canvas.SetLeft(_bubble, x);
        Canvas.SetTop(_bubble, y);
    }
}
