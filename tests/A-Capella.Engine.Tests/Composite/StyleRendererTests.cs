using A_Capella.Engine.Composite;
using SkiaSharp;

namespace A_Capella.Engine.Tests.Composite;

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
        var style = new CompositeStyle(new BackgroundSettings { Color = "#336699" });
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
        var style = new CompositeStyle(new BackgroundSettings { Color = "not a color" });
        using var renderer = new StyleRenderer(style, 20, 20);
        using var output = Compositor.Composite(20, 20, new List<SKBitmap>(), new List<SKRect>(), style: renderer);
        Assert.Equal(SKColors.Black, output.GetPixel(10, 10));
    }

    [Fact]
    public void BackgroundImage_Stretch_CoversTheCanvas()
    {
        using var img = Solid(8, 8, SKColors.Lime);
        var style = new CompositeStyle(new BackgroundSettings { ImagePath = SavePng(img), ImageMode = BackgroundImageMode.Stretch });
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
        var style = new CompositeStyle(new BackgroundSettings { ImagePath = SavePng(img), ImageMode = BackgroundImageMode.Tile });
        using var renderer = new StyleRenderer(style, 60, 30);
        using var output = Compositor.Composite(60, 30, new List<SKBitmap>(), new List<SKRect>(), style: renderer);
        Assert.Equal(SKColors.Red, output.GetPixel(40, 10));
        Assert.Equal(SKColors.Blue, output.GetPixel(41, 10));
        Assert.Equal(SKColors.Red, output.GetPixel(42, 10));
    }

    [Fact]
    public void MissingBackgroundImage_FallsBackToTheColor()
    {
        var style = new CompositeStyle(new BackgroundSettings { Color = "#FF0000", ImagePath = "/no/such/file.png" });
        using var renderer = new StyleRenderer(style, 20, 20);
        using var output = Compositor.Composite(20, 20, new List<SKBitmap>(), new List<SKRect>(), style: renderer);
        Assert.Equal(SKColors.Red, output.GetPixel(10, 10));
    }

    [Fact]
    public void DisposedRenderer_DrawsPlainBlack_InsteadOfThrowing()
    {
        var renderer = new StyleRenderer(new CompositeStyle(new BackgroundSettings { Color = "#FF0000" }), 20, 20);
        renderer.Dispose();
        using var output = Compositor.Composite(20, 20, new List<SKBitmap>(), new List<SKRect>(), style: renderer);
        Assert.Equal(SKColors.Black, output.GetPixel(5, 5));
    }
}
