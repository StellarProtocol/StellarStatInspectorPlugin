namespace Stellar.StatInspector;

/// <summary>
/// Mini-HUD layout-edit preview. While the player arranges the HUD (layout edit-mode, Shift+`), the framework
/// force-shows every <c>EditModeDragOnly</c> window even when its <c>ShouldRender</c> is false (out of the world /
/// behind a menu — <c>WindowRenderer.ApplyValues</c>), so the outline the player drags must be the HUD's TRUE size:
/// <list type="bullet">
/// <item>stats live + ≥1 selected → the real cells with live values, delta flash suppressed (plain white);</item>
/// <item>nothing selected OR stats unavailable → <see cref="SampleAttrIds"/> as sample cells (game names + icons,
/// fixed sample values) instead of the "no stats" CTA / "not in world" text.</item>
/// </list>
/// All of it is read live from <see cref="Stellar.Abstractions.Services.IWindowHost.IsLayoutEditing"/> inside the cell
/// Funcs (no OnUpdate dependency — Framework.Update doesn't tick at the title screen, where the editor can also run).
/// The sample set is static; nothing here allocates per refresh.
/// </summary>
public sealed partial class Plugin
{
    // ATK, Crit, Haste, Mastery, Luck, Versatility — the stats a player most often pins.
    private static readonly int[] SampleAttrIds = { 11330, 11710, 11930, 11940, 11780, 11950 };

    // Shown only when GameData can't name the sample ids (e.g. the editor opened at the title screen before the
    // tables load). Developer-chosen stand-ins, normally replaced by the localized game names.
    private static readonly string[] SampleFallbackNames = { "ATK", "Crit", "Haste", "Mastery", "Luck", "Versatility" };

    private const string SampleFlatValue    = "1,234";
    private const string SamplePercentValue = "12.3%";

    private bool LayoutEditing => _services.Windows.IsLayoutEditing;

    // Sample cells replace the body only in edit mode, and only when the real grid would be empty or unavailable.
    private bool ShowingSample => LayoutEditing && (!_services.PlayerStats.IsAvailable || _miniSnapshot.Count == 0);

    private int MiniCellCount() => ShowingSample ? SampleAttrIds.Length : _miniSnapshot.Count;

    private string SampleLabel(int slot)
    {
        if (slot >= SampleAttrIds.Length) return string.Empty;
        var id = SampleAttrIds[slot];
        if (_labelOf.TryGetValue(id, out var cached)) return cached;
        // Pre-check the name so an unnamed sample shows its stand-in, not ResolveLabel's "#id" + orphan log line.
        if (_services.GameData.IsAvailable && !string.IsNullOrEmpty(ResolveAttrName(id))) return ResolveLabel(id);
        return SampleFallbackNames[slot];
    }

    private string SampleValue(int slot) =>
        slot >= SampleAttrIds.Length ? string.Empty : slot == 0 ? SampleFlatValue : SamplePercentValue;
}
