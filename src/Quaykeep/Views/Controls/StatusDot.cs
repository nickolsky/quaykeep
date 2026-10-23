using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Quaykeep.Views.Controls;

/// <summary>
/// Status dot: <see cref="Dot"/> = ok / bad / warn / busy / off, hidden when empty. Colors come from the theme
/// (success, critical, caution, attention, disabled).
/// </summary>
public sealed class StatusDot : Control
{
    public static readonly StyledProperty<string?> DotProperty = AvaloniaProperty.Register<StatusDot, string?>(nameof(Dot));

    static StatusDot()
    {
        AffectsRender<StatusDot>(DotProperty);
        WidthProperty.OverrideDefaultValue<StatusDot>(9);
        HeightProperty.OverrideDefaultValue<StatusDot>(9);
        VerticalAlignmentProperty.OverrideDefaultValue<StatusDot>(Avalonia.Layout.VerticalAlignment.Center);
    }

    public StatusDot()
    {
        Margin = new Thickness(2, 0, 7, 0);
        IsVisible = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public string? Dot
    {
        get => GetValue(DotProperty);
        set => SetValue(DotProperty, value);
    }

    public static string BrushKey(string? dot) => dot switch
    {
        "ok" => "SystemFillColorSuccessBrush",
        "bad" => "SystemFillColorCriticalBrush",
        "warn" => "SystemFillColorCautionBrush",
        "busy" => "SystemFillColorAttentionBrush",
        _ => "ControlStrongFillColorDisabledBrush",
    };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DotProperty) IsVisible = !string.IsNullOrEmpty(Dot);
    }

    public override void Render(DrawingContext context)
    {
        var r = Math.Min(Bounds.Width, Bounds.Height) / 2;
        context.DrawEllipse(this.Brush(BrushKey(Dot)), null, new Point(Bounds.Width / 2, Bounds.Height / 2), r, r);
    }
}

/// <summary>Small usage bar (CPU, memory, disk): <see cref="Value"/> 0..100, <see cref="Level"/> ok / warn / bad picks the color.</summary>
public sealed class UsageBar : Control
{
    public static readonly StyledProperty<double?> ValueProperty = AvaloniaProperty.Register<UsageBar, double?>(nameof(Value));
    public static readonly StyledProperty<string?> LevelProperty = AvaloniaProperty.Register<UsageBar, string?>(nameof(Level));

    static UsageBar()
    {
        AffectsRender<UsageBar>(ValueProperty, LevelProperty);
        WidthProperty.OverrideDefaultValue<UsageBar>(44);
        HeightProperty.OverrideDefaultValue<UsageBar>(6);
        VerticalAlignmentProperty.OverrideDefaultValue<UsageBar>(Avalonia.Layout.VerticalAlignment.Center);
    }

    public UsageBar()
    {
        Margin = new Thickness(0, 0, 8, 0);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public double? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string? Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        context.DrawRectangle(this.Brush("ControlStrongFillColorDisabledBrush"), null, rect, 3, 3);
        var key = Level switch { "warn" => "SystemFillColorCautionBrush", "bad" => "SystemFillColorCriticalBrush", _ => "SystemFillColorSuccessBrush" };
        var w = Math.Max(3, rect.Width * Math.Clamp(Value ?? 0, 0, 100) / 100);
        context.DrawRectangle(this.Brush(key), null, new Rect(0, 0, w, rect.Height), 3, 3);
    }
}
