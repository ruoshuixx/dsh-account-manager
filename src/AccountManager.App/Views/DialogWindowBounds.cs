using Avalonia;
using Avalonia.Controls;

namespace AccountManager.App.Views;

/// <summary>Keep native title bars and fixed dialog actions inside the monitor's work area.</summary>
internal static class DialogWindowBounds
{
    // Screen pixels to logical units, minus room for native decorations and a small margin.
    private const double WidthMargin = 32;
    private const double HeightMargin = 64;

    // Room kept for native decorations (title bar, borders) around the main window, in device pixels.
    private const double MainWidthMarginPx = 16;
    private const double MainHeightMarginPx = 80;

    /// <summary>Main window zoom, minimum size and size after fitting them to a work area.</summary>
    internal readonly record struct MainBounds(double Zoom, double MinWidth, double MinHeight, double Width, double Height);

    /// <summary>
    /// The layout is designed for a minimum size in logical units. At a high display scale that minimum
    /// can be larger than the whole monitor, which leaves the window cut off (and some window managers
    /// refuse to maximize it). When the minimum does not fit, the content is zoomed down uniformly so
    /// the designed layout still fits, and the window's minimum and default sizes shrink with it.
    /// </summary>
    internal static MainBounds FitMainBounds(
        double areaWidth, double areaHeight, double scaling,
        double designMinWidth, double designMinHeight, double designWidth, double designHeight)
    {
        var maxWidth = Math.Max(1, (areaWidth - MainWidthMarginPx) / scaling);
        var maxHeight = Math.Max(1, (areaHeight - MainHeightMarginPx) / scaling);
        var zoom = Math.Min(1.0, Math.Min(maxWidth / designMinWidth, maxHeight / designMinHeight));
        var minWidth = designMinWidth * zoom;
        var minHeight = designMinHeight * zoom;
        return new MainBounds(
            zoom,
            minWidth,
            minHeight,
            Math.Clamp(designWidth * zoom, minWidth, Math.Max(minWidth, maxWidth)),
            Math.Clamp(designHeight * zoom, minHeight, Math.Max(minHeight, maxHeight)));
    }

    /// <summary>A dialog's limits and size inside a work area (all in logical units).</summary>
    internal readonly record struct DialogBounds(double MinWidth, double MinHeight, double MaxWidth, double MaxHeight, double Width, double Height);

    /// <summary>
    /// Pure fitting maths: the dialog may be at most the work area minus decorations; width and height (NaN when the
    /// dialog sizes itself to its content) are clamped into the limits.
    /// </summary>
    internal static DialogBounds ComputeDialogBounds(
        double areaWidth, double areaHeight, double scaling, double width, double height,
        double minWidth = DefaultMinWidth, double minHeight = DefaultMinHeight)
    {
        var maxWidth = Math.Max(1, areaWidth / scaling - WidthMargin);
        var maxHeight = Math.Max(1, areaHeight / scaling - HeightMargin);
        var lowWidth = Math.Min(minWidth, maxWidth);
        var lowHeight = Math.Min(minHeight, maxHeight);
        return new DialogBounds(
            lowWidth, lowHeight, maxWidth, maxHeight,
            double.IsNaN(width) ? width : Math.Clamp(width, lowWidth, maxWidth),
            double.IsNaN(height) ? height : Math.Clamp(height, lowHeight, maxHeight));
    }

    internal const double DefaultMinWidth = 380;
    internal const double DefaultMinHeight = 260;

    /// <summary>
    /// Keeps a dialog inside the work area now and whenever it moves to another screen or the scale changes.
    /// Dialogs that size themselves to their content pass a small <paramref name="minHeight"/>.
    /// </summary>
    public static void KeepOnScreen(Window dialog, double minWidth = DefaultMinWidth, double minHeight = DefaultMinHeight)
    {
        dialog.ScalingChanged += (_, _) => Fit(dialog, minWidth: minWidth, minHeight: minHeight);
        dialog.PositionChanged += (_, _) => Fit(dialog, minWidth: minWidth, minHeight: minHeight);
    }

    public static void Fit(Window dialog, Window? owner = null, bool center = false,
        double minWidth = DefaultMinWidth, double minHeight = DefaultMinHeight)
    {
        var screen = dialog.Screens.ScreenFromWindow(owner ?? dialog) ?? dialog.Screens.Primary;
        if (screen is null) { return; }
        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        if (area.Width <= 0 || area.Height <= 0 || scaling <= 0) { return; }

        // Screen pixels must be converted to Avalonia logical units. Reserve space for
        // native decorations as well as a small margin; taskbars are already excluded.
        var fit = ComputeDialogBounds(area.Width, area.Height, scaling, dialog.Width, dialog.Height, minWidth, minHeight);
        dialog.MinWidth = fit.MinWidth;
        dialog.MinHeight = fit.MinHeight;
        dialog.MaxWidth = fit.MaxWidth;
        dialog.MaxHeight = fit.MaxHeight;
        dialog.Width = fit.Width;
        dialog.Height = fit.Height;

        // A dialog that sizes itself to its content has no height yet; the window manager centres it on its owner.
        if (!center || double.IsNaN(dialog.Height)) { return; }
        var width = (int)Math.Ceiling(dialog.Width * scaling);
        var height = (int)Math.Ceiling((dialog.Height + 40) * scaling);
        var x = owner is null ? area.X + (area.Width - width) / 2
            : owner.Position.X + (int)(owner.Bounds.Width * scaling - width) / 2;
        var y = owner is null ? area.Y + (area.Height - height) / 2
            : owner.Position.Y + (int)(owner.Bounds.Height * scaling - height) / 2;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = new PixelPoint(
            Math.Clamp(x, area.X + 8, Math.Max(area.X + 8, area.Right - width - 8)),
            Math.Clamp(y, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - height - 8)));
    }
}
