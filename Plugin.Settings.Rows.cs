using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using AbsUv = Stellar.Abstractions.Domain.UvRect;

namespace Stellar.StatInspector;

/// <summary>
/// Settings picker rows, house style. One reusable slot is a <see cref="ConditionalElement"/> that renders either a
/// group header (Card tile: accent ▶/▼ + bold name + muted "selected / total", click toggles collapse) or a stat row
/// (Tile: 16 px check box + atlas icon + name + muted live value, click toggles the stat on the HUD). Both are filled
/// 26 px tiles in a 28 px slot (<see cref="ListSlot"/>), with a hover that follows the rounded tile (Bubble fill around
/// a Bare selectable). Each slot reads the flattened <see cref="_snapshot"/> at <see cref="_winFirst"/>+slot; the
/// framework's scroll-windowed list recycles the fixed pool over the full list. Every text Func is O(1) and returns a
/// cached string unless its inputs changed. Peek-subscription follows the visible window (<see cref="RefreshWindowPeek"/>)
/// so every on-screen row shows a live value even when it isn't on the HUD.
/// </summary>
public sealed partial class Plugin
{
    private const float CheckBoxSize = 16f;
    // Value column: the widest live values ("12,345,678", "118.5%") and the bold flash words (th "นำออกแล้ว" ≈ 63 px)
    // fit, so the name column keeps one width and the value is right-aligned on the same edge on every row.
    private const float ValueColW = 84f;

    private RowEntry EntryAt(int slot)
    {
        var i = _winFirst + slot;
        return i >= 0 && i < _snapshot.Count ? _snapshot[i] : default;
    }

    private bool IsHeaderAt(int slot) => EntryAt(slot).IsHeader;
    private bool IsCollapsed(string slug) => _groupCollapsed.GetValueOrDefault(slug, true);

    private HudElement BuildPickerSlot(int slot) =>
        new ConditionalElement(() => IsHeaderAt(slot), ListSlot(BuildHeaderTile(slot)), ListSlot(BuildStatTile(slot)));

