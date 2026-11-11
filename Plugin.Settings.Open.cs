using System;
using Stellar.Abstractions.Domain;

namespace Stellar.StatInspector;

/// <summary>
/// Opening / closing the Settings window, house style: open = show → <c>BringToFront()</c> → centre on the SCREEN for
/// a short burst of ticks. Not on the mini-HUD (the opener), unlike the usual OpenCentred rule: Settings is where you
/// pick the HUD's stats, so centring on the HUD covered the very thing being edited (user decision 2026-11-12). Always fronted: the help popover is fronted too, and <c>ZFront</c> is
/// sticky across hide/show (WindowBuilder-Patterns §"ZFront is STICKY", CLAUDE.md Rule 5 exception), so every open
/// path — the HUD gear and the HUD's "Open settings" button — goes through <see cref="OpenSettings"/>.
/// The burst is the Dungeon Confirm recipe (StellarDungeonHubPlugin Plugin.DungeonConfirm.Centre.cs): a SetRect right
/// after SetVisible(true) is ignored on the first open (no token yet) and the mount then restores the saved rect, so
/// re-centre every tick for <see cref="CentreTicks"/> ticks — but stop for good the moment the window is no longer
/// where we left it (the user dragged it), or the burst snaps the first drag back.
/// </summary>
public sealed partial class Plugin
{
    private const int CentreTicks = 30;   // plugin ticks (≥ 1 s at 30 Hz) — covers mount + deferred apply + layout settle

    private int _centreLeft;
    private (float X, float Y)? _centrePlaced;   // where we last left the MOUNTED window (null = not placed yet)

    private void OpenSettings()
    {
        _settingsWindow.SetVisible(true);
        _settingsWindow.BringToFront();
        _centrePlaced = null;
        _centreLeft = CentreTicks;
        CentreSettingsNow();   // already mounted (parked) → lands centred this tick; first open → waits for the mount
        // settings_visible is persisted by SyncSettingsVisibility (Plugin.cs) once IsShown flips.
    }

    // The ✕ in the title bar and the gear's second click.
    private void CloseSettings()
    {
        _settingsWindow.SetVisible(false);
        _centreLeft = 0;
        _clearConfirmAt = -1f;
        CloseTip();
        ClearPeek();
    }

    private void ToggleSettingsFromGear()
    {
        if (LayoutEditing) return;   // the gear is dimmed + inert while arranging (edit-mode clicks are for dragging)
        if (_settingsWindow.IsShown) CloseSettings();
        else OpenSettings();
    }

    // OnUpdate: one centring step while the burst runs.
    private void SettingsCentreTick()
    {
        if (_centreLeft <= 0) return;
        _centreLeft--;
        if (_settingsWindow.IsShown && _centrePlaced is { } p)
        {
            var cur = _settingsWindow.Rect;
            if (Math.Abs(cur.X - p.X) >= 0.5f || Math.Abs(cur.Y - p.Y) >= 0.5f) { _centreLeft = 0; return; }   // user's now
        }
        CentreSettingsNow();
    }

    // Centre the Settings window on the screen (canvas units), clamped on-screen and floored to whole units.
    // SetRect only when off target.
    private void CentreSettingsNow()
    {
        if (!_settingsWindow.IsShown) return;   // not mounted yet: Rect is the DefaultRect, SetRect would be ignored
        var fw = _services.Framework;
        float cw = fw.CanvasWidth, ch = fw.CanvasHeight;
        if (cw <= 1f || ch <= 1f) return;
        var r = _settingsWindow.Rect;
        float w = r.Width > 1f ? r.Width : SettingsWindowWidth;
        float h = r.Height > 1f ? r.Height : SettingsWindowHeight;
        float cx = cw / 2f, cy = ch / 2f;
        float x = MathF.Floor(MathF.Max(0f, MathF.Min(cx - w / 2f, cw - w)));
        float y = MathF.Floor(MathF.Max(0f, MathF.Min(cy - h / 2f, ch - h)));
        if (Math.Abs(r.X - x) >= 0.5f || Math.Abs(r.Y - y) >= 0.5f) _settingsWindow.SetRect(new WindowRect(x, y, w, h));
        var after = _settingsWindow.Rect;   // read back: SetRect clamps on-screen, so record the live pose, not (x, y)
        _centrePlaced = (after.X, after.Y);
    }
}
