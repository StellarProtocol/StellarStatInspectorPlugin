using System;
using System.Collections.Generic;

namespace Stellar.StatInspector;

/// <summary>
/// Flattened picker snapshot for the uGUI Settings window (Phase E). The searchable/grouped/collapsible
/// attribute list is flattened ONCE into a linear <see cref="RowEntry"/> list (headers + attr rows
/// interleaved) that the window's <c>VirtualListElement</c> windows over. Rebuilt only when the search
/// term, a group's collapse, the selection, or the classification changes (a dirty flag) — never per
/// refresh. <see cref="RowEntry"/> is a field struct (no constructor) to stay under the analyzer's
/// param/ctor caps (records are exempt; plain structs are not).
/// </summary>
public sealed partial class Plugin
{
    internal struct RowEntry
    {
        public bool IsHeader;
        public string GroupSlug;   // header: the collapse key; attr row: owning group's slug
        public int AttrId;         // attr row only (-1 for headers / placeholder rows)
        public string Text;        // header: group display name; attr row: resolved/disambiguated name
        public int Count;          // header only: member count shown
    }

    private readonly List<RowEntry> _snapshot = new();
    private bool _snapshotDirty = true;

    private void MarkSnapshotDirty() => _snapshotDirty = true;

    private void RebuildSnapshotIfDirty()
    {
        if (!_snapshotDirty) return;
        _snapshotDirty = false;
        _snapshot.Clear();

        if (!_classificationBuilt)
        {
            _snapshot.Add(new RowEntry { IsHeader = false, AttrId = -1, Text = "Loading attributes…" });
            return;
        }

        var trimmed = (_search ?? string.Empty).Trim();
        var searching = trimmed.Length > 0;

        foreach (var group in _orderedGroups)
        {
            var members = _groupMembers[group];
            List<int>? filtered = null;
            if (searching)
            {
                filtered = new List<int>();
                foreach (var id in members)
                {
                    var nm = ResolveAttrName(id);
                    if (nm is not null && nm.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0) filtered.Add(id);
                }
                if (filtered.Count == 0) continue;
            }

            var visible = filtered ?? members;
            var slug = SlugFor(group);
            // While searching, force-expand (don't overwrite persisted collapse state).
            var collapsed = !searching && _groupCollapsed.GetValueOrDefault(slug, true);

            _snapshot.Add(new RowEntry { IsHeader = true, GroupSlug = slug, AttrId = -1, Text = group, Count = visible.Count });
            if (collapsed) continue;
            foreach (var id in visible)
                _snapshot.Add(new RowEntry { IsHeader = false, GroupSlug = slug, AttrId = id, Text = PickerName(id) });
        }
    }
}
