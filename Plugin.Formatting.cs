using System.Collections.Generic;
using System.Globalization;

namespace Stellar.StatInspector;

/// <summary>
/// Value-formatting heuristic (UI design Decision 5). Walks every plausible attr
/// ID at construction; for each ID, resolves the display name from
/// <see cref="Stellar.Abstractions.Services.IGameDataCombat.GetAttributeProfile"/>
/// (Profile carries the in-game panel labels — typically the source of any "%"
/// suffix) with a fallback to
/// <see cref="Stellar.Abstractions.Services.IGameDataCombat.GetAttribute"/>;
/// classifies each ID as Raw or one of four Percent flavors via name suffix →
/// keyword fallback → blocklist override. The Percent scale is resolved lazily
/// on first non-zero sample for each ID so we don't pre-decide based on a
/// presumption.
/// </summary>
public sealed partial class Plugin
{
    private enum FormatKind { Raw, PercentTenths, PercentHundredths, PercentThousandths, PercentRaw, PercentUndetermined }

    // attrId -> initial classification. Percent attrs start as PercentUndetermined
    // and flip to one of the three concrete percent kinds on first non-zero sample.
    private readonly Dictionary<int, FormatKind> _formatOf = new();

    // Keywords that suggest percent-formatting when the name doesn't carry a % suffix
    // (covers EN localizations that strip the symbol).
    private static readonly string[] PercentKeywords =
    {
        "crit", "block", "dodge", "haste", "lifesteal", "penetration",
        "resist", "damage taken", "movement speed", "attack speed",
        // Secondary combat stats the game shows as % (raw/100): confirmed in-game
        // Luck 15.39%, Mastery 15.45%, Versatility 7.65%, Attack/Casting SPD 9.73%/
        // 10.22%, element {Wind} Bonus 11.12%. "spd" covers the abbreviated speed
        // labels; "bonus" covers the elemental bonus family.
        "luck", "mastery", "versatility", "spd", "bonus",
    };

    // Static blocklist — primary attributes that are stored as straight ints
    // even though some have keyword overlaps. Starter list members default to Raw.
    private static readonly HashSet<int> RawBlocklist = new()
    {
        11011, // Strength
        11021, // Intellect
        11031, // Agility (assumed; harmless if not present)
        11331, // AttackTotal
        11341, // MAttackTotal
        11321, // MaxHp
    };

    // Hardcoded percent scales for stats whose raw encoding doesn't match
    // the default Hundredths (raw × 100 = basis points) scale that most
    // stat families use. Add IDs here only after confirming the in-game
    // value side-by-side with the mini-HUD reading. Map is checked BEFORE
    // keyword matching, so an entry here always wins.
    private static readonly Dictionary<int, FormatKind> KnownPercentScales = new();

    // Scan attr IDs in a reasonable range (Phase 6 recon found 1252 IDs span
    // ~1..14000). We don't pre-allocate a 14k dictionary — we only memoize on demand.
    private const int AttrIdScanMax = 14000;

    private void BuildFormatCache()
    {
        if (!_services.GameData.IsAvailable)
        {
            // GameData may not be eager-loaded yet on first frame; defer to first lookup.
            return;
        }

        var classified = 0;
        for (var id = 1; id <= AttrIdScanMax; id++)
        {
            var name = ResolveFormatName(id);
            if (name is null) continue;
            var kind = ClassifyByName(id, name);
            _formatOf[id] = kind;
            classified++;
        }

        _services.Log.Info($"[StatInspector] format cache built ({classified} attrs classified)");
    }

    // Resolves the name used for percent-detection. Profile entries (the
    // in-game Attributes-panel labels) are the most reliable source of "%"
    // suffix evidence — and they cover IDs missing from AttrDescriptionBase
    // such as CritDamage 12511. Falls back to AttrDescription's name for IDs
    // present there but absent from Profile.
    private string? ResolveFormatName(int attrId)
    {
        var profileName = _services.GameData.Combat.GetAttributeProfile(attrId)?.Name;
        if (!string.IsNullOrEmpty(profileName)) return profileName;
        return _services.GameData.Combat.GetAttribute(attrId)?.Name;
    }