    // ── group header ──────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildHeaderTile(int slot) => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(2f, ListTileHeight),   // height strut + (with the 6 px gap) the 8 px left inset
            new CellElement(new TextElement(() => HeaderArrow(slot), () => AccentColor(), NoWrap: true), Width: 12f),
            new CellElement(new TextElement(() => EntryAt(slot).Text, () => TextColor(), NoWrap: true, FontSize: 14)
                { Bold = true, Ellipsize = true }, Weight: 1f),
            new TextElement(() => HeaderCount(slot), () => MutedColor(), NoWrap: true),
            new SpacerElement(2f, 1f),               // right inset
        }, Gap: 6f),
        OnClick: () => ToggleCollapseAt(slot))
        { Bare = true, OnHover = over => SetSlotHover(slot, over) })
    {
        FillColorFn = () => Fade(SlotHovered(slot) ? HoverFill(CardColor()) : CardColor()),
        CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
    };

    private string HeaderArrow(int slot)
    {
        var e = EntryAt(slot);
        return e.IsHeader ? (IsCollapsed(e.GroupSlug) && !_searching ? "▶" : "▼") : string.Empty;
    }

    // "{selected} / {total}" for the group (total = the rows it shows, i.e. the matches while searching). Recounted per
    // slot only when the selection or the slot's group changed — a header row never scans its members per refresh.
    private readonly List<int>?[] _hdrMembers = new List<int>?[SettingsPoolSize];
    private readonly int[] _hdrVersion = new int[SettingsPoolSize];
    private readonly FmtCache[] _hdrFmt = NewFmtCaches(SettingsPoolSize);
    private int _selVersion;   // bumped on every selection change (Plugin.cs TogglePick / DoReset / OnConfigChanged)

    private string HeaderCount(int slot)
    {
        var e = EntryAt(slot);
        if (!e.IsHeader || e.Members is null) return string.Empty;
        if (!ReferenceEquals(_hdrMembers[slot], e.Members) || _hdrVersion[slot] != _selVersion)
        {
            var sel = 0;
            foreach (var id in e.Members) if (_selected.Contains(id)) sel++;
            _hdrMembers[slot] = e.Members;
            _hdrVersion[slot] = _selVersion;
            _hdrSel[slot] = sel;
        }
        return _hdrFmt[slot].Get("{0} / {1}", _hdrSel[slot], e.Members.Count);
    }

    private readonly int[] _hdrSel = new int[SettingsPoolSize];

    private static FmtCache[] NewFmtCaches(int n)
    {
        var a = new FmtCache[n];
        for (var i = 0; i < n; i++) a[i] = new FmtCache();
        return a;
    }

    // ── stat row ──────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildStatTile(int slot) => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(2f, ListTileHeight),
            CheckBox(slot),
            // Fixed cell: a bare SpriteElement collapses / stretches in a row (the icon-squish gotcha).
            new CellElement(new SpriteElement(() => _iconAtlas.Png, PickerIconUv(slot), 16, 16, UvFunc: () => PickerIconUv(slot)),
                Width: 16f),
            new CellElement(new TextElement(() => EntryAt(slot).Text, () => TextColor(), NoWrap: true) { Ellipsize = true },
                Weight: 1f),
            // Live value, or — for ~1 s after a click on this stat — bold green "Added" / "Removed" in its place (two
            // complete texts: bold can't toggle live). Same fixed cell, so the swap never moves the name.
            new CellElement(new ConditionalElement(() => RowFlashing(slot),
                new TextElement(() => FlashLabel(), () => FlashTextColor(), Align: TextAlign.Right, NoWrap: true, FontSize: 13)
                    { Bold = true },
                new TextElement(() => PickerValue(slot), () => MutedColor(), Align: TextAlign.Right, NoWrap: true)),
                Width: ValueColW),
            new SpacerElement(2f, 1f),
        }, Gap: 6f),
        OnClick: () => TogglePickAt(slot))
        { Bare = true, OnHover = over => SetSlotHover(slot, over) })
    {
        FillColorFn = () => Fade(StatTileFill(slot)),
        CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
    };

    // Tile → accent wash when the stat is on the HUD → green while its click flash runs; hover lightens whichever.
    private ColorRgba StatTileFill(int slot)
    {
        var id = EntryAt(slot).AttrId;
        var fill = RowFlashing(slot) ? Over(SuccessColor() with { A = FlashWashAlpha }, TileColor())
                 : _selected.Contains(id) ? Over(AccentColor() with { A = AccentWashAlpha }, TileColor())
                 : TileColor();
        return SlotHovered(slot) ? HoverFill(fill) : fill;
    }

    // 16 px rounded check box: on = solid accent + white "✓", off = an empty Neutral box. One element (the glyph is a
    // live text, the fill a live colour), so ticking never rebuilds anything. TextElement.Height pins the plain 14 px
    // glyph's line box to 16 (its natural line is taller), so the box stays exactly 16 × 16.
    private HudElement CheckBox(int slot) => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, CheckBoxSize),
            new TextElement(() => IsPicked(slot) ? "✓" : string.Empty,
                () => EnsureContrast(new ColorRgba(1f, 1f, 1f), AccentColor(), SolidMinContrast),
                Width: CheckBoxSize, Align: TextAlign.Center, NoWrap: true) { Height = CheckBoxSize },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(IsPicked(slot) ? AccentColor() : NeutralFill()),
        CornerRadius = 4f, PaddingX = 0f, PaddingY = 0f,
    }, Width: CheckBoxSize);

    private bool IsPicked(int slot) => _selected.Contains(EntryAt(slot).AttrId);

    private AbsUv PickerIconUv(int slot)
    {
        var id = EntryAt(slot).AttrId;
        return StatIconAtlas.UvRectFor(id > 0 ? IconIndexFor(id) : 0);
    }

    // Live value, formatted only when the stat in this slot or its raw value changed (FormatValue builds a string).
    private readonly int[] _valId = new int[SettingsPoolSize];
    private readonly long[] _valRaw = new long[SettingsPoolSize];
    private readonly string[] _valStr = new string[SettingsPoolSize];

    private string PickerValue(int slot)
    {
        var id = EntryAt(slot).AttrId;
        if (id <= 0) return string.Empty;
        var r = _services.PlayerStats.TryGetAttribute(id);
        if (!r.HasValue) return "—";
        if (_valId[slot] != id || _valRaw[slot] != r.Value || _valStr[slot] is null)
        {
            _valId[slot] = id;
            _valRaw[slot] = r.Value;
            _valStr[slot] = FormatValue(id, r.Value);
        }
        return _valStr[slot];
    }

    // ── hover (per pool slot) ─────────────────────────────────────────────────────────────────────────────────────
    // Keyed by the slot's LOGICAL row: when the slot is re-bound to another row (scroll, collapse), the flag resets
    // until the pointer re-enters, so a recycled slot never inherits the previous row's hover.
    private readonly bool[] _slotHover = new bool[SettingsPoolSize];
    private readonly int[] _slotHoverRow = new int[SettingsPoolSize];

    private void SetSlotHover(int slot, bool over)
    {
        _slotHover[slot] = over;
        _slotHoverRow[slot] = _winFirst + slot;
    }

    private bool SlotHovered(int slot)
    {
        if (_slotHoverRow[slot] != _winFirst + slot) { _slotHoverRow[slot] = _winFirst + slot; _slotHover[slot] = false; }
        return _slotHover[slot];
    }

    // ── clicks ────────────────────────────────────────────────────────────────────────────────────────────────────
    private void ToggleCollapseAt(int slot)
    {
        var e = EntryAt(slot);
        if (!e.IsHeader || string.IsNullOrEmpty(e.GroupSlug) || _searching) return;   // searching = all open
        _groupCollapsed[e.GroupSlug] = !IsCollapsed(e.GroupSlug);
        _windowSection.Set("group_" + e.GroupSlug + "_collapsed", _groupCollapsed[e.GroupSlug]);
        _windowSection.Save();
        MarkSnapshotDirty();
    }

    private void TogglePickAt(int slot)
    {
        var e = EntryAt(slot);
        if (e.IsHeader || e.AttrId <= 0) return;
        var wanted = !_selected.Contains(e.AttrId);
        TogglePick(e.AttrId, wanted);   // selection is read live; no snapshot rebuild needed
        OnStatToggled(e.AttrId, e.Text, wanted);   // Plugin.Settings.Feedback.cs — row flash + footer line
    }

    // Subscribe the attrs in the current visible window (so rows that aren't on the HUD still show a live value) and
    // drop peek-subscriptions for rows that scrolled out. Driven from OnUpdate while the window is shown.
    private void RefreshWindowPeek()
    {
        _drawnThisFrame.Clear();
        for (var i = 0; i < SettingsPoolSize; i++)
        {
            var idx = _winFirst + i;
            if (idx < 0 || idx >= _snapshot.Count) continue;
            var e = _snapshot[idx];
            if (e.IsHeader || e.AttrId <= 0) continue;
            _drawnThisFrame.Add(e.AttrId);
            EnsurePeekSubscribed(e.AttrId);
        }
        ReconcilePeek();
    }

    private string PickerName(int id)
    {
        var name = ResolveAttrName(id) ?? ("#" + id.ToString(CultureInfo.InvariantCulture));
        if (_disambiguationLabel.TryGetValue(id, out var prefix)) name = prefix + " " + name;
        return name;
    }
}
