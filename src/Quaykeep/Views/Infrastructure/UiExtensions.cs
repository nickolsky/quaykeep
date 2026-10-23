using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Quaykeep.Views;

public static class UiExtensions
{
    /// <summary>WPF's SetResourceReference: the property follows a theme resource (dark / light switches included).</summary>
    public static void SetResourceReference(this Control control, AvaloniaProperty property, string key) =>
        control.Bind(property, control.GetResourceObservable(key));

    /// <summary>A theme brush by its key (the current theme); <paramref name="fallback"/> when missing.</summary>
    public static IBrush Brush(this Control control, string key, IBrush? fallback = null) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var value) && value is IBrush b ? b : fallback ?? Brushes.Gray;

    /// <summary>The nearest ancestor of a type, starting from the element itself.</summary>
    public static T? FindSelfOrAncestor<T>(this Visual? visual) where T : class
    {
        for (var v = visual; v != null; v = v.GetVisualParent())
            if (v is T t) return t;
        return null;
    }

    /// <summary>Opens a web page or a folder with the system's default handler.</summary>
    public static void OpenWithShell(string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return;
        }
        var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
        psi.ArgumentList.Add(target);
        Process.Start(psi)?.Dispose();
    }
}

/// <summary>Value converters for bindings that WPF did with DataTriggers.</summary>
public static class Conv
{
    /// <summary>True when the value's text equals the parameter ("ok", "server", …).</summary>
    public static readonly IValueConverter Equal = new FuncValueConverter<object?, object?, bool>((v, p) => string.Equals(v?.ToString() ?? "", p?.ToString() ?? "", StringComparison.Ordinal));

    /// <summary>True when the value's text differs from the parameter.</summary>
    public static readonly IValueConverter NotEqual = new FuncValueConverter<object?, object?, bool>((v, p) => !string.Equals(v?.ToString() ?? "", p?.ToString() ?? "", StringComparison.Ordinal));

    /// <summary>True when the value's text is one of the parameter's "|"-separated words ("server|forward").</summary>
    public static readonly IValueConverter OneOf = new FuncValueConverter<object?, object?, bool>((v, p) =>
        (p?.ToString() ?? "").Split('|').Contains(v?.ToString() ?? "", StringComparer.Ordinal));
}

/// <summary>A converter from a function, for code-behind bindings.</summary>
public sealed class LambdaConverter(Func<object?, object?> convert) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
