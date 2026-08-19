using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AbsUv = Stellar.Abstractions.Domain.UvRect;

namespace Stellar.StatInspector;

/// <summary>
/// Bundled stat-icon atlas. The mockup-approved emoji icons were rasterised to a
/// single transparent PNG (8×5 grid), embedded in this DLL. Each attribute maps to a
/// cell by keyword (<see cref="IndexFor"/>); the framework's <c>SpriteElement</c> draws
/// it from the raw <see cref="Png"/> bytes using the <see cref="UvRectFor"/> sub-rect.
///
/// <para>This replaces loading the game's own attribute icons — the game's
/// AttrDescription has none and the user chose the bundled emoji set. Self-
/// contained: the PNG ships inside the assembly (no extra deploy files).</para>
/// </summary>
internal sealed class StatIconAtlas
{
    private const string ResourceName = "Stellar.StatInspector.stat-icon-atlas.png";
    private const int Cols = 8;
    private const int Rows = 6;

    /// <summary>Atlas cell for the settings cog (⚙️) — used by the mini-HUD gear button.</summary>
    public const int GearIndex = 40;

    private const string GearResourceName = "Stellar.StatInspector.settings-gear.png";
    private byte[]? _png, _gearPng;
    private bool _pngFailed, _gearFailed;

    /// <summary>Raw embedded settings-gear PNG bytes (cached) — a clean standalone cog for the mini-HUD header
    /// button (crisper than the emoji-atlas gear cell). Null if the resource is missing.</summary>
    public byte[]? GearPng
    {
        get
        {
            if (_gearPng != null || _gearFailed) return _gearPng;
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(GearResourceName);
                if (s == null) { _gearFailed = true; return null; }
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                _gearPng = ms.ToArray();
            }
            catch { _gearFailed = true; }
            return _gearPng;
        }
    }

    /// <summary>Raw embedded atlas PNG bytes (cached), for SpriteElement.Atlas. Null if the resource is missing.</summary>
    public byte[]? Png
    {
        get
        {
            if (_png != null || _pngFailed) return _png;
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (s == null) { _pngFailed = true; return null; }
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                _png = ms.ToArray();
            }
            catch { _pngFailed = true; }
            return _png;
        }
    }

    /// <summary>Atlas sub-rect (origin bottom-left) as an Abstractions UvRect for SpriteElement.</summary>
    public static AbsUv UvRectFor(int index)
    {
        if (index < 0) index = 0;
        var col = index % Cols;
        var row = index / Cols;
        float w = 1f / Cols, h = 1f / Rows;
        return new AbsUv(col * w, 1f - (row + 1) * h, w, h);
    }

    // Stable, LOCALE- AND FRAMEWORK-INDEPENDENT numeric EAttrType id -> atlas cell. This is the
    // primary lookup: the name-based IndexFor below only matches when the localized attribute name
    // contains an English keyword, so on non-English clients (JP: 会心/幸運/万能/器用さ/ファスト/
    // 幻夢強度/火属性強度…) every match misses and the row falls to the 📊 placeholder. Keying on the
    // numeric attrId (from the game's FightAttrTable) is stable across locales AND framework
    // versions — deliberately NO dependency on the framework catalog / AttributeInfo.EnumName, which
    // the shipped 2.1.0 framework does not populate at runtime (so an EnumName-keyed lookup silently
    // returned the placeholder). Cell numbers mirror StatIndex/ElementIndex — same emoji per concept.
    // Generated from FightAttrTable.json (id ↔ concept); see the plugin's icon-mapping notes.
    private static readonly Dictionary<int, int> AttrCell = new()
    {
        // Movement / mount speed / revive.
        [10200]=3, [10210]=3, [10220]=3, [10230]=31, [10240]=30, [10250]=30, [10260]=30, [10270]=31,
        // Primaries / secondary ratings / HP / attack / defense.
        [11010]=1, [11020]=2, [11030]=3, [11040]=4, [11110]=10, [11120]=12, [11130]=13, [11140]=14,
        [11150]=15, [11170]=16, [11310]=5, [11320]=5, [11330]=6, [11340]=7, [11350]=8, [11360]=8,
        [11370]=17, [11380]=18, [11390]=17, [11400]=18, [11410]=6, [11420]=8, [11430]=7, [11440]=18,
        // Element attack / per-element atk.
        [11500]=27, [11510]=19, [11520]=20, [11530]=21, [11540]=22, [11550]=23, [11560]=24, [11570]=25,
        [11580]=26, [11710]=10, [11720]=32, [11730]=32, [11740]=32, [11750]=31, [11760]=31, [11780]=13,
        [11790]=28, [11800]=28, [11810]=29, [11820]=29, [11840]=36, [11850]=35, [11880]=34, [11890]=34,
        [11910]=33, [11930]=12, [11940]=14, [11950]=15, [11960]=31, [11970]=16, [11980]=31, [11990]=32,
        // Crit dmg / luck / block / damage-inc + reduction families / near-far.
        [12510]=11, [12520]=11, [12530]=13, [12540]=16, [12550]=36, [12560]=35, [12570]=36, [12580]=35,
        [12590]=36, [12600]=35, [12610]=36, [12620]=35, [12630]=36, [12640]=35, [12650]=29, [12660]=29,
        [12670]=36, [12680]=35, [12690]=36, [12700]=35, [12720]=13, [12730]=36, [12740]=28, [12750]=36,
        [12760]=35, [12790]=6, [12800]=7,
        // Element power / bonus / defense / resist (13xxx).
        [13000]=27, [13010]=19, [13020]=20, [13030]=21, [13040]=22,
        [13050]=23, [13060]=24, [13070]=25, [13080]=26, [13100]=27, [13110]=19, [13120]=20, [13130]=21,
        [13140]=22, [13150]=23, [13160]=24, [13170]=25, [13180]=26, [13200]=27, [13210]=19, [13220]=20,
        [13230]=21, [13240]=22, [13250]=23, [13260]=24, [13270]=25, [13280]=26, [13310]=27, [13320]=19,
        [13330]=20, [13340]=21, [13350]=22, [13360]=23, [13370]=24, [13380]=25, [13390]=26, [13400]=5,
        [13410]=8,
    };

    /// <summary>Atlas cell for a numeric EAttrType id (locale + framework independent), or -1 when
    /// unmapped. A Base/Total pair is (X, X+1) and the Total variant carries no own row in the game
    /// table, so it falls back to the base id's cell.</summary>
    public static int IndexForAttr(int attrId)
    {
        if (AttrCell.TryGetValue(attrId, out var i)) return i;
        if (AttrCell.TryGetValue(attrId - 1, out var baseCell)) return baseCell;
        return -1;
    }

    // Attribute name -> atlas cell. Order matches the rasteriser's emoji list +
    // keyword rules (specific before general). Falls back to cell 0 (📊).
    public static int IndexFor(string name)
    {
        var s = (name ?? string.Empty).ToLowerInvariant();
        var el = ElementIndex(s);   // elements first (their names contain "attack")
        return el >= 0 ? el : StatIndex(s);
    }

    private static int ElementIndex(string s)
    {
        if (Has(s, "fire")) return 19;
        if (Has(s, "ice")) return 20;
        if (Has(s, "forest")) return 21;
        if (Has(s, "thunder")) return 22;
        if (Has(s, "wind")) return 23;
        if (Has(s, "rock")) return 24;
        if (Has(s, "light attack")) return 25;
        if (Has(s, "dark")) return 26;
        if (Has(s, "all element")) return 27;
        return -1;
    }

    private static int StatIndex(string s)
    {
        // Primaries / core.
        if (Has(s, "strength")) return 1;
        if (Has(s, "illusion")) return 18;
        if (Has(s, "intellect") || Has(s, "intelligence")) return 2;
        if (Has(s, "agility")) return 3;
        if (Has(s, "endurance")) return 4;
        if (Has(s, "max hp")) return 5;
        if (Has(s, "crit dmg")) return 11;
        if (Has(s, "crit")) return 10;
        if (Has(s, "haste")) return 12;
        if (Has(s, "lucky") || Has(s, "luck")) return 13;
        if (Has(s, "mastery")) return 14;
        if (Has(s, "versatility")) return 15;
        if (Has(s, "block")) return 16;
        if (Has(s, "magic pen")) return 18;
        if (Has(s, "pen")) return 17;
        if (Has(s, "matk") || Has(s, "mag boost")) return 7;
        if (Has(s, "atk reduction") || Has(s, "matk reduction")) return 35;
        if (Has(s, "atk")) return 6;
        if (Has(s, "armor") || Has(s, "defense")) return 8;
        if (Has(s, "resistance")) return 9;
        // Speeds / timing.
        if (Has(s, "mount speed")) return 30;
        if (Has(s, "revive")) return 31;
        if (Has(s, "cast speed") || Has(s, "charging") || Has(s, "attack spd")) return 32;
        if (Has(s, "speed")) return 3;
        // Support / misc.
        if (Has(s, "healing")) return 28;
        if (Has(s, "shield")) return 29;
        if (Has(s, "cd ") || Has(s, "cooldown") || Has(s, "skill cd") || Has(s, "trigger interval")) return 31;
        if (Has(s, "rage")) return 33;
        if (Has(s, "suppress")) return 34;
        if (Has(s, "reduction")) return 35;
        if (Has(s, "boost") || Has(s, "bonus")) return 36;
        if (Has(s, "ability score")) return 37;
        if (Has(s, "resilience")) return 38;
        if (Has(s, "companion")) return 39;
        return 0;
    }

    private static bool Has(string s, string k) => s.IndexOf(k, StringComparison.Ordinal) >= 0;
}
