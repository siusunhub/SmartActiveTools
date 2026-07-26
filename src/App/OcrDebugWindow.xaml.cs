using System.IO;
using System.Windows;
using System.Drawing;
using System.Drawing.Imaging;
using SmartActiveTools.Core;

namespace SmartActiveTools.App;

public partial class OcrDebugWindow : Window
{
    private readonly TargetWindow _target;

    public OcrDebugWindow(TargetWindow target)
    {
        _target = target;
        InitializeComponent();
        Title = $"OCR Text — {target.Title}";
    }

    private async void OnOcrAfterDelay(object sender, RoutedEventArgs e)
    {
        OcrAfterDelayButton.IsEnabled = false;
        CropTextCheckBox.IsEnabled = false;
        StatusText.Text = "Capturing selected window in 3 seconds…";

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            StatusText.Text = "Running OCR…";

            using var capture = ScreenCapture.CaptureWindow(_target.Handle, out var originX, out var originY);
            if (capture is null)
            {
                StatusText.Text = "OCR failed: could not capture the target window.";
                return;
            }

            Bitmap? cropped = null;
            try
            {
                var source = capture;
                var sourceOriginX = originX;
                var sourceOriginY = originY;
                var scope = "full capture";

                if (CropTextCheckBox.IsChecked == true)
                {
                    var fullLines = await OcrTextReader.ReadBitmapAsync(capture, originX, originY);
                    var label = FindActivationKeyLabel(fullLines);
                    if (label is null)
                    {
                        OcrTextBox.Text = "Crop requested, but the Activation key label was not found in this capture.";
                        StatusText.Text = "OCR stopped: Activation key label not found.";
                        return;
                    }

                    cropped = OcrImageProcessing.CropInputText(capture, label.Value, originX, originY, out var cropRect);
                    source = cropped;
                    sourceOriginX += cropRect.X;
                    sourceOriginY += cropRect.Y;
                    scope = $"cropped input {cropRect.Width}x{cropRect.Height}";
                }

                using var enhanced = OcrImageProcessing.EnhanceForOcr(source);
                var captureNote = SaveOcrInputs(source, enhanced);
                var enhancedLines = await OcrTextReader.ReadBitmapAsync(enhanced, sourceOriginX, sourceOriginY);
                var originalLines = await OcrTextReader.ReadBitmapAsync(source, sourceOriginX, sourceOriginY);

                OcrTextBox.Text =
                    $"=== Try 1: grayscale, darken, contrast, 2x enlarged ({scope}) ===" + Environment.NewLine +
                    string.Join(Environment.NewLine, enhancedLines.Select(line => line.Text)) +
                    Environment.NewLine + Environment.NewLine +
                    $"=== Try 2: original capture ({scope}) ===" + Environment.NewLine +
                    string.Join(Environment.NewLine, originalLines.Select(line => line.Text));
                StatusText.Text = $"OCR complete: enhanced {enhancedLines.Count} line(s), original {originalLines.Count} line(s). {captureNote}";
            }
            finally
            {
                cropped?.Dispose();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"OCR failed: {ex.Message}";
        }
        finally
        {
            OcrAfterDelayButton.IsEnabled = true;
            CropTextCheckBox.IsEnabled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static string SaveOcrInputs(Bitmap normal, Bitmap enhanced)
    {
        var normalPath = Path.Combine(AppContext.BaseDirectory, "ocr-capture-normal.png");
        var enhancedPath = Path.Combine(AppContext.BaseDirectory, "ocr-capture-enhanced.png");
        try
        {
            normal.Save(normalPath, ImageFormat.Png);
            enhanced.Save(enhancedPath, ImageFormat.Png);
            return $"Saved normal: {normalPath}; enhanced: {enhancedPath}";
        }
        catch (Exception saveEx)
        {
            return $"OCR images could not be saved: {saveEx.Message}";
        }
    }

    private static OcrLine? FindActivationKeyLabel(IEnumerable<OcrLine> lines) =>
        lines.Where(line => FuzzyMatch.Contains(line.Text, "Activation key"))
             .OrderBy(line => FuzzyMatch.FullDistance(line.Text, "Activation key"))
             .Cast<OcrLine?>()
             .FirstOrDefault();

}