    private FormatKind ClassifyByName(int attrId, string name)
    {
        // Override path — primary attrs always Raw regardless of name keywords.
        if (RawBlocklist.Contains(attrId)) { LogFormatDetect(attrId, name, "Raw(blocklist)"); return FormatKind.Raw; }

        // Hardcoded scale path — stats with confirmed in-game scale that the
        // magnitude heuristic guesses wrong. Returns the concrete kind so the
        // lazy scale-resolution in FormatValue doesn't override it.
        if (KnownPercentScales.TryGetValue(attrId, out var fixedKind))
        {
            LogFormatDetect(attrId, name, $"Percent(hardcoded:{fixedKind})");
            return fixedKind;
        }

        // Data-driven path (authoritative) — FightAttrTable.AttrNumType: 0 = raw int,
        // >=1 = percent (value/100). Only the name-keyword fallback below runs when the
        // attribute has no FightAttr row (NumType -1).
        var numType = _services.GameData.Combat.GetAttribute(attrId)?.NumType ?? -1;
        if (numType == 0) { LogFormatDetect(attrId, name, "Raw(numType=0)"); return FormatKind.Raw; }
        if (numType >= 1) { LogFormatDetect(attrId, name, $"Percent(numType={numType})"); return FormatKind.PercentUndetermined; }

        var trimmed = name.TrimEnd();
        if (trimmed.EndsWith("%") || trimmed.Contains("(%)"))
        {
            LogFormatDetect(attrId, name, "Percent(name-%)");
            return FormatKind.PercentUndetermined;
        }

        var lower = name.ToLowerInvariant();
        foreach (var kw in PercentKeywords)
        {
            if (lower.Contains(kw))
            {
                LogFormatDetect(attrId, name, $"Percent(keyword:{kw})");
                return FormatKind.PercentUndetermined;
            }
        }

        return FormatKind.Raw;
    }

    private string FormatValue(int attrId, long value)
    {
        if (!_formatOf.TryGetValue(attrId, out var kind))
        {
            // Lazy classification path (cache not built at ctor or attr discovered later).
            var name = ResolveFormatName(attrId);
            if (name is null) return value.ToString("N0", CultureInfo.InvariantCulture);
            kind = ClassifyByName(attrId, name);
            _formatOf[attrId] = kind;
        }

        // Lazy scale resolution for percent attrs on first non-zero sample.
        // Universal default: Hundredths (raw × 100 = basis points encoding
        // common across stat families). Worked across observed samples:
        //   * Crit DMG raw  5000 → 50.0% ✓
        //   * Crit Rate raw  500 → 5.0%  ✓
        //   * Haste raw       70 → 0.7%  ✓
        // Magnitude-based picking (old: ≥1000 Hundredths / ≥10 Tenths) gave
        // 10× errors for low-magnitude percent stats. Outliers that need a
        // different divisor go in KnownPercentScales (checked at classify
        // time, bypassing this lazy path).
        if (kind == FormatKind.PercentUndetermined && value != 0)
        {
            kind = FormatKind.PercentHundredths;
            _formatOf[attrId] = kind;
            LogPercentScale(attrId, value, kind.ToString());
        }

        return kind switch
        {
            FormatKind.Raw                  => AbbreviateInt(value),
            FormatKind.PercentRaw           => value.ToString("0", CultureInfo.InvariantCulture) + "%",
            FormatKind.PercentTenths        => (value / 10.0).ToString("F1", CultureInfo.InvariantCulture) + "%",
            FormatKind.PercentHundredths    => (value / 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%",
            FormatKind.PercentThousandths   => (value / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "%",
            FormatKind.PercentUndetermined  => AbbreviateInt(value), // value==0
            _                               => AbbreviateInt(value),
        };
    }

    // K/M abbreviation for Raw integers that would otherwise overflow the
    // mini-HUD's 100px value column. Threshold chosen so 5-char values like
    // "9,999" render literally; 6+ char values (10,000+) get compressed.
    //   <10,000   → "9,999"     (N0 with thousands separator)
    //   <1M       → "203K"      (no decimal)
    //   ≥1M       → "1.2M"      (one decimal)
    private static string AbbreviateInt(long value)
    {
        var abs = value < 0 ? -value : value;
        var sign = value < 0 ? "-" : "";
        if (abs >= 1_000_000)
            return sign + (abs / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        if (abs >= 10_000)
            return sign + (abs / 1_000).ToString(CultureInfo.InvariantCulture) + "K";
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
