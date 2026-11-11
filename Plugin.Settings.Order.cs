using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using AbsUv = Stellar.Abstractions.Domain.UvRect;

namespace Stellar.StatInspector;

/// <summary>
/// STATS panel tabs + the HUD order list (user-approved option A, 2026-11-12). A segmented control above the search row —
/// two 24 px chips in a rounded Tile track, "All stats" | "On the HUD · N" (on = bold accent on a 22 % accent wash, off =
/// muted), fixed widths so nothing moves. All stats = the picker as before. On the HUD = the search row and the picker
/// give way to the ordered list of picked stats in the same space (the window never changes size): 26 px tiles
/// [muted "::" grip · muted position · icon · name · muted live value · round ✕], dragged to reorder through
/// <see cref="VirtualListElement.OnReorder"/> (framework insertion bar; the pattern of StellarPositionPlugin
/// Plugin.PresetsList.cs). A drop moves the stat (Plugin.Selection.cs <see cref="MoveHudStat"/>, debounced save), flashes
/// the row green and puts "✓ Moved “x” to position n" in the footer; ✕ removes it with the picker's feedback. The tab is
/// remembered in the window section. Every row Func is O(1) and returns cached strings (position numbers, names, values).
/// </summary>
public sealed partial class Plugin
{
    private const string StatsTabKey   = "stats_tab_hud";
    // Chip box = the longest tab label in any shipped locale (en "On the HUD · 64" bold 13 ≈ 112 px, id "Semua statistik"
    // ≈ 104 px) + headroom, so switching tab, count or language never moves a chip (house rule 10).
    private const float TabChipW       = 132f;
    private const float TabTrackPad    = 2f;
    private const int   HudOrderRows   = 9;     // the On the HUD area = search row + gap + picker → 9 full rows
    private const int   HudPoolSize    = HudOrderRows + 3;
    private const float GripW          = 10f;
    private const float PosW           = 18f;   // "64" right-aligned
    private const float RemoveSize     = 22f;   // round ✕ inside the 26 px tile
    private static readonly ColorRgba RemoveWash = new(1f, 0x4d / 255f, 0x4f / 255f, 0.22f);   // #ff4d4f @22 %
    private static readonly ColorRgba RemoveText = new(1f, 0x8a / 255f, 0x8b / 255f, 1f);      // #ff8a8b

    private bool _hudTab;            // STATS panel shows the HUD order list (persisted: window.stats_tab_hud)
    private int  _hudFirst;          // first visible item of the HUD order list (its OnWindow callback)
    private int  _movedAttr = -1;    // the stat just dropped (its row flashes green for RowFlashDurS)
    private float _movedAt;
    private readonly FmtCache _hudTabFmt = new();

    // ── tabs ──────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildStatsTabs()
    {
        var track = new BubbleElement(new RowElement(new HudElement[]
        {
            TabChip(() => _loc.T("stat.tab.all"), () => !_hudTab, () => SetStatsTab(false)),
            TabChip(HudTabLabel, () => _hudTab, () => SetStatsTab(true)),
        }, Gap: TabTrackPad))
        {
            FillColorFn = () => Fade(TileColor()),
            CornerRadius = RoundRadius(ChipHeight + 2f * TabTrackPad), PaddingX = TabTrackPad, PaddingY = TabTrackPad,
        };
        return new RowElement(new HudElement[] { track, new SpacerElement(0f) }, Gap: 0.01f);   // a Bubble hugs in a Row
    }

    private string HudTabLabel() => _hudTabFmt.Get(_loc.T("stat.tab.hud"), _hudOrder.Count);

    // Two complete chips in one fixed cell, swapped by a Conditional (bold can't toggle live) — the ColumnChip pattern.
    private HudElement TabChip(System.Func<string> label, System.Func<bool> on, System.Action pick)
    {
        var hover = new HoverFlag();
        return new CellElement(new ConditionalElement(on,
            ChipButton(DropBold(new TextElement(label, Color: () => EnsureContrast(AccentColor(), TabOnFill(), SolidMinContrast),
                    Width: TabChipW, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }),
                TabOnFill, hover, pick, TabChipW),
            ChipButton(new TextElement(label, Color: () => MutedColor(), Width: TabChipW, Align: TextAlign.Center, NoWrap: true)
                    { Ellipsize = true },
                TileColor, hover, pick, TabChipW)), Width: TabChipW);
    }

