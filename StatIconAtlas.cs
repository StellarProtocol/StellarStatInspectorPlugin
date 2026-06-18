using System;
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
