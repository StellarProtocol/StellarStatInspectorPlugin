using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using AbsUv = Stellar.Abstractions.Domain.UvRect;

namespace Stellar.StatInspector;

/// <summary>
/// Mini-HUD — a transparent in-world overlay (HudOverlay chrome), migrated to the uGUI window
/// toolkit (Phase E). A grid of <see cref="_columns"/> stat cells, each an atlas icon + clipped
/// label + right-aligned live value; a value that changes in combat flashes green (▲) / red (▼)
/// via the value's colour Func, fading to neutral over <see cref="DeltaFadeSeconds"/>. States:
/// populated grid / "no stats" CTA / not-in-world placeholder (ConditionalElement). The grid is
/// built ONCE over a fixed cell pool; cells read the per-poll <see cref="_miniSnapshot"/> (the
/// sorted selected ids, refreshed on Update — no allocation in Funcs). A gear (SelectableElement
/// over an atlas SpriteElement) toggles the Settings window; F6 toggles the mini-HUD itself.
/// </summary>
public sealed partial class Plugin
{
    private const float MiniHudColW = 178f;   // per-column width → window width = columns × this
    private const int   MiniHudMaxCells = 64; // fixed cell pool (the selectable set never exceeds this in practice)

    // Selected ids, sorted; refreshed on Update so the cell Funcs never allocate/scan.
    private readonly List<int> _miniSnapshot = new();

    private IWindowControl BuildAndRegisterMiniHud()
    {
        var spec = new WindowSpec(
            Id:          "statinspector.mini-hud",
            Title:       "StatInspector",
            DefaultRect: new WindowRect(1644f, 1192f, _columns * MiniHudColW, 184f),
            Category:    WindowCategory.HUD,
            Style:       WindowPanelStyle.Borderless)   // chrome-less custom overlay (NOT a titled glass panel)
        {
            StartVisible            = _windowSection.Get<bool>("minihud_visible", true),
            Draggable               = true,   // required so BuildChrome registers the (edit-only) whole-frame drag
            EditModeDragOnly        = true,   // gameplay overlay: moves only in layout edit-mode (Shift+`)
            HideUntilInWorld        = true,
            AutoHideBehindGameMenus = true,
        };
        return _services.Windows.Register(new WindowRegistration(spec, BuildMiniRoot()));
    }

    // Built ONCE. Header (gear) + a Conditional state machine: in-world → (populated grid | empty CTA);
    // not in-world → placeholder. The grid is a ListElement so it shows the first N (=selected count)
    // of a fixed cell pool, gridded into `_columns` columns.
    private HudElement BuildMiniRoot()
    {
        // Fixed-width cell around the clickable gear — SelectableElement force-expands its child to the column
        // width, so without this the 16px gear sprite stretches into a full-width streak.
        var gear = new CellElement(
            new SelectableElement(
                new ImageElement(() => _iconAtlas.GearPng, 15, 15),   // clean standalone cog (not the emoji atlas cell)
                OnClick: ToggleSettingsFromGear),
            Width: 26f);
        // Thin titled header strip (design D): a faint "STATS" caption (left) + gear (right) over a divider, so
        // the gear reads as part of an intentional header instead of a disconnected floating corner icon.
        var header = new RowElement(new HudElement[]
        {
            new TextElement(() => "STATS", () => MutedRgba(), Shadow: true),
            new SpacerElement(),
            gear,
        });

        var cells = new HudElement[MiniHudMaxCells];
        for (var i = 0; i < MiniHudMaxCells; i++) { var idx = i; cells[idx] = BuildStatCell(idx); }
        // Wide grid cells (the framework default 120px is too tight for icon+label+value) so column names read.
        var grid = new ListElement(() => _miniSnapshot.Count, cells, Columns: _columns, CellWidth: MiniHudColW, CellHeight: 24f);

        var empty = new ColumnElement(new HudElement[]
        {
            new TextElement(() => "No stats selected.", () => MutedRgba(), Shadow: true),
            new ButtonElement(() => "Open settings", () => _settingsWindow.SetVisible(true)),
        });
        var populated = new ConditionalElement(() => _miniSnapshot.Count > 0, grid, empty);

        var notInWorld = new TextElement(() => "Not in-world — values unavailable.", () => MutedRgba(), Shadow: true);
        var body = new ConditionalElement(() => _services.PlayerStats.IsAvailable, populated, notInWorld);

        // Tight gap (default Column gap is the 12px section spacing — too airy around the header divider here).
        return new ColumnElement(new HudElement[] { header, new SeparatorElement(), body }, Gap: 2f);
    }

