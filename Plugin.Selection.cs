using System;
using System.Collections.Generic;

namespace Stellar.StatInspector;

/// <summary>
/// The HUD selection, ORDERED (user-approved 2026-11-12): <see cref="_hudOrder"/> is the list of stats on the HUD in the
/// order the HUD draws them, <see cref="_selected"/> the same ids as a set for O(1) Contains. Every mutation goes through
/// here so the two never drift: a new pick APPENDS, a removal drops the entry, Clear all empties both, a drag in Settings
/// → On the HUD moves one entry. Persisted as <c>statinspector.selected</c> (a JSON int array) IN LIST ORDER; on load
/// the stored order is kept as-is (configs written before ordering existed are id-sorted, so nothing moves until the
/// user reorders) and duplicates / invalid ids are dropped.
/// </summary>
public sealed partial class Plugin
{
    private const float OrderSaveDelay = 0.5f;   // seconds after the last drag-drop before the config is written
    private float _orderSaveAt = -1f;            // TimeNow when the pending order save is due; -1 = nothing pending

    // Replace the whole selection with `ids` in their order, keeping the first copy of a duplicate and dropping ids ≤ 0.
    // Does NOT touch subscriptions (callers diff and (un)subscribe themselves).
    private void SetSelectionFrom(IReadOnlyList<int> ids)
    {
        _selected.Clear();
        _hudOrder.Clear();
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (id > 0 && _selected.Add(id)) _hudOrder.Add(id);
        }
    }

    private void TogglePick(int attrId, bool wanted)
    {
        _peek.Remove(attrId);   // ownership moves to (or away from) _selected
        _selVersion++;          // Settings group headers recount their "selected / total"
        if (wanted)
        {
            if (_selected.Add(attrId))
            {
                _hudOrder.Add(attrId);   // a new pick goes to the END of the HUD
                _services.PlayerStats.Subscribe(attrId);
            }
        }
        else
        {
            if (_selected.Remove(attrId))
            {
                _hudOrder.Remove(attrId);
                _services.PlayerStats.Unsubscribe(attrId);
            }
            // Re-peeked next frame if still visible, so the value keeps showing.
        }
        PersistSelected();
    }

    // Drag-reorder (VirtualListElement.OnReorder): REMOVE-THEN-INSERT indices, `to` = the index AFTER the move. The HUD
    // follows on the next OnUpdate (RefreshMiniSnapshot copies _hudOrder). The disk write is debounced like the
    // background slider, so a burst of drags costs one config write. Returns false for an out-of-range / no-op move.
    private bool MoveHudStat(int from, int to)
    {
        if (from < 0 || from >= _hudOrder.Count || to < 0 || to >= _hudOrder.Count || from == to) return false;
        var id = _hudOrder[from];
        _hudOrder.RemoveAt(from);
        _hudOrder.Insert(to, id);
        _orderSaveAt = _services.Framework.TimeNow + OrderSaveDelay;
        return true;
    }

    // OnUpdate: write the debounced order once the list has been still for OrderSaveDelay.
    private void OrderSaveTick()
    {
        if (_orderSaveAt < 0f || _services.Framework.TimeNow < _orderSaveAt) return;
        PersistSelected();
    }

    private void DoReset()
    {
        foreach (var id in _hudOrder) _services.PlayerStats.Unsubscribe(id);
        _selected.Clear();
        _hudOrder.Clear();
        _selVersion++;
        PersistSelected();
    }

    // Writes the list in HUD order (also flushes a pending debounced order save — same data). Called on a click / drop,
    // never per frame, so the array is fine.
    private void PersistSelected()
    {
        _orderSaveAt = -1f;
        _statSection.Set("selected", _hudOrder.ToArray());
        _statSection.SaveQuiet();
    }

    // Dispose: a drag dropped just before unload still persists.
    private void FlushOrderSave()
    {
        if (_orderSaveAt >= 0f) PersistSelected();
    }

    // The statinspector section was edited outside the plugin: adopt the file's list (and its order), (un)subscribing
    // only the difference.
    private void OnConfigChanged(string sectionName)
    {
        if (sectionName != "statinspector")
        {
            // window section may have been edited externally; framework owns rect.
            return;
        }

        var arr = _statSection.Get<int[]>("selected", null) ?? Array.Empty<int>();
        var disk = new HashSet<int>();
        foreach (var id in arr) if (id > 0) disk.Add(id);

        var toAdd = new List<int>();
        foreach (var id in disk) if (!_selected.Contains(id)) toAdd.Add(id);
        var toRemove = new List<int>();
        foreach (var id in _selected) if (!disk.Contains(id)) toRemove.Add(id);

        foreach (var id in toAdd) _services.PlayerStats.Subscribe(id);
        foreach (var id in toRemove) _services.PlayerStats.Unsubscribe(id);
        SetSelectionFrom(arr);
        _orderSaveAt = -1f;   // the file wins over a not-yet-written drag

        if (toAdd.Count + toRemove.Count > 0) _selVersion++;
        LogReconciliation(toAdd.Count, toRemove.Count);
    }
}
