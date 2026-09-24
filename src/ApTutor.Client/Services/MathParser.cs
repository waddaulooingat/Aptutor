namespace ApTutor.Client.Services;

// Equation rendering (see the learn-quiz-mode-switch plan's Part C). No viable native Avalonia
// LaTeX-rendering library exists at this project's Avalonia/net8.0 combination: CSharpMath.Avalonia's
// only stable release (0.5.1) targets Avalonia 0.10 (API-incompatible with this project's Avalonia
// 12.1.0), and its only Avalonia-12-compatible release (1.0.0-pre.2) requires net10.0, not net8.0.
// Content Admin's web review page uses real KaTeX instead (see _Layout.cshtml) since a browser can
// load that trivially — this is the Shell's side of a deliberate asymmetry: full LaTeX fidelity on
// review, a small purpose-built SUBSET here covering what PSAT/algebra-level equations actually
// need (superscripts, subscripts, \frac, \sqrt, common symbol macros), not a general TeX engine.
//
// This file is the pure parsing layer — zero Avalonia dependency, fully unit-testable. See
// MathTextRenderer for the thin Avalonia-specific layer that walks this AST into actual controls.

public abstract record MathNode;

/// Plain text/symbols with no further structure — already macro-substituted (e.g. "\pi" becomes a
/// MathRun("π"), not left as literal backslash-pi for the renderer to re-interpret).
public sealed record MathRun(string Text) : MathNode;

/// An ordered sequence of nodes rendered inline, one after another — the parse result for a whole
/// expression, a {...} group, or a \frac/\sqrt argument.
public sealed record MathGroup(IReadOnlyList<MathNode> Children) : MathNode;

public sealed record MathSuperscript(MathNode Base, MathNode Exponent) : MathNode;
public sealed record MathSubscript(MathNode Base, MathNode Sub) : MathNode;
public sealed record MathFraction(MathNode Numerator, MathNode Denominator) : MathNode;
public sealed record MathSqrt(MathNode Radicand) : MathNode;

public static class MathParser
{
    // Deliberately small — covers common PSAT/algebra/physics notation, not the full LaTeX symbol
    // table. An unrecognized \macro falls back to showing its name literally (see ParseAtom) rather
    // than silently dropping it, so a gap here is visible/debuggable, not silently wrong.
    private static readonly IReadOnlyDictionary<string, string> SymbolMacros = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pi"] = "π", ["theta"] = "θ", ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
        ["mu"] = "μ", ["sigma"] = "σ", ["omega"] = "ω", ["lambda"] = "λ",
        ["times"] = "×", ["div"] = "÷", ["pm"] = "±", ["cdot"] = "·",
        ["leq"] = "≤", ["geq"] = "≥", ["neq"] = "≠", ["approx"] = "≈", ["infty"] = "∞",
    };

    // Single-character exponents/subscripts using only these glyphs render as true Unicode
    // super/subscripts (e.g. "x^2" -> "x²") rather than a nested control — reads better and covers
    // the overwhelming majority of real cases. Multi-character or unsupported exponents fall back to
    // a nested, smaller-text control instead (see MathTextRenderer.Render).
    internal static readonly IReadOnlyDictionary<char, char> SuperscriptDigits = new Dictionary<char, char>
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴',
        ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['n'] = 'ⁿ',
    };

    internal static readonly IReadOnlyDictionary<char, char> SubscriptDigits = new Dictionary<char, char>
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄',
        ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋',
    };

    public static MathNode Parse(string latex)
    {
        var pos = 0;
        var group = ParseSequence(latex, ref pos, stopAtClosingBrace: false);
        return group;
    }

    private static MathGroup ParseSequence(string s, ref int pos, bool stopAtClosingBrace)
    {
        var items = new List<MathNode>();
        while (pos < s.Length && !(stopAtClosingBrace && s[pos] == '}'))
        {
            var atom = ParseAtom(s, ref pos);
            atom = ParsePostfix(s, ref pos, atom);
            items.Add(atom);
        }
        return new MathGroup(items);
    }

    /// Consumes any trailing ^.../_... on the atom just parsed — a superscript can itself carry a
    /// further subscript-of-the-exponent etc., so this loops rather than handling just one.
    private static MathNode ParsePostfix(string s, ref int pos, MathNode atom)
    {
        while (pos < s.Length && (s[pos] == '^' || s[pos] == '_'))
        {
            var isSuper = s[pos] == '^';
            pos++;
            var arg = ParseAtom(s, ref pos);
            atom = isSuper ? new MathSuperscript(atom, arg) : new MathSubscript(atom, arg);
        }
        return atom;
    }

    private static MathNode ParseAtom(string s, ref int pos)
    {
        if (pos >= s.Length) return new MathRun("");

        if (s[pos] == '{')
        {
            pos++; // consume '{'
            var group = ParseSequence(s, ref pos, stopAtClosingBrace: true);
            if (pos < s.Length && s[pos] == '}') pos++; // consume '}'
            return group;
        }

        if (s[pos] == '\\')
        {
            return ParseCommand(s, ref pos);
        }

        // A plain run: consume characters up to the next special character, so "x+3" is one MathRun
        // rather than three, avoiding pointless fragmentation of ordinary text.
        var start = pos;
        while (pos < s.Length && s[pos] is not ('^' or '_' or '{' or '}' or '\\'))
            pos++;
        return new MathRun(s[start..pos]);
    }

    private static MathNode ParseCommand(string s, ref int pos)
    {
        pos++; // consume '\'
        var nameStart = pos;
        while (pos < s.Length && char.IsLetter(s[pos])) pos++;
        var name = s[nameStart..pos];

        switch (name)
        {
            case "frac":
            {
                var num = ParseBracedArg(s, ref pos);
                var den = ParseBracedArg(s, ref pos);
                return new MathFraction(num, den);
            }
            case "sqrt":
            {
                var radicand = ParseBracedArg(s, ref pos);
                return new MathSqrt(radicand);
            }
            default:
                return new MathRun(SymbolMacros.TryGetValue(name, out var symbol) ? symbol : $"\\{name}");
        }
    }

    /// \frac and \sqrt both require a {...} argument immediately following — an argument that isn't
    /// braced (a bare command with no following group) degrades to an empty group rather than
    /// throwing, since a malformed equation shouldn't crash the practice panel around it.
    private static MathNode ParseBracedArg(string s, ref int pos)
    {
        if (pos >= s.Length || s[pos] != '{') return new MathGroup(Array.Empty<MathNode>());
        return ParseAtom(s, ref pos);
    }
}
