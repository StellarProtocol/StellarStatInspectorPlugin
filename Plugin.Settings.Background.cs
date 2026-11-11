using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.StatInspector;

/// <summary>
/// Mini-HUD background opacity (user-approved 2026-11-12): a black fill behind the Borderless HUD at 0..1 opacity
/// (<see cref="WindowSpec.BackgroundOpacity"/>, poll-diffed by the framework), default 0 = no background (the original
/// look). Settings HUD panel row: "Background" label + round-knob slider + muted "N%" + a "?" help dot. Mirrors
/// CooldownBar's bar.bg_opacity, but the config save is DEBOUNCED: SliderElement calls Set on every drag frame, and a
/// config Save is a file write — one write per frame while dragging would hitch the game thread
/// (Plugin-Performance-Rules). The value itself updates live; only the disk write waits for the drag to settle.
/// </summary>
public sealed partial class Plugin
{
    private const string HudBgOpacityKey = "minihud_bg_opacity";
    private const float  HudBgSliderW    = 180f;
    private const float  HudBgPctW       = 40f;
    private const float  HudBgSaveDelay  = 0.5f;   // seconds after the last drag frame before the config is written

    // A plain field so the BackgroundOpacity Func is a field read (no allocation, no lookup). It lives on the plugin,
    // not on the window, so it survives RebuildMiniHud() (a column change re-registers the window with the same Func).
    private float _hudBgOpacity;
    private float _hudBgSaveAt = -1f;   // TimeNow when the pending save is due; -1 = nothing pending

    // "N%" per whole percent, built once on first use of that value (≤ 101 tiny strings for the plugin's lifetime), so
    // the label Func never formats per refresh.
    private static readonly string?[] _pctText = new string?[101];

    private void LoadHudBgOpacity()
    {
        var v = _windowSection.Get<float>(HudBgOpacityKey, 0f);
        _hudBgOpacity = float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;
    }

    private void SetHudBgOpacity(float v)
    {
        v = float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;
        if (v == _hudBgOpacity) return;
        _hudBgOpacity = v;
        _windowSection.Set(HudBgOpacityKey, v);   // in-memory now; written to disk by HudBgSaveTick once the drag settles
        _hudBgSaveAt = _services.Framework.TimeNow + HudBgSaveDelay;
    }

    // OnUpdate: write the debounced config once the slider has been still for HudBgSaveDelay.
    private void HudBgSaveTick()
    {
        if (_hudBgSaveAt < 0f || _services.Framework.TimeNow < _hudBgSaveAt) return;
        FlushHudBgSave();
    }

    // Also called from Dispose, so a drag released just before unload still persists.
    private void FlushHudBgSave()
    {
        if (_hudBgSaveAt < 0f) return;
        _hudBgSaveAt = -1f;
        _windowSection.Save();
    }

    private string HudBgPercent()
    {
        var pct = (int)MathF.Round(_hudBgOpacity * 100f);
        if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
        return _pctText[pct] ??= pct.ToString(CultureInfo.InvariantCulture) + "%";
    }

    // HUD panel row 2: same 88 px label box as "Columns" so both controls start at the same x in every language.
    // Body 376 = label 88 + slider 180 + pct 40 + "?" 24 + gaps; the flexible spacer pushes the "?" to the right edge,
    // directly under the Columns row's "?".
    private HudElement BuildHudBgRow() => new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(() => _loc.T("stat.background"), Color: () => TextColor(), NoWrap: true)
            { Ellipsize = true }, Width: ColumnsLabelW),
        // House slider: round 14 px knob (SquareHandle — without it HandleSize ADDS to the row height, a capsule).
        new SliderElement(() => _hudBgOpacity, SetHudBgOpacity) { Width = HudBgSliderW, SquareHandle = true, HandleSize = 14f },
        new TextElement(HudBgPercent, Color: () => MutedColor(), Width: HudBgPctW, Align: TextAlign.Right, NoWrap: true),
        new SpacerElement(0f),   // flexible
        HelpDot("stat.background", "stat.help.background", _bgHelpHover),   // Plugin.Settings.Help.cs
    }, Gap: 6f);

    private readonly HoverFlag _bgHelpHover = new();
}
