using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SmartActiveTools.Core;

/// <summary>Shared crop and enhancement pipeline for input-field OCR.</summary>
public static class OcrImageProcessing
{
    private const int CropTopFromLabelCenter = 15;
    private const int CropRightFromLabelCenter = 350;
    private const int CropHeight = 45;

    public static Bitmap CropInputText(Bitmap capture, OcrLine label, int originX, int originY, out Rectangle cropRect)
    {
        var x = (int)Math.Round(label.X - originX);
        var y = (int)Math.Round(label.CenterY - originY) + CropTopFromLabelCenter;
        var right = (int)Math.Round(label.CenterX - originX) + CropRightFromLabelCenter;
        cropRect = Rectangle.Intersect(new Rectangle(x, y, right - x, CropHeight),
            new Rectangle(0, 0, capture.Width, capture.Height));

        if (cropRect.Width == 0 || cropRect.Height == 0)
            throw new InvalidOperationException("The calculated input-text crop falls outside the captured window.");

        return capture.Clone(cropRect, PixelFormat.Format32bppArgb);
    }

    public static Bitmap EnhanceForOcr(Bitmap source)
    {
        const float contrast = 1.8f;
        const float darken = 0.75f;
        const float offset = (1f - contrast) / 2f;
        using var grayscale = ToGrayscale(source);
        using var darkened = ApplyColorMatrix(grayscale, new ColorMatrix([
            [darken, 0, 0, 0, 0], [0, darken, 0, 0, 0], [0, 0, darken, 0, 0],
            [0, 0, 0, 1, 0], [0, 0, 0, 0, 1]
        ]));
        using var contrasted = ApplyColorMatrix(darkened, new ColorMatrix([
            [contrast, 0, 0, 0, 0], [0, contrast, 0, 0, 0], [0, 0, contrast, 0, 0],
            [0, 0, 0, 1, 0], [offset, offset, offset, 0, 1]
        ]));
        var result = new Bitmap(contrasted.Width * 2, contrasted.Height * 2, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(contrasted, new Rectangle(0, 0, result.Width, result.Height));
        return result;
    }

    private static Bitmap ToGrayscale(Bitmap source) => ApplyColorMatrix(source, new ColorMatrix([
        [0.299f, 0.299f, 0.299f, 0, 0], [0.587f, 0.587f, 0.587f, 0, 0], [0.114f, 0.114f, 0.114f, 0, 0],
        [0, 0, 0, 1, 0], [0, 0, 0, 0, 1]
    ]));

    private static Bitmap ApplyColorMatrix(Bitmap source, ColorMatrix matrix)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        graphics.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height),
            0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return result;
    }
}
