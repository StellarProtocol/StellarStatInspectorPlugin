using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.StatInspector;

public sealed partial class Plugin
{
    private void BuildGroupClassification()
    {
        if (!_services.GameData.IsAvailable)
        {
            // Eager batch not done yet — leave classification empty; settings window
            // shows "Loading attributes…" until the next frame's draw triggers a retry.
            return;
        }

        // Walk every attribute that has EITHER a description OR a profile
        // entry; bucket by profile's TypeDisplayName (string), falling back to
        // "Other" when no profile row. AttrDescriptionBase covers a subset of
        // EAttrType IDs (e.g. 1252 rows) but AttributeProfile carries the
        // canonical names for the starter-list IDs (Strength=11011,
        // AttackTotal=11331, MAttackTotal=11341, CritDamage=12511) that are
        // missing from AttrDescriptionBase — see ResolveAttrName in Plugin.cs.
        //
        // Collision-detection pass: most stats appear in ProfileAttrTableBase
        // as multiple variants sharing the same display name. Earlier dedup
        // attempt assumed Total = higher AttrId (true for MaxHp 11320/11321),
        // but in-world recon showed the convention is not universal — for
        // Illusion Strength the higher ID holds the smaller (Base-style)
        // value, opposite of MaxHp. Instead of guessing, we keep all variants
        // and disambiguate at render time by appending "#ID" to the picker
        // label when the bare name would collide. Plugin authors pick the
        // variant whose live value matches the in-game Attributes panel.
        var nameCounts = BuildNameCountMap();
        BuildDisambiguationBuckets(nameCounts);
    }

    // First pass: populate _groupOf / _groupMembers and return a map of
    // (group, name) → occurrence count for collision detection.
    private Dictionary<(string Group, string Name), int> BuildNameCountMap()
    {
        var nameCounts = new Dictionary<(string Group, string Name), int>();
        for (var id = 1; id <= AttrIdSettingsScanMax; id++)
        {
            var info = _services.GameData.Combat.GetAttribute(id);
            var profile = _services.GameData.Combat.GetAttributeProfile(id);
            if (info is null && profile is null) continue;

            var group = profile is { TypeDisplayName: { Length: > 0 } gn } ? gn : OtherGroup;
            var name = ResolveAttrName(id);
            if (string.IsNullOrEmpty(name))
            {
                name = "#" + id.ToString(CultureInfo.InvariantCulture);
            }

            _groupOf[id] = group;
            if (!_groupMembers.TryGetValue(group, out var list))
            {
                list = new List<int>(16);
                _groupMembers[group] = list;
            }
            list.Add(id);

            var key = (group, name);
            nameCounts[key] = nameCounts.TryGetValue(key, out var c) ? c + 1 : 1;
        }
        return nameCounts;
    }

    // Second pass: for any (group, name) with multiple IDs, assign a
    // human-readable disambiguation prefix. The 2-ID common case follows
    // the X / X+1 Base/Total convention; 3+ falls back to "#ID".
    private void BuildDisambiguationBuckets(Dictionary<(string Group, string Name), int> nameCounts)
    {
        var bucket = new Dictionary<(string, string), List<int>>();
        for (var id = 1; id <= AttrIdSettingsScanMax; id++)
        {
            if (!_groupOf.TryGetValue(id, out var group)) continue;
            var name = ResolveAttrName(id);
            if (string.IsNullOrEmpty(name)) continue;
            var key = (group, name);
            if (!nameCounts.TryGetValue(key, out var c) || c <= 1) continue;
            _ambiguousIds.Add(id);
            if (!bucket.TryGetValue(key, out var ids))
            {
                ids = new List<int>(c);
                bucket[key] = ids;
            }
            ids.Add(id);
        }
        foreach (var kv in bucket)
        {
            AssignDisambiguationLabels(kv.Value);
        }
    }

