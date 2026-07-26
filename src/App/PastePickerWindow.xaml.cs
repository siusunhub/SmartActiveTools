using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InputAutomationTool.Core;

namespace InputAutomationTool.App;

/// <summary>
/// Full-screen transparent overlay for picking the target app's Paste button by
/// hand. It draws a red box over the OCR-located Win2 label — the origin every
/// stored offset is measured from — and reports the offset of the cursor from
/// that origin live, so the value saved is exactly what the driver will click.
/// </summary>
public partial class PastePickerWindow : Window
{
    private readonly OcrLine _label;
    private readonly int _offsetX;
    private readonly int _offsetY;

    /// <summary>The chosen offset, or null if the user cancelled.</summary>
    public (int Dx, int Dy)? Picked { get; private set; }

    public PastePickerWindow(OcrLine label, int inputOffsetX, int inputOffsetY)
    {
        InitializeComponent();
        _label = label;
        _offsetX = inputOffsetX;
        _offsetY = inputOffsetY;

        // Cover every monitor: the target app may not be on the primary one.
        // These SystemParameters are already in DIPs, which is what Left/Top want.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += OnLoaded;
        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnClick;
        KeyDown += OnKeyDown;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Place the red box over the label. PointFromScreen converts device pixels
        // to DIPs against the monitor this window is actually on, so this stays
        // correct under per-monitor DPI scaling.
        var topLeft = PointFromScreen(new Point(_label.X, _label.Y));
        var bottomRight = PointFromScreen(new Point(_label.X + _label.Width, _label.Y + _label.Height));

        LabelBox.Width = Math.Max(1, bottomRight.X - topLeft.X);
        LabelBox.Height = Math.Max(1, bottomRight.Y - topLeft.Y);
        Canvas.SetLeft(LabelBox, topLeft.X);
        Canvas.SetTop(LabelBox, topLeft.Y);

        // "Click the paste icon" sits directly above the box.
        Prompt.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(Prompt, topLeft.X);
        Canvas.SetTop(Prompt, Math.Max(0, topLeft.Y - Prompt.DesiredSize.Height - 4));

        // Hint bar centred near the bottom of the overlay.
        HintBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(HintBar, Math.Max(0, (Width - HintBar.DesiredSize.Width) / 2));
        Canvas.SetTop(HintBar, Math.Max(0, Height - HintBar.DesiredSize.Height - 24));

        Activate();
        Focus();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var (dx, dy) = OffsetAt(e.GetPosition(Root));
        ReadoutText.Text = $"dx {dx:+#;-#;0}  dy {dy:+#;-#;0}";

        // Pin the readout to the cursor's top-right, mirroring the log format.
        Readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var p = e.GetPosition(Root);
        Canvas.SetLeft(Readout, Math.Min(p.X + 14, Width - Readout.DesiredSize.Width));
        Canvas.SetTop(Readout, Math.Max(0, p.Y - Readout.DesiredSize.Height - 6));
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        Picked = OffsetAt(e.GetPosition(Root));
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
    }

    /// <summary>
    /// Converts a point in the overlay back to device pixels and then to the
    /// driver's stored-offset frame, so the picker and the driver agree exactly.
    /// </summary>
    private (int Dx, int Dy) OffsetAt(Point canvasPoint)
    {
        var screen = PointToScreen(canvasPoint);
        return PasteGeometry.OffsetFor(
            _label, (int)Math.Round(screen.X), (int)Math.Round(screen.Y), _offsetX, _offsetY);
    }
}
