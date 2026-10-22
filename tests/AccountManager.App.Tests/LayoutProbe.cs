using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AccountManager.App.Tests;

/// <summary>
/// Finds text that is drawn over other text or cut off: after a real layout pass it looks at every visible
/// <see cref="TextBlock"/>, works out which part of it can actually be seen (ancestors that clip, the window itself)
/// and reports overlaps, text that is wider than its box without trimming, and text cut off by a clipping cell or the window.
/// A text that scrolls inside a ScrollViewer is allowed to be partly hidden vertically; that is what scrolling is for.
/// </summary>
internal static class LayoutProbe
{
    private sealed record Item(TextBlock Text, Rect Full, Rect Visible);

    public static IReadOnlyList<string> Problems(Window window)
    {
        window.UpdateLayout();
        var windowRect = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
        var items = new List<Item>();
        var problems = new List<string>();

        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.IsEffectivelyVisible || string.IsNullOrEmpty(text.Text)) { continue; }
            if (text.Bounds.Width <= 0 || text.Bounds.Height <= 0) { continue; }
            var full = ToWindow(text, window, new Rect(text.Bounds.Size));
            var visible = full.Intersect(windowRect);
            var describe = Describe(text);

            var inScroller = false;
            // Ancestors that cut the text off.
            for (Visual? ancestor = text.GetVisualParent(); ancestor is not null && !ReferenceEquals(ancestor, window); ancestor = ancestor.GetVisualParent())
            {
                if (ancestor is ScrollContentPresenter presenter)
                {
                    inScroller = true;
                    var area = ToWindow(presenter, window, new Rect(presenter.Bounds.Size));
                    var scroller = presenter.FindAncestorOfType<ScrollViewer>();
                    // Vertical scrolling hides what is below the fold on purpose; horizontal overflow of a
                    // ScrollViewer without a horizontal scrollbar is a cut-off.
                    if (scroller?.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                        && (full.Right > area.Right + 0.5 || full.X < area.X - 0.5))
                    {
                        problems.Add("cut off horizontally by its scroll area: " + describe);
                    }

                    visible = visible.Intersect(area);
                }
                else if (ancestor is Control { ClipToBounds: true } clipper)
                {
                    var area = ToWindow(clipper, window, new Rect(clipper.Bounds.Size));
                    // Inside scrolling content only the horizontal direction counts (see the class comment).
                    var cutHorizontally = full.Right > area.Right + 0.5 || full.X < area.X - 0.5;
                    var cutVertically = !inScroller && (full.Bottom > area.Bottom + 0.5 || full.Y < area.Y - 0.5);
                    if (cutHorizontally || cutVertically)
                    {
                        problems.Add("cut off by its cell (" + clipper.GetType().Name + "): " + describe);
                    }

                    visible = visible.Intersect(area);
                }
            }

            if (!inScroller && (full.Right > windowRect.Right + 0.5 || full.Bottom > windowRect.Bottom + 0.5 || full.X < -0.5 || full.Y < -0.5))
            {
                problems.Add("outside the window: " + describe);
            }

            // Text wider (or, when wrapping, taller) than its box and nothing that trims it.
            if (text.TextTrimming is null || ReferenceEquals(text.TextTrimming, TextTrimming.None))
            {
                var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
                if (text.TextWrapping == TextWrapping.NoWrap)
                {
                    var width = Measure(text, typeface, double.PositiveInfinity).Width;
                    if (width > text.Bounds.Width + 0.75) { problems.Add($"text is {width:0.#}px wide in a {text.Bounds.Width:0.#}px box without trimming: {describe}"); }
                }
                else
                {
                    var height = Measure(text, typeface, text.Bounds.Width).Height;
                    if (height > text.Bounds.Height + 1.5) { problems.Add($"wrapped text is {height:0.#}px tall in a {text.Bounds.Height:0.#}px box: {describe}"); }
                }
            }

            if (visible.Width > 0 && visible.Height > 0) { items.Add(new Item(text, full, visible)); }
        }

        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                // A one-pixel tolerance: neighbouring lines share an edge.
                var a = items[i].Visible.Deflate(1);
                var b = items[j].Visible.Deflate(1);
                if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0) { continue; }
                if (a.Intersects(b)) { problems.Add("overlap: " + Describe(items[i].Text) + "  <->  " + Describe(items[j].Text)); }
            }
        }

        return problems.Distinct().ToList();
    }

    /// <summary>Fails with the full list of problems (an xunit collection assertion would truncate it).</summary>
    public static void AssertClean(Window window)
    {
        var problems = Problems(window);
        Xunit.Assert.True(problems.Count == 0, window.GetType().Name + " " + window.Bounds.Width + "x" + window.Bounds.Height + ":\n" + string.Join("\n", problems));
    }

    public static Rect ToWindow(Visual visual, Visual window, Rect local)
    {
        var corners = new[] { local.TopLeft, local.TopRight, local.BottomLeft, local.BottomRight };
        var points = corners.Select(c => visual.TranslatePoint(c, window) ?? c).ToList();
        var left = points.Min(p => p.X);
        var top = points.Min(p => p.Y);
        return new Rect(left, top, points.Max(p => p.X) - left, points.Max(p => p.Y) - top);
    }

    private static Size Measure(TextBlock text, Typeface typeface, double maxWidth)
    {
        var formatted = new FormattedText(text.Text ?? string.Empty, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, text.FontSize, Brushes.White);
        if (!double.IsPositiveInfinity(maxWidth)) { formatted.MaxTextWidth = Math.Max(1, maxWidth); }
        return new Size(formatted.Width, formatted.Height);
    }

    private static string Describe(TextBlock text)
    {
        var value = text.Text ?? string.Empty;
        return "\"" + (value.Length > 40 ? value[..40] + "…" : value) + "\"";
    }
}