    // Walks a list of IDs sharing the same (group, name) and assigns short
    // disambiguation prefixes. Two cases:
    //   * Exact pair (X, X+1)       → "B" / "T"   (Base / Total in X+1 form)
    //   * Multiple pairs (X1, X1+1, X2, X2+1, ...) → "B1/T1", "B2/T2", ...
    //   * Stragglers that don't pair up cleanly → "#NNNN" fallback
    // Pairing is detected by ascending sort then checking consecutive
    // ID delta == 1.
    private void AssignDisambiguationLabels(List<int> ids)
    {
        ids.Sort();
        if (ids.Count == 2 && ids[1] == ids[0] + 1)
        {
            _disambiguationLabel[ids[0]] = "B";
            _disambiguationLabel[ids[1]] = "T";
            return;
        }

        var pairIndex = 1;
        var i = 0;
        while (i < ids.Count)
        {
            if (i + 1 < ids.Count && ids[i + 1] == ids[i] + 1)
            {
                var suffix = pairIndex.ToString(CultureInfo.InvariantCulture);
                _disambiguationLabel[ids[i]]     = "B" + suffix;
                _disambiguationLabel[ids[i + 1]] = "T" + suffix;
                pairIndex++;
                i += 2;
            }
            else
            {
                // Straggler with no X+1 sibling — fall back to ID suffix so
                // the row stays distinguishable from its kin.
                _disambiguationLabel[ids[i]] = "#" + ids[i].ToString(CultureInfo.InvariantCulture);
                i++;
            }
        }

        BuildOrderedGroups();
        EnsureSlugDefaults();
        _classificationBuilt = true;
        _services.Log.Info($"[StatInspector] group classification built ({_groupMembers.Count} groups, {_ambiguousIds.Count} ambiguous IDs labelled Base/Total)");
    }

    private void BuildOrderedGroups()
    {
        _orderedGroups.Clear();
        // 1) Known display-order groups in their canonical order.
        foreach (var g in DefaultGroupOrder)
        {
            if (_groupMembers.ContainsKey(g)) _orderedGroups.Add(g);
        }
        // 2) Unknown groups alphabetical, excluding Other.
        var leftover = new List<string>();
        foreach (var g in _groupMembers.Keys)
        {
            var known = false;
            foreach (var d in DefaultGroupOrder)
            {
                if (string.Equals(d, g, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            }
            if (!known && !string.Equals(g, OtherGroup, StringComparison.OrdinalIgnoreCase))
                leftover.Add(g);
        }
        leftover.Sort(StringComparer.OrdinalIgnoreCase);
        _orderedGroups.AddRange(leftover);
        // 3) Other (if present) always last.
        if (_groupMembers.ContainsKey(OtherGroup)) _orderedGroups.Add(OtherGroup);

        // Sort each group's members by name so the picker reads cleanly.
        foreach (var kv in _groupMembers)
        {
            kv.Value.Sort((a, b) =>
            {
                var na = ResolveAttrName(a) ?? string.Empty;
                var nb = ResolveAttrName(b) ?? string.Empty;
                return string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    // Ensure every group has a slug-default in _groupCollapsed (unknown groups
    // default to collapsed). Lets us key the persistence by stable slug.
    private void EnsureSlugDefaults()
    {
        foreach (var g in _orderedGroups)
        {
            var slug = SlugFor(g);
            if (!_groupCollapsed.ContainsKey(slug))
            {
                _groupCollapsed[slug] = _windowSection.Get<bool>("group_" + slug + "_collapsed", true);
            }
        }
    }

    private static string SlugFor(string groupName)
    {
        if (GroupSlugMap.TryGetValue(groupName, out var slug)) return slug;
        // Fallback: stable lowercase alphanum slug.
        var chars = new char[groupName.Length];
        var n = 0;
        for (var i = 0; i < groupName.Length; i++)
        {
            var c = groupName[i];
            if (c is >= 'A' and <= 'Z') chars[n++] = (char)(c + 32);
            else if (c is >= 'a' and <= 'z' or >= '0' and <= '9') chars[n++] = c;
        }
        return n == 0 ? "unknown" : new string(chars, 0, n);
    }
}
