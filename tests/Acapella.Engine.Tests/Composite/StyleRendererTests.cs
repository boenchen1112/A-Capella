using Acapella.Engine.Composite;
using SkiaSharp;

namespace Acapella.Engine.Tests.Composite;

public class StyleRendererTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var f in _temp) if (File.Exists(f)) File.Delete(f);
    }

    private static SKBitmap Solid(int w, int h, SKColor c)
    {
        var b = new SKBitmap(w, h);
        using var cv = new SKCanvas(b);
        cv.Clear(c);
        return b;
    }

    private string SavePng(SKBitmap bitmap)
    {
        string path = Path.Combine(Path.GetTempPath(), $"bg-{Guid.NewGuid()}.png");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var fs = File.Create(path)) data.SaveTo(fs);
        _temp.Add(path);
        return path;
    }

    [Fact]
    public void BackgroundColor_ShowsInGapsAndEmptyCells()
    {
        var style = new CompositeStyle(new BackgroundSettings { Color = "#336699" }, new WatermarkSettings());
        using var renderer = new StyleRenderer(style, 600, 300);
        var layout = GridLayoutCatalog.Get("2-cols");
        var frames = new List<SKBitmap> { Solid(10, 10, SKColors.Red) };   // second cell stays empty
        var rects = layout.GetCellRects(600, 300, 0.05, new[] { 0 });

        using var output = Compositor.Composite(600, 300, frames, rects, style: renderer);

        Assert.Equal(SKColors.Red, output.GetPixel(150, 150));
        Assert.Equal(new SKColor(0x33, 0x66, 0x99), output.GetPixel(450, 150));   // empty cell shows the background
        Assert.Equal(new SKColor(0x33, 0x66, 0x99), output.GetPixel(300, 150));   // so does the gap
    }

    [Fact]
    public void InvalidColor_FallsBackToBlack()
    {
        var style = new CompositeStyle(new BackgroundSettings { Color = "not a color" }, new WatermarkSettings());
        using var renderer = new StyleRenderer(style, 20, 20);
        using var output = Compositor.Composite(20, 20, new List<SKBitmap>(), new List<SKRect>(), style: renderer);
        Assert.Equal(SKColors.Black, output.GetPixel(10, 10));
    }

    [Fact]
    public void BackgroundImage_Stretch_CoversTheCanvas()
    {
        using var img = Solid(8, 8, SKColors.Lime);
        var style = new CompositeStyle(new BackgroundSettings { ImagePath = SavePng(img), ImageMode = BackgroundImageMode.Stretch }, new WatermarkSettings());
        using var renderer = new StyleRenderer(style, 100, 60);
        using var output = Compositor.Composite(100, 60, new List<SKBitmap>(), new List<SKRect>(), style: renderer);
        Assert.Equal(SKColors.Lime, output.GetPixel(2, 2));
        Assert.Equal(SKColors.Lime, output.GetPixel(97, 57));
    }

    [Fact]
    public void BackgroundImage_Tile_RepeatsAtNativeSize()
    {
        // 2x1 image: left pixel red, right pixel blue -> tiled, the pattern repeats every 2px.
        using var img = new SKBitmap(2, 1);
        img.SetPixel(0, 0, SKColors.Red);
        img.SetPixel(1, 0, SKColors.Blue);
        var style = new CompositeStyle(new BackgroundSettings { ImagePath = SavePng(img), ImageMode = BackgroundImageMode.Tile }, new WatermarkSettings());
        using var renderer = new StyleRenderer(style, 60, 30);
        using var output = Compositor.Composite(60, 30, new List<SKBitmap>(), new List<SKRect>(), style: renderer);
        Assert.Equal(SKColors.Red, output.GetPixel(40, 10));
        Assert.Equal(SKColors.Blue, output.GetPixel(41, 10));
        Assert.Equal(SKColors.Red, output.GetPixel(42, 10));
    }

    [Fact]
    public void MissingBackgroundImage_FallsBackToTheColor()
    {
        var style = new CompositeStyle(new BackgroundSettings { Color = "#FF0000", ImagePath = "/no/such/file.png" }, new WatermarkSettings());
        using var renderer = new StyleRenderer(style, 20, 20);
        using var output = Compositor.Composite(20, 20, new List<SKBitmap>(), new List<SKRect>(), style: renderer);
        Assert.Equal(SKColors.Red, output.GetPixel(10, 10));
    }

    private static int CountNonBackground(SKBitmap bmp, SKRect region, SKColor background)
    {
        int n = 0;
        for (int y = (int)region.Top; y < (int)region.Bottom; y++)
            for (int x = (int)region.Left; x < (int)region.Right; x++)
                if (bmp.GetPixel(x, y) != background) n++;
        return n;
    }

    /// <summary>A typeface that can actually draw glyphs, or null on a machine with no fonts at all
    /// (e.g. a bare Linux container) -- the text assertions below then return early, since there is
    /// nothing to render. Windows always has fonts, so the app's own lookup path is what runs there.</summary>
    private static readonly Lazy<SKTypeface?> TestTypeface = new(() =>
    {
        bool Renders(SKTypeface? t) => t is not null && t.GetGlyphs("H")[0] != 0;
        var system = SKTypeface.FromFamilyName("Arial");
        if (Renders(system)) return system;
        foreach (var path in new[] { "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/TTF/DejaVuSans.ttf", "/usr/share/fonts/dejavu/DejaVuSans.ttf" })
            if (File.Exists(path) && SKTypeface.FromFile(path) is { } t && Renders(t)) return t;
        return null;
    });

    private static bool HasFonts => TestTypeface.Value is not null;

    private static SKBitmap RenderWatermark(WatermarkSettings w, int width = 400, int height = 400)
    {
        using var renderer = new StyleRenderer(new CompositeStyle(new BackgroundSettings(), w), width, height, TestTypeface.Value);
        return Compositor.Composite(width, height, new List<SKBitmap>(), new List<SKRect>(), style: renderer);
    }

    [Theory]
    [InlineData(WatermarkCorner.TopLeft, 0, 0)]
    [InlineData(WatermarkCorner.TopRight, 200, 0)]
    [InlineData(WatermarkCorner.BottomLeft, 0, 200)]
    [InlineData(WatermarkCorner.BottomRight, 200, 200)]
    public void Watermark_DrawsOnlyInItsCorner(WatermarkCorner corner, int quadrantLeft, int quadrantTop)
    {
        if (!HasFonts) return;   // no fonts on this machine: nothing can be drawn

        using var output = RenderWatermark(new WatermarkSettings { Enabled = true, Text = "HELLO", Corner = corner, Opacity = 1, Shadow = false });

        var all = new SKRect(0, 0, 400, 400);
        int total = CountNonBackground(output, all, SKColors.Black);
        int inQuadrant = CountNonBackground(output, new SKRect(quadrantLeft, quadrantTop, quadrantLeft + 200, quadrantTop + 200), SKColors.Black);

        Assert.True(total > 50, "the watermark should put pixels on screen");
        Assert.Equal(total, inQuadrant);
    }

    [Fact]
    public void Watermark_Disabled_OrBlankText_DrawsNothing()
    {
        using var disabled = RenderWatermark(new WatermarkSettings { Enabled = false, Text = "HELLO" });
        using var blank = RenderWatermark(new WatermarkSettings { Enabled = true, Text = "   " });
        Assert.Equal(0, CountNonBackground(disabled, new SKRect(0, 0, 400, 400), SKColors.Black));
        Assert.Equal(0, CountNonBackground(blank, new SKRect(0, 0, 400, 400), SKColors.Black));
    }

    [Fact]
    public void Watermark_Opacity_DimsThePixels()
    {
        if (!HasFonts) return;   // no fonts on this machine: nothing can be drawn

        var opaque = new WatermarkSettings { Enabled = true, Text = "HHHH", Corner = WatermarkCorner.Center, Opacity = 1, Shadow = false, StrokeFraction = 0, SizeFraction = 0.3 };
        using var full = RenderWatermark(opaque);
        using var half = RenderWatermark(opaque with { Opacity = 0.5 });

        byte Brightest(SKBitmap b) { byte m = 0; for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) m = Math.Max(m, b.GetPixel(x, y).Red); return m; }
        Assert.Equal(255, Brightest(full));
        Assert.InRange((int)Brightest(half), 110, 145);
    }

    [Fact]
    public void Watermark_UnknownFont_StillDraws()
    {
        if (!HasFonts) return;   // no fonts on this machine: nothing can be drawn

        using var output = RenderWatermark(new WatermarkSettings { Enabled = true, Text = "HELLO", FontFamily = "No Such Font 9000", Opacity = 1 });
        Assert.True(CountNonBackground(output, new SKRect(0, 0, 400, 400), SKColors.Black) > 50);
    }

    [Fact]
    public void DisposedRenderer_DrawsPlainBlack_InsteadOfThrowing()
    {
        var renderer = new StyleRenderer(new CompositeStyle(new BackgroundSettings { Color = "#FF0000" }, new WatermarkSettings()), 20, 20);
        renderer.Dispose();
        using var output = Compositor.Composite(20, 20, new List<SKBitmap>(), new List<SKRect>(), style: renderer);
        Assert.Equal(SKColors.Black, output.GetPixel(5, 5));
    }
}
