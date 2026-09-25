using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace EmployeeMonitoring.Client;

internal sealed class CaptureResult
{
    public byte[] Jpeg { get; init; } = Array.Empty<byte>();
    public int Width { get; init; }
    public int Height { get; init; }
    public bool LooksLocked { get; init; }
    public string WindowTitle { get; init; } = string.Empty;
}

internal static class ScreenCapture
{
    private static ImageCodecInfo? _jpegCodec;

    public static (int Width, int Height, int Count) DescribeScreen()
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        int count = Screen.AllScreens.Length;
        return (bounds.Width, bounds.Height, count);
    }

    public static CaptureResult Capture(int maxWidth, int jpegQuality)
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Не удалось определить границы виртуального экрана.");
        }

        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(full))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        bool looksLocked = LooksLikeLockedScreen(full);

        Bitmap output = full;
        bool ownsOutput = false;
        if (maxWidth > 0 && full.Width > maxWidth)
        {
            int height = (int)Math.Round(full.Height * (double)maxWidth / full.Width, MidpointRounding.AwayFromZero);
            height = Math.Clamp(height, 1, full.Height);
            var resized = new Bitmap(maxWidth, height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(full, new Rectangle(0, 0, maxWidth, height));
            }

            output = resized;
            ownsOutput = true;
        }

        try
        {
            using var buffer = new MemoryStream();
            var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(jpegQuality, 10, 100));
            output.Save(buffer, GetJpegCodec(), parameters);
            return new CaptureResult
            {
                Jpeg = buffer.ToArray(),
                Width = output.Width,
                Height = output.Height,
                LooksLocked = looksLocked,
                WindowTitle = NativeMethods.GetWindowTitle(NativeMethods.ForegroundWindow)
            };
        }
        finally
        {
            if (ownsOutput)
            {
                output.Dispose();
            }
        }
    }

    /// <summary>
    /// Признак заблокированного рабочего стола: снимок полностью чёрный
    /// (при захвате экрана в заблокированной сессии GDI возвращает нули).
    /// </summary>
    private static bool LooksLikeLockedScreen(Bitmap bitmap)
    {
        const int samples = 32;
        using var buffer = new Bitmap(samples, samples, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(buffer))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(bitmap, new Rectangle(0, 0, samples, samples));
        }

        for (int y = 0; y < samples; y++)
        {
            for (int x = 0; x < samples; x++)
            {
                Color color = buffer.GetPixel(x, y);
                if (color.R > 12 || color.G > 12 || color.B > 12)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static ImageCodecInfo GetJpegCodec()
    {
        if (_jpegCodec is not null)
        {
            return _jpegCodec;
        }

        foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
            {
                _jpegCodec = codec;
                return codec;
            }
        }

        throw new InvalidOperationException("JPEG-кодек недоступен в системе.");
    }
}
