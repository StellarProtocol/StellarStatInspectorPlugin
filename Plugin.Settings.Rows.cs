using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using AbsUv = Stellar.Abstractions.Domain.UvRect;

namespace Stellar.StatInspector;

/// <summary>
/// Settings picker rows (Phase E — native uGUI). One reusable slot is a polymorphic
/// <see cref="ConditionalElement"/> that renders either a group header (arrow + name + count, click toggles
/// collapse) or an attribute row (accent checkbox + atlas icon + name + right-aligned live value, click
/// toggles pick). Each slot reads the flattened <see cref="_snapshot"/> at <see cref="_winFirst"/>+slot —
/// the framework's scroll-windowed list recycles the fixed pool over the full list. Peek-subscription is
/// driven by the visible window range (<see cref="RefreshWindowPeek"/>) so every on-screen row shows a live
/// value even when unticked.
/// </summary>
public sealed partial class Plugin
{
    private RowEntry EntryAt(int slot)
    {
        var i = _winFirst + slot;
        return i >= 0 && i < _snapshot.Count ? _snapshot[i] : default;
    }

    private bool IsHeaderAt(int slot) => EntryAt(slot).IsHeader;
    private bool IsCollapsed(string slug) => _groupCollapsed.GetValueOrDefault(slug, true);

    private HudElement BuildPickerSlot(int slot)
    {
        var header = new SelectableElement(
            new RowElement(new HudElement[]
            {
                new TextElement(() => HeaderArrow(slot), () => MenuAccentRgba(), Width: 16f),
                new TextElement(() => EntryAt(slot).Text, Emphasis: true),
                new SpacerElement(),
                new TextElement(() => EntryAt(slot).IsHeader ? $"({EntryAt(slot).Count})" : string.Empty,
                    () => MenuMutedRgba(), Align: TextAlign.Right),
            }),
            OnClick: () => ToggleCollapseAt(slot));

        var picker = new SelectableElement(
            new RowElement(new HudElement[]
            {
                new TextElement(() => PickCheck(slot), () => MenuAccentRgba(), Width: 16f),
                new SpriteElement(() => _iconAtlas.Png, PickerIconUv(slot), 16, 16, UvFunc: () => PickerIconUv(slot)),
                new TextElement(() => EntryAt(slot).Text),
                new SpacerElement(),
                new TextElement(() => PickerValue(slot), () => MenuMutedRgba(), Align: TextAlign.Right),
            }),
            OnClick: () => TogglePickAt(slot),
            Selected: () => _selected.Contains(EntryAt(slot).AttrId));

        return new ConditionalElement(() => IsHeaderAt(slot), header, picker);
    }

    private string HeaderArrow(int slot)
    {
        var e = EntryAt(slot);
        return e.IsHeader ? (IsCollapsed(e.GroupSlug) ? "▶" : "▼") : string.Empty;
    }

    private string PickCheck(int slot) => _selected.Contains(EntryAt(slot).AttrId) ? "✓" : "□";

    private AbsUv PickerIconUv(int slot)
    {
        var id = EntryAt(slot).AttrId;
        return StatIconAtlas.UvRectFor(id > 0 ? IconIndexFor(id) : 0);
    }

    private string PickerValue(int slot)
    {
        var id = EntryAt(slot).AttrId;
        if (id <= 0) return string.Empty;
        var r = _services.PlayerStats.TryGetAttribute(id);
        return r.HasValue ? FormatValue(id, r.Value) : "—";
    }

    private void ToggleCollapseAt(int slot)
    {
        var e = EntryAt(slot);
        if (!e.IsHeader || string.IsNullOrEmpty(e.GroupSlug)) return;
        _groupCollapsed[e.GroupSlug] = !IsCollapsed(e.GroupSlug);
        _windowSection.Set("group_" + e.GroupSlug + "_collapsed", _groupCollapsed[e.GroupSlug]);
        _windowSection.Save();
        MarkSnapshotDirty();
    }

    private void TogglePickAt(int slot)
    {
        var e = EntryAt(slot);
        if (e.IsHeader || e.AttrId <= 0) return;
        TogglePick(e.AttrId, !_selected.Contains(e.AttrId));   // selection is read live; no snapshot rebuild needed
    }

    // Subscribe the attrs in the current visible window (so unticked-but-visible rows show a live value) and
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

    private ColorRgba MenuMutedRgba()  { var c = _services.Theme.Colors.MenuMuted;  return new ColorRgba(c.R, c.G, c.B, c.A); }
    private ColorRgba MenuAccentRgba() { var c = _services.Theme.Colors.MenuAccent; return new ColorRgba(c.R, c.G, c.B, c.A); }
}