    private ColorRgba TabOnFill() => Over(AccentColor() with { A = AccentWashAlpha }, TileColor());

    private void SetStatsTab(bool hud)
    {
        if (_hudTab == hud) return;
        _hudTab = hud;
        _clearConfirmAt = -1f;   // any other action dismisses a pending Clear-all confirm
        _windowSection.Set(StatsTabKey, hud);
        _windowSection.Save();   // one click = one write
    }

    // ── HUD order list ────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildHudOrderList()
    {
        var pool = new HudElement[HudPoolSize];
        for (var i = 0; i < HudPoolSize; i++) pool[i] = ListSlot(BuildHudOrderTile(i));
        return new VirtualListElement(() => _hudOrder.Count, ListRowPitch, pool, f => _hudFirst = f,
            Height: ListHeight(HudOrderRows))
        {
            FixedHeight = true,   // ends on a full tile; the area's few spare px stay blank below it
            // Drag a row past ~6 px to move it (shorter = a click); Esc or a drop outside cancels; edges auto-scroll.
            OnReorder = OnHudReorder,
        };
    }

    // Nothing on the HUD yet: a centred two-line note in the same area.
    private HudElement BuildHudOrderEmpty() => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, ListHeight(HudOrderRows) / 2f - 24f),
        new TextElement(() => _loc.T("stat.order.empty"), Color: () => MutedColor(), Align: TextAlign.Center),
        new TextElement(() => _loc.T("stat.order.empty.hint"), Color: () => MutedColor(), Align: TextAlign.Center),
    }, Gap: 4f);

    private int HudIdAt(int slot)
    {
        var i = _hudFirst + slot;
        return i >= 0 && i < _hudOrder.Count ? _hudOrder[i] : -1;
    }

    private HudElement BuildHudOrderTile(int slot) => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(2f, ListTileHeight),   // height strut + (with the 6 px gap) the 8 px left inset
        // "::" — the grip ChatTools' quick-phrase drag list uses (renders in the plain menu font).
        new CellElement(new TextElement(() => "::", () => MutedColor(), Align: TextAlign.Center, NoWrap: true), Width: GripW),
        new CellElement(new TextElement(() => PositionText(slot), () => MutedColor(), Align: TextAlign.Right, NoWrap: true),
            Width: PosW),
        // Fixed cell: a bare SpriteElement collapses / stretches in a row (the icon-squish gotcha).
        new CellElement(new SpriteElement(() => _iconAtlas.Png, HudIconUv(slot), 16, 16, UvFunc: () => HudIconUv(slot)),
            Width: 16f),
        new CellElement(new TextElement(() => HudName(slot), () => TextColor(), NoWrap: true) { Ellipsize = true },
            Weight: 1f),
        new CellElement(new TextElement(() => HudValue(slot), () => MutedColor(), Align: TextAlign.Right, NoWrap: true),
            Width: ValueColW),
        RemoveButton(slot),
        new SpacerElement(2f, 1f),
    }, Gap: 6f))
    {
        FillColorFn = () => Fade(HudRowFlashing(slot) ? Over(SuccessColor() with { A = FlashWashAlpha }, TileColor()) : TileColor()),
        CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
    };

    private bool HudRowFlashing(int slot)
    {
        var id = HudIdAt(slot);
        return id > 0 && id == _movedAttr && _services.Framework.TimeNow - _movedAt < RowFlashDurS;
    }

    // "1".."N", built once per position for the plugin's lifetime (≤ MiniHudMaxCells tiny strings).
    private static readonly string?[] _posText = new string?[MiniHudMaxCells + 1];

    private string PositionText(int slot)
    {
        var i = _hudFirst + slot;
        if (i < 0 || i >= _hudOrder.Count) return string.Empty;
        var n = i + 1;
        if (n >= _posText.Length) return n.ToString(CultureInfo.InvariantCulture);   // past the HUD's cell pool: rare
        return _posText[n] ??= n.ToString(CultureInfo.InvariantCulture);
    }

    private AbsUv HudIconUv(int slot)
    {
        var id = HudIdAt(slot);
        return StatIconAtlas.UvRectFor(id > 0 ? IconIndexFor(id) : 0);
    }

    // The mini-HUD's own label (name + Base/Total prefix), cached per attr once it resolves.
    private string HudName(int slot)
    {
        var id = HudIdAt(slot);
        return id > 0 ? ResolveLabel(id) : string.Empty;
    }

    // Live value, formatted only when the stat in this slot or its raw value changed (FormatValue builds a string).
    private readonly int[] _hudValId = new int[HudPoolSize];
    private readonly long[] _hudValRaw = new long[HudPoolSize];
    private readonly string?[] _hudValStr = new string?[HudPoolSize];

    private string HudValue(int slot)
    {
        var id = HudIdAt(slot);
        if (id <= 0) return string.Empty;
        var r = _services.PlayerStats.TryGetAttribute(id);
        if (!r.HasValue) return "—";
        if (_hudValId[slot] != id || _hudValRaw[slot] != r.Value || _hudValStr[slot] is null)
        {
            _hudValId[slot] = id;
            _hudValRaw[slot] = r.Value;
            _hudValStr[slot] = FormatValue(id, r.Value);
        }
        return _hudValStr[slot]!;
    }

    // ── round ✕ (house "Remove hover": muted glyph at rest, red glyph on a soft red round wash on hover) ──────────
    // ⚠️ Pooled row: the hover is keyed by the slot's ATTR — a ✕ click slides the next stat under the pointer, and it
    // must not inherit the red state until the pointer re-enters.
    private readonly bool[] _rmHover = new bool[HudPoolSize];
    private readonly int[] _rmHoverId = new int[HudPoolSize];

    private bool RemoveHovered(int slot)
    {
        var id = HudIdAt(slot);
        if (_rmHoverId[slot] != id) { _rmHoverId[slot] = id; _rmHover[slot] = false; }
        return _rmHover[slot];
    }

    private HudElement RemoveButton(int slot) => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, RemoveSize),
            // Plain text: bold "✕" can draw blank (KB Stellar-Design-Language §6).
            new TextElement(() => "✕", () => RemoveHovered(slot)
                    ? EnsureContrast(RemoveText, Over(RemoveWash, TileColor()), 3f) : MutedColor(),
                Width: RemoveSize, Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: () => RemoveHudAt(slot))
        { Bare = true, OnHover = over => { _rmHover[slot] = over; _rmHoverId[slot] = HudIdAt(slot); } })
    {
        FillColorFn = () => RemoveHovered(slot) ? Fade(Over(RemoveWash, TileColor())) : Transparent,
        CornerRadius = RoundRadius(RemoveSize), PaddingX = 0f, PaddingY = 0f,
    };

    // ── actions ───────────────────────────────────────────────────────────────────────────────────────────────────
    private void RemoveHudAt(int slot)
    {
        var id = HudIdAt(slot);
        if (id <= 0) return;
        var name = ResolveLabel(id);
        TogglePick(id, false);              // Plugin.Selection.cs — unsubscribes, saves
        OnStatToggled(id, name, false);     // Plugin.Settings.Feedback.cs — the picker's footer line
    }

    private void OnHudReorder(int from, int to)
    {
        if (!MoveHudStat(from, to)) return;   // Plugin.Selection.cs — debounced save
        var id = _hudOrder[to];
        var now = _services.Framework.TimeNow;
        _movedAttr = id;
        _movedAt = now;
        _footerNote = _loc.TFormat("stat.feedback.moved", ResolveLabel(id), to + 1);
        _footerNoteAt = now;
        _clearConfirmAt = -1f;
    }
}
