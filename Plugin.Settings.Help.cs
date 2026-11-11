using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.StatInspector;

/// <summary>
/// The round "?" beside the HUD Columns chips and its help popover, house style (Maestro Plugin.Help.cs /
/// Plugin.Help.Tip.cs): a 24 px Neutral circle (lighter on hover, accent-tinted while its tip is open) whose click opens
/// a title-bar-less GlassMenu popover anchored under it — 32 px accent "?" tile + bold setting name + muted
/// "HUD · HELP" + round ✕, then the help text in one Tile box, 360 wide. Re-clicking the "?" closes it. One tip only
/// (Columns), so there is no key registry.
/// </summary>
public sealed partial class Plugin
{
    private const float HelpSize    = 24f;    // the round "?" — exactly 24 × 24 (RoundRadius)
    private const float TipWidth    = 360f;
    private const float TipIconSize = 32f;

    private IWindowControl _tipWindow = null!;
    private WindowRect _tipRect;              // where the popover should sit (re-applied for a few ticks after opening)
    private int _tipRepositionTicks;
    private readonly HoverFlag _helpHover = new();
    private readonly HoverFlag _tipCloseHover = new();

    // Content = [24 px strut, the "?" in a 24 px box] side by side in ONE Row, so the circle is exactly its side (a
    // stacked spacer drew Maestro's "?" 24 × 26). CenterGlyphs centres the drawn "?" in its box.
    private HudElement HelpDot() => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "?", Color: () => NeutralText(), Width: HelpSize, Align: TextAlign.Center,
                NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: () => { })                       // the real work is in OnClickWithRect (needs the button's rect)
        {
            Bare = true,
            OnHover = over => _helpHover.On = over,
            OnClickWithRect = ToggleTip,
        })
    {
        FillColorFn = () => Fade(_tipWindow.IsShown ? Over(AccentColor() with { A = 0.35f }, NeutralFill())
                                 : _helpHover.On ? HoverFill(NeutralFill())
                                 : NeutralFill()),
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
    };

    private IWindowControl BuildAndRegisterTip()
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "statinspector.help",
                Title:       _loc.T("stat.help.title"),
                DefaultRect: new WindowRect(800f, 80f, TipWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                TitleProvider = () => _loc.T("stat.help.title"),
                Draggable = true, Closable = true, StartVisible = false,
                // No title bar: the header carries the title and a close; the whole frame is the drag handle.
                ShowTitleBar = false,
                // Help for the in-world Settings window — only while in the world and not on a loading screen.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            Root: BuildTipRoot(),
            OnClose: () => w!.SetVisible(false)));
        return w;
    }

    private HudElement BuildTipRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            TipIcon(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _loc.T("stat.columns"), Color: () => TextColor(), NoWrap: true, FontSize: 14)
                    { Bold = true, Ellipsize = true },
                new TextElement(TipSubtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 11)
                    { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            TipCloseButton(),
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(() => _loc.T("stat.help.columns"), Color: () => TextColor()))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    // "HUD · HELP" — composed only when the language changes (string.Equals on the two short templates).
    private string _tipSubSection = string.Empty, _tipSubHelp = string.Empty, _tipSub = string.Empty;

    private string TipSubtitle()
    {
        string section = _loc.T("stat.section.hud"), help = _loc.T("stat.help.title");
        if (!string.Equals(section, _tipSubSection, StringComparison.Ordinal) || !string.Equals(help, _tipSubHelp, StringComparison.Ordinal))
        {
            _tipSubSection = section; _tipSubHelp = help;
            _tipSub = (section + " · " + help).ToUpperInvariant();
        }
        return _tipSub;
    }

    // 32 px accent tile with a white bold "?" (the icon-tile look, drawn with text instead of a PNG).
    private HudElement TipIcon() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, TipIconSize),
            new TextElement(() => "?", Color: () => EnsureContrast(new ColorRgba(1f, 1f, 1f), AccentColor(), SolidMinContrast),
                Width: TipIconSize, Align: TextAlign.Center, NoWrap: true, FontSize: 18) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(TipIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
    }, Width: TipIconSize);

    // Round 24 px ✕ that closes the tip: muted glyph at rest, a soft round wash + text colour on hover.
    private HudElement TipCloseButton() => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "✕", Color: () => _tipCloseHover.On ? TextColor() : MutedColor(), Width: HelpSize,
                Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: CloseTip) { Bare = true, OnHover = over => _tipCloseHover.On = over })
    {
        FillColorFn = () => _tipCloseHover.On ? Fade(Over(TextColor() with { A = HoverMix }, PanelColor())) : Transparent,
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
    };

    // Toggle the tip: re-clicking the "?" closes it; otherwise anchor it under the "?".
    private void ToggleTip(WindowRect r)
    {
        if (_tipWindow.IsShown) { CloseTip(); return; }
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the tip drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 1f ? fw.CanvasWidth / fw.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = fw.CanvasHeight > 1f ? fw.CanvasHeight : fw.ScreenHeight;
        // Below the "?", right-aligned to it, so the popover opens to the LEFT over the chips instead of off the window.
        float x = MathF.Max(10f, r.X + r.Width - TipWidth);
        float y = MathF.Min(r.Y + r.Height + 4f, canvasH - 40f);
        _tipRect = new WindowRect(x, y, TipWidth, 0f);
        _tipWindow.SetVisible(true);
        _tipWindow.SetRect(_tipRect);
        // Always front (house rule): ZFront is sticky, and the Settings window it belongs to is fronted on every open, so
        // a never-fronted tip would open BEHIND it.
        _tipWindow.BringToFront();
        // The first-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-apply the anchored
        // rect for the next few ticks so it lands under the "?" on the first open too.
        _tipRepositionTicks = 4;
    }

    private void CloseTip()
    {
        _tipRepositionTicks = 0;
        if (_tipWindow.IsShown) _tipWindow.SetVisible(false);
    }

    // OnUpdate: re-assert the tip position while the reposition window is open.
    private void TipRepositionTick()
    {
        if (_tipRepositionTicks <= 0) return;
        _tipRepositionTicks--;
        if (_tipWindow.IsShown) _tipWindow.SetRect(_tipRect);
    }
}