    // One stat cell: icon + clipped label + right-aligned value (delta-flash colour Func). Reads _miniSnapshot[slot].
    private HudElement BuildStatCell(int slot) => new RowElement(new HudElement[]
    {
        // Fixed-width icon cell — as the first child of a grid-cell Row, a bare SpriteElement collapses to 0;
        // a CellElement pins its slot so the 16px icon renders.
        // Fits the wide grid cell (CellWidth = MiniHudColW): icon 18 + label 92 + value 52 + gaps ≈ 170. NO
        // Spacer (the Borderless fitter fights a flexible spacer). Delta direction is shown by the value COLOUR
        // (green up / red down) — the inline "▲ N" number is dropped (also dodges the ▲ font-risk glyph).
        new CellElement(new SpriteElement(() => _iconAtlas.Png, IconUvForSlot(slot), 16, 16, UvFunc: () => IconUvForSlot(slot)), Width: 18f),
        new TextElement(() => SlotLabel(slot), () => MutedRgba(), Width: 92f, Shadow: true),
        new TextElement(() => SlotValue(slot), () => SlotDeltaColor(slot), Width: 52f, Align: TextAlign.Right, Shadow: true),
    }, Gap: 4f);

    private int AttrAt(int slot) => slot < _miniSnapshot.Count ? _miniSnapshot[slot] : -1;

    private AbsUv IconUvForSlot(int slot)
    {
        var id = AttrAt(slot);
        return StatIconAtlas.UvRectFor(id >= 0 ? IconIndexFor(id) : 0);
    }

    private string SlotLabel(int slot) { var id = AttrAt(slot); return id >= 0 ? ResolveLabel(id) : string.Empty; }

    private string SlotValue(int slot)
    {
        var id = AttrAt(slot);
        if (id < 0) return string.Empty;
        var raw = _services.PlayerStats.TryGetAttribute(id);
        return raw.HasValue ? FormatValue(id, raw.Value) : "—";   // delta direction is shown via SlotDeltaColor
    }

    // Delta-flash: neutral HudText when no recent change; otherwise lerp HudText→HpFill (up)/Warning (down) by fade.
    private ColorRgba? SlotDeltaColor(int slot)
    {
        var id = AttrAt(slot);
        if (id < 0) return null;
        var fade = DeltaFade(id, out var dir);
        var t = _services.Theme.Colors;
        // White (not the muted cream) — the borderless HUD has no background, so crisp white + the dark text
        // outline reads cleanly over any world background. Delta still flashes green/red.
        if (dir == 0 || fade <= 0f) return new ColorRgba(1f, 1f, 1f, 1f);
        var a = dir > 0 ? t.HpFill : t.Warning;
        var b = t.HudText;
        return new ColorRgba(Lerp(b.R, a.R, fade), Lerp(b.G, a.G, fade), Lerp(b.B, a.B, fade), 1f);
    }

    // 0 = neutral; otherwise 1→0 over DeltaFadeSeconds. dir = +1 up / -1 down / 0 none.
    private float DeltaFade(int attrId, out int dir)
    {
        dir = 0;
        if (!_changes.TryGetValue(attrId, out var ch)) return 0f;
        var age = UnityEngine.Time.realtimeSinceStartup - ch.At;
        if (age >= DeltaFadeSeconds) return 0f;
        dir = ch.Delta > 0 ? 1 : -1;
        return 1f - age / DeltaFadeSeconds;
    }

    private string ResolveLabel(int attrId)
    {
        var label = ResolveAttrName(attrId);
        if (string.IsNullOrEmpty(label)) { LogOrphanAttr(attrId); return "#" + attrId; }
        if (_disambiguationLabel.TryGetValue(attrId, out var prefix)) label = prefix + " " + label;
        return label.Length > 15 ? label.Substring(0, 14) + "…" : label;   // fits the 92px multi-col label cell
    }

    private void ToggleSettingsFromGear()
    {
        _settingsWindow.SetVisible(!_settingsWindow.IsShown);
        _windowSection.Set("settings_visible", _settingsWindow.IsShown);
        _windowSection.Save();
    }

    // White — the borderless mini-HUD has no background, so crisp white labels + the dark text outline read
    // cleanly over any world background (the muted/cream theme colour washed out).
    private static ColorRgba MutedRgba() => new(1f, 1f, 1f, 1f);

    private static float Lerp(float from, float to, float t) => from + (to - from) * t;

    // Refresh the mini-HUD snapshot (selected ids, sorted) — called from OnUpdate. No allocation in Funcs.
    private void RefreshMiniSnapshot()
    {
        _miniSnapshot.Clear();
        foreach (var id in _selected) _miniSnapshot.Add(id);
        _miniSnapshot.Sort();
    }
}
