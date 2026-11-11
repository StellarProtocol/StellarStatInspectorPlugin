using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellar.StatInspector;

/// <summary>
/// The HUD selection: picking / unpicking a stat, Clear all, persisting the selection to
/// <c>statinspector.selected</c> and reconciling an external edit of that key.
/// </summary>
public sealed partial class Plugin
{
    private void TogglePick(int attrId, bool wanted)
    {
        _peek.Remove(attrId);   // ownership moves to (or away from) _selected
        _selVersion++;          // Settings group headers recount their "selected / total"
        if (wanted)
        {
            if (_selected.Add(attrId))
                _services.PlayerStats.Subscribe(attrId);
        }
        else
        {
            if (_selected.Remove(attrId))
                _services.PlayerStats.Unsubscribe(attrId);
            // Re-peeked next frame if still visible, so the value keeps showing.
        }
        PersistSelected();
    }

    private void DoReset()
    {
        foreach (var id in _selected.ToArray())
        {
            _services.PlayerStats.Unsubscribe(id);
        }
        _selected.Clear();
        _selVersion++;
        PersistSelected();
    }

    private void PersistSelected()
    {
        var arr = _selected.ToArray();
        Array.Sort(arr);
        _statSection.Set("selected", arr);
        _statSection.SaveQuiet();
    }

    private void OnConfigChanged(string sectionName)
    {
        if (sectionName != "statinspector")
        {
            // window section may have been edited externally; framework owns rect.
            return;
        }

        var arr = _statSection.Get<int[]>("selected", null) ?? Array.Empty<int>();
        var disk = new HashSet<int>(arr);

        var toAdd = new List<int>();
        foreach (var id in disk) if (!_selected.Contains(id)) toAdd.Add(id);
        var toRemove = new List<int>();
        foreach (var id in _selected) if (!disk.Contains(id)) toRemove.Add(id);

        foreach (var id in toAdd)
        {
            _services.PlayerStats.Subscribe(id);
            _selected.Add(id);
        }
        foreach (var id in toRemove)
        {
            _services.PlayerStats.Unsubscribe(id);
            _selected.Remove(id);
        }

        if (toAdd.Count + toRemove.Count > 0) _selVersion++;
        LogReconciliation(toAdd.Count, toRemove.Count);
    }
}
