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
    // Per-column width → window width = columns × this: icon 18 + label 135 + value 52 + 2×4 gaps ≈ 213, plus slack.
    // Not Resizable, so the Borderless root content-fits its width every build (a saved layout only restores the
    // POSITION — WindowRenderer.SetRect applies size to Resizable windows only); widening needs no layout reset.
    private const float MiniHudColW = 221f;
    private const float MiniHudLabelW = 135f;
    private const int   MiniHudMaxCells = 64; // fixed cell pool (the selectable set never exceeds this in practice)

    // Selected ids, sorted; refreshed on Update so the cell Funcs never allocate/scan.
    private readonly List<int> _miniSnapshot = new();

    private IWindowControl BuildAndRegisterMiniHud()
    {
        var spec = new WindowSpec(
            Id:          "statinspector.mini-hud",
            Title:       _loc.T("stat.miniHud.title"),
            DefaultRect: new WindowRect(1644f, 1192f, _columns * MiniHudColW, 184f),
            Category:    WindowCategory.HUD,
            Style:       WindowPanelStyle.Borderless)   // chrome-less custom overlay (NOT a titled glass panel)
        {
            // Default shown; the framework layout slot is the single source of truth for persisted hide/show
            // (ApplySavedRect honours a persisted hide on mount) and the F6 toggle writes it via
            // SetVisiblePersist. The old private "minihud_visible" key desynced — it was read here but no longer
            // written, so a stale/false value left the HUD permanently hidden at start, which (never mounting)
            // also stopped the saved position from restoring and let the show-path clobber it with DefaultRect.
            // Live-localized title (layout editor outline label); Title stays the fallback.
            TitleProvider           = () => _loc.T("stat.miniHud.title"),
            StartVisible            = true,
            Draggable               = true,   // required so BuildChrome registers the (edit-only) whole-frame drag
            EditModeDragOnly        = true,   // gameplay overlay: moves only in layout edit-mode (Shift+`)
            // Gameplay in-world overlay: draw only while in the World phase AND no menu covers the game HUD
            // (v2 render-gate; replaces the removed HideUntilInWorld / AutoHideBehindGameMenus bools).
            ShouldRender            = () => _services.ClientState.Phase == GamePhase.World
                                            && (_services.ClientState.UiState & (GameUIState.Blocking | GameUIState.AnyMenu)) == 0,
            // Framework pre-build (fw 2.30.0): while a menu holds it hidden and it was never drawn, the framework fills
            // it off-screen once in the world, so its first drawn apply no longer builds the grid (~45 ms firstshow).
            // The cell Funcs only read _miniSnapshot + IPlayerStats (guarded), so running hidden is safe. Signature =
            // which body branch it shows (stats in → grid, N cells), so a fill that ran before the stats arrived or
            // with nothing selected is redone when that changes (and on entering/leaving the layout-edit preview).
            // ShouldRender needs no edit-mode term: the framework force-shows EditModeDragOnly windows while editing.
            Prebuild                = true,
            PrebuildSignature       = () => HashCode.Combine(_services.PlayerStats.IsAvailable, _miniSnapshot.Count, LayoutEditing),
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
                // Clean standalone cog (not the emoji atlas cell); dimmed + inert while layout-editing.
                new ImageElement(() => _iconAtlas.GearPng, 15, 15) { Tint = () => LayoutEditing ? GearDimmed : null },
                OnClick: ToggleSettingsFromGear),
            Width: 26f);
        // Thin titled header strip (design D): a faint "STATS" caption (left) + gear (right) over a divider, so
        // the gear reads as part of an intentional header instead of a disconnected floating corner icon.
        var header = new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("stat.header"), () => MutedRgba(), Shadow: true),
            new SpacerElement(),
            gear,
        });

        var cells = new HudElement[MiniHudMaxCells];
        for (var i = 0; i < MiniHudMaxCells; i++) { var idx = i; cells[idx] = BuildStatCell(idx); }
        // Wide grid cells (the framework default 120px is too tight for icon+label+value) so column names read.
        // Count is the live selection, or the sample set during the layout-edit preview (Plugin.MiniHud.Preview.cs).
        var grid = new ListElement(MiniCellCount, cells, Columns: _columns, CellWidth: MiniHudColW, CellHeight: 24f);

        var empty = new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("stat.noneSelected"), () => MutedRgba(), Shadow: true),
            new ButtonElement(() => _loc.T("stat.openSettings"), () => _settingsWindow.SetVisible(true)),
        });
        // In the edit preview MiniCellCount is never 0 and ShowingSample covers "stats unavailable", so neither the CTA
        // nor the not-in-world text shows while editing — the outline is always the real grid's size.
        var populated = new ConditionalElement(() => MiniCellCount() > 0, grid, empty);

        var notInWorld = new TextElement(() => _loc.T("stat.notInWorld"), () => MutedRgba(), Shadow: true);
        var body = new ConditionalElement(() => _services.PlayerStats.IsAvailable || ShowingSample, populated, notInWorld);

        // Tight gap (default Column gap is the 12px section spacing — too airy around the header divider here).
        return new ColumnElement(new HudElement[] { header, new SeparatorElement(), body }, Gap: 2f);
    }

    // One stat cell: icon + clipped label + right-aligned value (delta-flash colour Func). Reads _miniSnapshot[slot].
    private HudElement BuildStatCell(int slot) => new RowElement(new HudElement[]
    {
        // Fixed-width icon cell — as the first child of a grid-cell Row, a bare SpriteElement collapses to 0;
        // a CellElement pins its slot so the 16px icon renders.
        // Fits the wide grid cell (CellWidth = MiniHudColW): icon 18 + label 135 + value 52 + gaps ≈ 213. NO
        // Spacer (the Borderless fitter fights a flexible spacer). Delta direction is shown by the value COLOUR
        // (green up / red down) — the inline "▲ N" number is dropped (also dodges the ▲ font-risk glyph).
        new CellElement(new SpriteElement(() => _iconAtlas.Png, IconUvForSlot(slot), 16, 16, UvFunc: () => IconUvForSlot(slot)), Width: 18f),
        // MaxLines = 1: the framework cuts an over-wide name with "…" at the label's real laid-out width (re-fitted
        // only when the string/width changes). Works here although the doc says "ignored on Shadow": that applies to
        // the TMP styled path; a plain Shadow label on a Menu-surface window takes the legacy Text path, whose
        // TextBinding still honours MaxLines (UGuiPrimitives.CapLines). Width-based, so CJK names cut correctly too.
        new TextElement(() => SlotLabel(slot), () => MutedRgba(), Width: MiniHudLabelW, Shadow: true) { MaxLines = 1 },
        new TextElement(() => SlotValue(slot), () => SlotDeltaColor(slot), Width: 52f, Align: TextAlign.Right, Shadow: true),
    }, Gap: 4f);

    private int AttrAt(int slot)
    {
        if (ShowingSample) return slot < SampleAttrIds.Length ? SampleAttrIds[slot] : -1;
        return slot < _miniSnapshot.Count ? _miniSnapshot[slot] : -1;
    }

    private AbsUv IconUvForSlot(int slot)
    {
        var id = AttrAt(slot);
        return StatIconAtlas.UvRectFor(id >= 0 ? IconIndexFor(id) : 0);
    }

    private string SlotLabel(int slot)
    {
        if (ShowingSample) return SampleLabel(slot);
        var id = AttrAt(slot);
        return id >= 0 ? ResolveLabel(id) : string.Empty;
    }

    private string SlotValue(int slot)
    {
        if (ShowingSample) return SampleValue(slot);
        var id = AttrAt(slot);
        if (id < 0) return string.Empty;
        var raw = _services.PlayerStats.TryGetAttribute(id);
        return raw.HasValue ? FormatValue(id, raw.Value) : "—";   // delta direction is shown via SlotDeltaColor
    }

    // Delta-flash: neutral HudText when no recent change; otherwise lerp HudText→Success (up)/Danger (down) by fade.
    private ColorRgba? SlotDeltaColor(int slot)
    {
        var id = AttrAt(slot);
        if (id < 0) return null;
        if (LayoutEditing) return White;   // edit preview: no green/red flash while arranging
        var fade = DeltaFade(id, out var dir);
        var t = _services.Theme.Colors;
        // White (not the muted cream) — the borderless HUD has no background, so crisp white + the dark text
        // outline reads cleanly over any world background. Delta still flashes green/red.
        if (dir == 0 || fade <= 0f) return White;
        var a = dir > 0 ? t.Success : t.Danger;   // theme status colours (IThemeStatusColors, fw 2.30.0)
        var b = t.HudText;
        return new ColorRgba(Lerp(b.R, a.R, fade), Lerp(b.G, a.G, fade), Lerp(b.B, a.B, fade), 1f);
    }

    // 0 = neutral; otherwise 1→0 over DeltaFadeSeconds. dir = +1 up / -1 down / 0 none.
    private float DeltaFade(int attrId, out int dir)
    {
        dir = 0;
        if (!_changes.TryGetValue(attrId, out var ch)) return 0f;
        var age = _services.Framework.TimeNow - ch.At;
        if (age >= DeltaFadeSeconds) return 0f;
        dir = ch.Delta > 0 ? 1 : -1;
        return 1f - age / DeltaFadeSeconds;
    }

    // Full label (truncation is the label TextElement's MaxLines). Cached per attrId once the name resolves, so the
    // per-refresh cell Func doesn't rebuild the "Base "/"Total " disambiguation string every poll.
    private readonly Dictionary<int, string> _labelOf = new();

    private string ResolveLabel(int attrId)
    {
        if (_labelOf.TryGetValue(attrId, out var cached)) return cached;
        var label = ResolveAttrName(attrId);
        if (string.IsNullOrEmpty(label)) { LogOrphanAttr(attrId); return "#" + attrId; }   // not cached: retried until GameData names it
        if (_disambiguationLabel.TryGetValue(attrId, out var prefix)) label = prefix + " " + label;
        if (_classificationBuilt) _labelOf[attrId] = label;   // the Base/Total prefixes exist only once classified
        return label;
    }

    private void ToggleSettingsFromGear()
    {
        if (LayoutEditing) return;   // the gear is dimmed + inert while arranging (edit-mode clicks are for dragging)
        _settingsWindow.SetVisible(!_settingsWindow.IsShown);
        _windowSection.Set("settings_visible", _settingsWindow.IsShown);
        _windowSection.Save();
    }

    // White — the borderless mini-HUD has no background, so crisp white labels + the dark text outline read
    // cleanly over any world background (the muted/cream theme colour washed out).
    private static ColorRgba MutedRgba() => White;

    private static readonly ColorRgba White = new(1f, 1f, 1f, 1f);
    private static readonly ColorRgba GearDimmed = new(1f, 1f, 1f, 0.35f);   // gear tint while layout-editing

    private static float Lerp(float from, float to, float t) => from + (to - from) * t;

    // Refresh the mini-HUD snapshot (selected ids, sorted) — called from OnUpdate. No allocation in Funcs.
    private void RefreshMiniSnapshot()
    {
        _miniSnapshot.Clear();
        foreach (var id in _selected) _miniSnapshot.Add(id);
        _miniSnapshot.Sort();
    }
}
