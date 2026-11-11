using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.StatInspector;

/// <summary>
/// Settings window feedback (house rule 13 — every click answers where you clicked) and the footer. A stat click
/// flashes its row green with bold "Added" / "Removed" for <see cref="RowFlashDurS"/> (keyed by attr id + time, so a
/// scroll or a re-bind keeps the flash on the right stat) and puts "✓ Added “x” to the HUD · N" in the footer for
/// <see cref="FooterNoteDurS"/>. The footer is ONE 28 px row that never changes height: "{N} selected" (or that ✓ line)
/// + a Caution "Clear all" pill, which swaps the row for an inline confirm ("Remove all N from the HUD?" + Caution
/// "Remove all" + Neutral "Cancel") that times out after <see cref="ResetPromptDurS"/>.
/// </summary>
public sealed partial class Plugin
{
    private const float RowFlashDurS   = 1.0f;
    private const float FooterNoteDurS = 3.0f;
    // Pill label boxes = the longest label in any shipped locale (bold 13; id "Hapus semua" ≈ 84 px, fil "Kanselahin"
    // ≈ 69 px) + headroom, so a language switch never resizes a pill (house rule 10).
    private const float CautionPillLabelW = 88f;
    private const float CancelPillLabelW  = 72f;

    private int    _flashAttr = -1;
    private float  _flashAt;
    private bool   _flashAdded;
    private string _footerNote = string.Empty;   // composed once per click (not per refresh)
    private float  _footerNoteAt = float.NegativeInfinity;
    private float  _clearConfirmAt = -1f;        // ≥ 0 while the inline Clear-all confirm is showing

    private bool ClearConfirming => _clearConfirmAt >= 0f;

    private readonly FmtCache _selectedFmt = new();
    private readonly FmtCache _confirmFmt  = new();

    // ── footer ────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildSettingsFooter() => new ConditionalElement(() => ClearConfirming,
        new RowElement(new HudElement[]
        {
            // Bold 12 (not 13): en "Remove all 99 from the HUD?" ≈ 169 px must fit beside both pills in 396 px.
            new CellElement(new TextElement(ConfirmQuestion, Color: () => TextColor(), NoWrap: true, FontSize: 12)
                { Bold = true, Ellipsize = true }, Weight: 1f),
            CautionButton(() => _loc.T("stat.clearAll.yes"), ConfirmClearAll, CautionPillLabelW),
            NeutralButton(() => _loc.T("stat.cancel"), CancelClearAll, CancelPillLabelW),
        }, Gap: 8f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(FooterText, Color: FooterTextColor, NoWrap: true) { Ellipsize = true },
                Weight: 1f),
            CautionButton(() => _loc.T("stat.clearAll"), BeginClearAll, CautionPillLabelW,
                enabled: () => _selected.Count > 0),
        }, Gap: 8f));

    private bool FooterNoteShowing() => _services.Framework.TimeNow - _footerNoteAt < FooterNoteDurS;

    private string FooterText() => FooterNoteShowing()
        ? _footerNote
        : _selectedFmt.Get(_loc.T("stat.footer.selected"), _selected.Count);

    private ColorRgba? FooterTextColor() => FooterNoteShowing() ? SuccessColor() : MutedColor();

    // "Remove all 1 from the HUD?" reads wrong → a singular template for one stat.
    private string ConfirmQuestion() => _confirmFmt.Get(
        _loc.T(_selected.Count == 1 ? "stat.clearAll.confirm.one" : "stat.clearAll.confirm"), _selected.Count);

    // ── row flash ─────────────────────────────────────────────────────────────────────────────────────────────────
    private bool RowFlashing(int slot)
    {
        var id = EntryAt(slot).AttrId;
        return id > 0 && id == _flashAttr && _services.Framework.TimeNow - _flashAt < RowFlashDurS;
    }

    private string FlashLabel() => _loc.T(_flashAdded ? "stat.row.added" : "stat.row.removed");

    // The green word sits on the green-washed tile; keep it readable on the Light theme too.
    private ColorRgba? FlashTextColor() => EnsureContrast(SuccessColor(), Over(SuccessColor() with { A = FlashWashAlpha }, TileColor()), 3f);

    // ── events ────────────────────────────────────────────────────────────────────────────────────────────────────
    private void OnStatToggled(int attrId, string name, bool added)
    {
        var now = _services.Framework.TimeNow;
        _flashAttr = attrId;
        _flashAt = now;
        _flashAdded = added;
        _footerNote = _loc.TFormat(added ? "stat.feedback.added" : "stat.feedback.removed", name, _selected.Count);
        _footerNoteAt = now;
        _clearConfirmAt = -1f;   // any other action dismisses a pending confirm
    }

    private void BeginClearAll()
    {
        if (_selected.Count == 0) return;
        _clearConfirmAt = _services.Framework.TimeNow;
    }

    private void CancelClearAll() => _clearConfirmAt = -1f;

    private void ConfirmClearAll()
    {
        _clearConfirmAt = -1f;
        DoReset();   // Plugin.cs — unsubscribes + clears the selection (the HUD empties)
        _flashAttr = -1;
        _footerNote = _loc.T("stat.feedback.cleared");
        _footerNoteAt = _services.Framework.TimeNow;
    }

    // OnUpdate (while shown): an unanswered confirm falls back to the normal footer.
    private void AdvanceClearConfirm(float now)
    {
        if (ClearConfirming && now - _clearConfirmAt > ResetPromptDurS) _clearConfirmAt = -1f;
    }
}
