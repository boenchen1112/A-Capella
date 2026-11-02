using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace A_Capella.App;

/// <summary>
/// Shows the composited preview frame. The engine hands over SkiaSharp bitmaps; each is copied into
/// a WriteableBitmap (a few hundred KB at preview size) and drawn stretched over the control, on
/// black. Replaces the WPF-only SKElement.
/// </summary>
public sealed class FrameView : Control
{
    private WriteableBitmap? _bitmap;

    /// <summary>Replaces the picture; null clears to black. The bitmap is copied, not kept.</summary>
    public void SetFrame(SKBitmap? frame)
    {
        if (frame is null || frame.Width <= 0 || frame.Height <= 0)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }
        else
        {
            if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Premul);
            }

            using var locked = _bitmap.Lock();
            var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var pixmap = frame.PeekPixels();
            pixmap?.ReadPixels(info, locked.Address, locked.RowBytes);
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap is not null)
            context.DrawImage(_bitmap, new Rect(_bitmap.Size), new Rect(Bounds.Size));
    }
}
