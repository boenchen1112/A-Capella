using System.ComponentModel;
using A_Capella.App.Layout;
using A_Capella.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace A_Capella.App;

/// <summary>Raised by <see cref="LaneClipTrack"/> while one of its trim handles is dragged.
/// <c>TotalDeltaPx</c> is the pointer's horizontal travel since the drag began.</summary>
public sealed class TrimDragEventArgs : EventArgs
{
    public TrimDragEventArgs(LayerRowViewModel row, bool isInHandle, double trackWidthPx, double totalDeltaPx)
    {
        Row = row;
        IsInHandle = isInHandle;
        TrackWidthPx = trackWidthPx;
        TotalDeltaPx = totalDeltaPx;
    }

    public LayerRowViewModel Row { get; }
    public bool IsInHandle { get; }
    public double TrackWidthPx { get; }
    public double TotalDeltaPx { get; }
}

/// <summary>
/// One lane's clip track: a faint bar for the full source span, a solid bar for the trimmed span,
/// and drag handles at both ends of the solid span. Dragging a handle reports the pointer's travel
/// through <see cref="TrimDragStarted"/>/<see cref="TrimDragDelta"/>/<see cref="TrimDragCompleted"/>;
/// MainWindow turns that into TrimStartMs/TrimEndMs (the channel panel's In/Out boxes read and write
/// the same two properties, so either path stays in sync with the other). The geometry comes from
/// ClipTimelineMath, shared with the drag math.
/// </summary>
public sealed class LaneClipTrack : Canvas
{
    private const double BarTop = 11;
    private const double BarHeight = 40;
    private const double HandleWidth = 10;

    private readonly BarsLayer _bars;
    private readonly Border _inHandle = NewHandle();
    private readonly Border _outHandle = NewHandle();
    private LayerRowViewModel? _row;
    private bool _dragIsIn;
    private double _dragStartX;
    private bool _dragging;

    public event EventHandler<TrimDragEventArgs>? TrimDragStarted;
    public event EventHandler<TrimDragEventArgs>? TrimDragDelta;
    public event EventHandler<TrimDragEventArgs>? TrimDragCompleted;

    public LaneClipTrack()
    {
        Background = Brushes.Transparent;
        _bars = new BarsLayer(this) { IsHitTestVisible = false };
        Children.Add(_bars);
        Children.Add(_inHandle);
        Children.Add(_outHandle);
        WireHandle(_inHandle, isIn: true);
        WireHandle(_outHandle, isIn: false);
    }

    private static Border NewHandle()
    {
        var handle = new Border { Width = HandleWidth, Height = BarHeight };
        handle.Classes.Add("trimhandle");
        SetTop(handle, BarTop);
        return handle;
    }

    private void WireHandle(Border handle, bool isIn)
    {
        handle.PointerPressed += (_, e) =>
        {
            if (_row is null || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            e.Pointer.Capture(handle);
            _dragging = true;
            _dragIsIn = isIn;
            _dragStartX = e.GetPosition(this).X;
            TrimDragStarted?.Invoke(this, Args(0));
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!_dragging || _dragIsIn != isIn) return;
            TrimDragDelta?.Invoke(this, Args(e.GetPosition(this).X - _dragStartX));
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!_dragging || _dragIsIn != isIn) return;
            _dragging = false;
            e.Pointer.Capture(null);
            TrimDragCompleted?.Invoke(this, Args(e.GetPosition(this).X - _dragStartX));
        };
    }

    private TrimDragEventArgs Args(double totalDeltaPx) => new(_row!, _dragIsIn, Bounds.Width, totalDeltaPx);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_row is not null) _row.PropertyChanged -= RowChanged;
        _row = DataContext as LayerRowViewModel;
        if (_row is not null) _row.PropertyChanged += RowChanged;
        Refresh();
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) Refresh();
    }

    private void Refresh()
    {
        if (_row is { HasSource: true })
        {
            var (x, width) = ClipTimelineMath.ToPixelRange(_row.TrimStartMs, _row.EffectiveTrimEndMs, _row.TimelineDurationMs, Bounds.Width);
            SetLeft(_inHandle, x - HandleWidth / 2);
            SetLeft(_outHandle, x + width - HandleWidth / 2);
            _inHandle.IsVisible = _outHandle.IsVisible = true;
        }
        else
        {
            _inHandle.IsVisible = _outHandle.IsVisible = false;
        }
        _bars.Width = Bounds.Width;
        _bars.Height = Bounds.Height;
        _bars.InvalidateVisual();
    }

    private void DrawBars(DrawingContext context)
    {
        if (_row is not { HasSource: true } row) return;

        var accent = row.CellColor;
        var pen = new Pen(accent, 1);

        var (fullX, fullWidth) = ClipTimelineMath.ToPixelRange(0, row.SourceDurationMs, row.TimelineDurationMs, Bounds.Width);
        using (context.PushOpacity(0.3))
            context.DrawRectangle(accent, pen, new Rect(fullX, BarTop, fullWidth, BarHeight), 5, 5);

        var (x, width) = ClipTimelineMath.ToPixelRange(row.TrimStartMs, row.EffectiveTrimEndMs, row.TimelineDurationMs, Bounds.Width);
        context.DrawRectangle(accent, pen, new Rect(x, BarTop, width, BarHeight), 5, 5);
    }

    /// <summary>Paints the two bars behind the handles (Panel's own Render can't be overridden).</summary>
    private sealed class BarsLayer : Control
    {
        private readonly LaneClipTrack _owner;
        public BarsLayer(LaneClipTrack owner) => _owner = owner;
        public override void Render(DrawingContext context) => _owner.DrawBars(context);
    }
}
