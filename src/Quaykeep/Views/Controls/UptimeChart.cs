using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Quaykeep.Core.Monitoring;

namespace Quaykeep.Views.Controls;

/// <summary>
/// Bar chart of uptime buckets: stacked up / degraded / down shares (availability) or the average latency.
/// Hovering a bar shows its period and numbers.
/// </summary>
public sealed class UptimeChart : Control
{
    private const double AxisHeight = 18;
    private const double LeftPad = 44;

    private IReadOnlyList<UptimeBucket> _buckets = [];
    private int _hover = -1;

    public UptimeChart()
    {
        ToolTip.SetPlacement(this, PlacementMode.Pointer);
        ToolTip.SetShowDelay(this, 0);
        MinHeight = 90;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public bool ShowLatency { get; set; }

    /// <summary>Format of the time labels under the bars ("HH:mm" for a day, "dd.MM" for longer periods).</summary>
    public string AxisFormat { get; set; } = "HH:mm";

    public IReadOnlyList<UptimeBucket> Buckets
    {
        get => _buckets;
        set
        {
            _buckets = value;
            _hover = -1;
            InvalidateVisual();
        }
    }

    private IBrush Res(string key, Color fallback) => this.Brush(key, new SolidColorBrush(fallback));

    private Rect Plot => new(LeftPad, 4, Math.Max(0, Bounds.Width - LeftPad - 4), Math.Max(0, Bounds.Height - AxisHeight - 6));

    public override void Render(DrawingContext dc)
    {
        var plot = Plot;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size)); // hit testing for the tooltip
        if (_buckets.Count == 0 || plot.Width <= 0 || plot.Height <= 0) return;

        var text = Res("TextFillColorSecondaryBrush", Colors.Gray);
        var grid = new Pen(Res("DividerStrokeColorDefaultBrush", Color.FromArgb(40, 128, 128, 128)), 1);
        var up = Res("SystemFillColorSuccessBrush", Colors.SeaGreen);
        var degraded = Res("SystemFillColorCautionBrush", Colors.Goldenrod);
        var down = Res("SystemFillColorCriticalBrush", Colors.IndianRed);
        var none = Res("ControlStrongFillColorDisabledBrush", Color.FromArgb(60, 128, 128, 128));
        var accent = Res("AccentFillColorDefaultBrush", Colors.SteelBlue);
        var hover = Res("TextFillColorPrimaryBrush", Colors.Gray);

        var w = plot.Width / _buckets.Count;
        var gap = w > 5 ? 1.0 : 0.0;
        double maxLatency = ShowLatency ? Math.Max(1, _buckets.Max(b => b.AvgLatencyMs ?? 0)) * 1.15 : 1;

        // y axis: 0 / 50 / 100 % or 0 / half / max ms
        foreach (var f in new[] { 0.0, 0.5, 1.0 })
        {
            var y = plot.Bottom - plot.Height * f;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = ShowLatency ? $"{maxLatency * f:0}" : $"{100 * f:0}%";
            DrawText(dc, label, text, new Point(plot.Left - 6, y), alignRight: true, centerY: true);
        }

        for (int i = 0; i < _buckets.Count; i++)
        {
            var b = _buckets[i];
            var x = plot.Left + i * w;
            var bw = Math.Max(1, w - gap);
            if (ShowLatency)
            {
                if (b.AvgLatencyMs is { } l)
                {
                    var h = plot.Height * l / maxLatency;
                    dc.DrawRectangle(accent, null, new Rect(x, plot.Bottom - h, bw, h));
                }
                continue;
            }
            if (b.UpPercent is not { } p)
            {
                dc.DrawRectangle(none, null, new Rect(x, plot.Bottom - 3, bw, 3));
                continue;
            }
            var downH = plot.Height * (100 - p) / 100;
            var degradedH = plot.Height * Math.Min(b.DegradedShare, p / 100);
            var upH = plot.Height - downH - degradedH;
            if (downH > 0) dc.DrawRectangle(down, null, new Rect(x, plot.Top, bw, Math.Max(downH, p < 100 ? 2 : 0)));
            if (degradedH > 0) dc.DrawRectangle(degraded, null, new Rect(x, plot.Top + downH, bw, degradedH));
            if (upH > 0) dc.DrawRectangle(up, null, new Rect(x, plot.Top + downH + degradedH, bw, upH));
        }

        if (_hover >= 0 && _hover < _buckets.Count)
        {
            var x = plot.Left + _hover * w;
            dc.DrawRectangle(null, new Pen(hover, 1), new Rect(x - 0.5, plot.Top - 0.5, Math.Max(1, w - gap) + 1, plot.Height + 1));
        }

        // time axis: ~6 labels
        var ticks = Math.Min(6, _buckets.Count);
        for (int t = 0; t < ticks; t++)
        {
            var i = ticks == 1 ? 0 : (int)Math.Round((double)t * (_buckets.Count - 1) / (ticks - 1));
            var x = plot.Left + i * w + w / 2;
            var label = _buckets[i].FromUtc.ToLocalTime().ToString(AxisFormat, L.Culture);
            DrawText(dc, label, text, new Point(x, plot.Bottom + 3), alignRight: false, centerY: false, centerX: true);
        }
    }

    private void DrawText(DrawingContext dc, string s, IBrush brush, Point at, bool alignRight, bool centerY, bool centerX = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 11, brush);
        var x = alignRight ? at.X - ft.Width : centerX ? at.X - ft.Width / 2 : at.X;
        x = Math.Clamp(x, 0, Math.Max(0, Bounds.Width - ft.Width));
        var y = centerY ? at.Y - ft.Height / 2 : at.Y;
        dc.DrawText(ft, new Point(x, y));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var plot = Plot;
        var pos = e.GetPosition(this);
        var i = _buckets.Count == 0 || plot.Width <= 0 ? -1 : (int)Math.Floor((pos.X - plot.Left) / (plot.Width / _buckets.Count));
        if (i < 0 || i >= _buckets.Count) i = -1;
        if (i == _hover) return;
        _hover = i;
        InvalidateVisual();
        // reopened for every bar, so it follows the pointer
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, i < 0 ? null : Describe(_buckets[i]));
        if (i >= 0) ToolTip.SetIsOpen(this, true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    private static string Describe(UptimeBucket b)
    {
        var from = b.FromUtc.ToLocalTime();
        var to = b.ToUtc.ToLocalTime();
        var period = from.Date == to.AddSeconds(-1).Date
            ? $"{from.ToString("d", L.Culture)} {from:HH:mm}–{to:HH:mm}"
            : $"{from.ToString("g", L.Culture)} – {to.ToString("g", L.Culture)}";
        var lines = new List<string> { period };
        if (b.UpPercent is { } p)
        {
            lines.Add(L.F("Uptime.BucketUp", ViewModels.ServerNode.Pct(p)));
            if (b.DegradedShare > 0) lines.Add(L.F("Uptime.BucketDegraded", ViewModels.ServerNode.Pct(b.DegradedShare * 100)));
        }
        else lines.Add(L.Get("Uptime.NoData"));
        if (b.AvgLatencyMs is { } l) lines.Add(L.F("Uptime.BucketLatency", l));
        return string.Join("\n", lines);
    }
}
