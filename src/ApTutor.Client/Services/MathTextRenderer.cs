using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ApTutor.Client.Services;

/// Turns plain text containing inline math (delimited by $...$, e.g. "solve for $x^2 + 3x$ here")
/// into an Avalonia control tree — the thin, Avalonia-specific half of the equation-rendering work
/// (see MathParser's remarks for why this is a small purpose-built renderer rather than a real TeX
/// engine). Drop-in wherever a plain TextBlock.Text assignment previously held a practice-item or
/// learn-content string that might contain math; text with no "$" in it renders exactly as a single
/// TextBlock always did.
public static class MathTextRenderer
{
    public static Control Build(string text, double fontSize = 13, FontWeight fontWeight = FontWeight.Normal, IBrush? foreground = null)
    {
        var brush = foreground ?? Brushes.Black;
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var (segment, isMath) in SplitMathSpans(text))
        {
            if (segment.Length == 0) continue;

            if (isMath)
                panel.Children.Add(Render(MathParser.Parse(segment), fontSize, fontWeight, brush));
            else
                panel.Children.Add(new TextBlock { Text = segment, FontSize = fontSize, FontWeight = fontWeight, Foreground = brush, TextWrapping = TextWrapping.Wrap });
        }

        // Nothing matched (empty text, or a lone unclosed "$") — always return something so callers
        // can add this to a panel unconditionally rather than checking first.
        if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = text, FontSize = fontSize, FontWeight = fontWeight, Foreground = brush, TextWrapping = TextWrapping.Wrap });

        return panel;
    }

    /// Splits on paired $...$ delimiters. An unpaired trailing "$" (malformed input) is treated as
    /// plain text rather than swallowing the rest of the string into a bogus math span.
    private static IEnumerable<(string Segment, bool IsMath)> SplitMathSpans(string text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var start = text.IndexOf('$', pos);
            if (start < 0)
            {
                yield return (text[pos..], false);
                yield break;
            }

            if (start > pos) yield return (text[pos..start], false);

            var end = text.IndexOf('$', start + 1);
            if (end < 0)
            {
                yield return (text[start..], false); // unpaired "$" — show literally
                yield break;
            }

            yield return (text[(start + 1)..end], true);
            pos = end + 1;
        }
    }

    private static Control Render(MathNode node, double fontSize, FontWeight fontWeight, IBrush foreground) => node switch
    {
        MathRun run => new TextBlock { Text = run.Text, FontSize = fontSize, FontWeight = fontWeight, Foreground = foreground },
        MathGroup group => RenderGroup(group, fontSize, fontWeight, foreground),
        MathSuperscript sup => RenderScript(sup.Base, sup.Exponent, raised: true, fontSize, fontWeight, foreground),
        MathSubscript sub => RenderScript(sub.Base, sub.Sub, raised: false, fontSize, fontWeight, foreground),
        MathFraction frac => RenderFraction(frac, fontSize, fontWeight, foreground),
        MathSqrt sqrt => RenderSqrt(sqrt, fontSize, fontWeight, foreground),
        _ => new TextBlock { Text = "", FontSize = fontSize },
    };

    private static Control RenderGroup(MathGroup group, double fontSize, FontWeight fontWeight, IBrush foreground)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in group.Children)
            panel.Children.Add(Render(child, fontSize, fontWeight, foreground));
        return panel;
    }

    /// A simple text^text or text_text (both plain runs, entirely covered by the Unicode super/
    /// subscript tables) renders as ONE TextBlock with real Unicode super/subscript characters —
    /// looks better and is the overwhelming common case ("x^2", "a_n"). Anything structurally
    /// richer (a fraction as the base, a multi-character or unsupported exponent) falls back to a
    /// smaller, vertically-offset control instead of failing or mis-rendering.
    private static Control RenderScript(MathNode baseNode, MathNode scriptNode, bool raised, double fontSize, FontWeight fontWeight, IBrush foreground)
    {
        if (baseNode is MathRun baseRun && scriptNode is MathRun scriptRun &&
            TryMapToUnicodeScript(scriptRun.Text, raised, out var mapped))
        {
            return new TextBlock { Text = baseRun.Text + mapped, FontSize = fontSize, FontWeight = fontWeight, Foreground = foreground };
        }

        var baseControl = Render(baseNode, fontSize, fontWeight, foreground);
        var scriptControl = Render(scriptNode, fontSize * 0.7, fontWeight, foreground);
        scriptControl.VerticalAlignment = raised ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        scriptControl.Margin = raised ? new Avalonia.Thickness(0, -fontSize * 0.3, 0, 0) : new Avalonia.Thickness(0, 0, 0, -fontSize * 0.15);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { baseControl, scriptControl },
        };
    }

    private static bool TryMapToUnicodeScript(string text, bool raised, out string mapped)
    {
        var table = raised ? MathParser.SuperscriptDigits : MathParser.SubscriptDigits;
        var chars = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (!table.TryGetValue(text[i], out var c)) { mapped = ""; return false; }
            chars[i] = c;
        }
        mapped = new string(chars);
        return true;
    }

    /// A small Grid — numerator, a 1px rule, denominator — rather than the "num/den" inline text a
    /// pure-text fallback would use: fractions are common enough in PSAT/Physics-level questions
    /// that a real stacked rendering is worth the extra control here (see MathParser's remarks on
    /// which constructs get this treatment versus a plain-text approximation).
    private static Control RenderFraction(MathFraction frac, double fontSize, FontWeight fontWeight, IBrush foreground)
    {
        var num = Render(frac.Numerator, fontSize * 0.85, fontWeight, foreground);
        num.HorizontalAlignment = HorizontalAlignment.Center;
        var den = Render(frac.Denominator, fontSize * 0.85, fontWeight, foreground);
        den.HorizontalAlignment = HorizontalAlignment.Center;

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(num, 0);
        var rule = new Border { Height = 1, Background = foreground, Margin = new Avalonia.Thickness(2, 1, 2, 1) };
        Grid.SetRow(rule, 1);
        Grid.SetRow(den, 2);
        grid.Children.Add(num);
        grid.Children.Add(rule);
        grid.Children.Add(den);
        return grid;
    }

    private static Control RenderSqrt(MathSqrt sqrt, double fontSize, FontWeight fontWeight, IBrush foreground) =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "√(", FontSize = fontSize, FontWeight = fontWeight, Foreground = foreground },
                Render(sqrt.Radicand, fontSize, fontWeight, foreground),
                new TextBlock { Text = ")", FontSize = fontSize, FontWeight = fontWeight, Foreground = foreground },
            },
        };
}
