using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.StatInspector;

// More house-kit building blocks (Stellar Design Language §4; reference Maestro Plugin.Style.Blocks.cs): embedded art,
// the accent icon tile, ToolHeader, filled list slots, the round icon button, and a cached string formatter so the
// per-refresh text Funcs never build a string unless their inputs changed (Plugin-Performance-Rules §2).
public sealed partial class Plugin
{
    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource (csproj EmbeddedResource LogicalName "Stellar.StatInspector.<file>") and kept: ImageElement
    // Funcs are polled. A missing resource caches null (the element then draws nothing) instead of re-reading every poll.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.StatInspector." + file);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[file] = png;
        return png;
    }

    // ── icon tile ─────────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with a flat white glyph centred on it (art can't follow the theme, so the
    // colour lives in the tile). The glyph is PRE-DRAWN at the exact size it is shown (Resources\stat-header-<px>.png,
    // 16/24/32/64/128, each hinted to whole pixels by tools\make-header-icon.py) — never one master scaled down, which
    // turns thin bars soft grey. 40 px tile → the 24 px glyph with 8 px all round. Radius ≈ 22 % of the side (9 at 40).
    private const float ToolIconSize  = 40f;
    private const int   ToolGlyphSize = 24;

    private HudElement IconBox(string glyphPng, float size, int glyph)
    {
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { new ImageElement(() => CachedPng(glyphPng), glyph, glyph) },
                Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // Window title block: 40 px icon + bold title over a live status line.
    private HudElement ToolHeader(string glyphPng, Func<string> title, Func<string> status) => new RowElement(
        new HudElement[]
        {
            IconBox(glyphPng, ToolIconSize, ToolGlyphSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
                new TextElement(status, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
            }, Gap: 2f), Weight: 1f),
        }, Gap: 10f);

    // ── list slots ────────────────────────────────────────────────────────────────────────────────────────────────
    // A filled list row = 26 px tile + 2 px transparent spacer (28 px pitch). ⚠️ A VirtualList slot STRETCHES its one
    // child to the full RowHeight, so a bare 26 px tile in a 28 px slot fills all 28 and the rows touch (Maestro,
    // in-game 2026-11-06); this column's preferred height IS the pitch, so nothing is stretched.
    private const float ListTileHeight = 26f;
    private const float ListRowGap     = 2f;
    private const float ListRowPitch   = ListTileHeight + ListRowGap;   // the VirtualList RowHeight

    private static HudElement ListSlot(HudElement tile) => new ColumnElement(new HudElement[]
    {
        tile,
        new SpacerElement(0f, ListRowGap),
    }, Gap: 0.01f);

    // Viewport height that shows exactly `rows` rows: the last row's 2 px gap falls outside, so the list ends on a tile.
    private static float ListHeight(int rows) => rows * ListRowPitch - ListRowGap;

    // ── round icon button ─────────────────────────────────────────────────────────────────────────────────────────
    // A 28 × 28 circle (Neutral fill, lighter on hover) around a Bare selectable with a centred plain glyph ("✕"
    // renders in the plain menu font). Exactly its side: one Row[28 px strut, a 28 px-wide glyph box].
    private const float IconButtonSize = 28f;

    private HudElement IconButton(Func<string> glyph, Action onClick)
    {
        var hover = new HoverFlag();
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, IconButtonSize),
                new TextElement(glyph, Color: () => hover.On ? NeutralText() : MutedColor(), Width: IconButtonSize,
                    Align: TextAlign.Center, NoWrap: true),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(NeutralFill()) : NeutralFill()),
            CornerRadius = RoundRadius(IconButtonSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // ── cached formatter ──────────────────────────────────────────────────────────────────────────────────────────
    // A localized template + up to two numbers → the composed string, rebuilt only when the template (a language
    // switch) or a number changes. The text Funcs poll every refresh; this keeps them allocation-free in steady state.
    private sealed class FmtCache
    {
        private string? _tpl;
        private long _a = long.MinValue, _b = long.MinValue;
        private string _s = string.Empty;

        public string Get(string tpl, long a, long b = 0)
        {
            if (a != _a || b != _b || !string.Equals(tpl, _tpl, StringComparison.Ordinal))
            {
                _tpl = tpl; _a = a; _b = b;
                try { _s = string.Format(CultureInfo.CurrentCulture, tpl, a, b); }
                catch (FormatException) { _s = tpl; }   // a broken translation shows its raw text, never throws per refresh
            }
            return _s;
        }
    }
}
