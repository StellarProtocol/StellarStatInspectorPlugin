using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.StatInspector;

/// <summary>
/// Settings window, house style (Stellar Design Language; user-approved prototype
/// <c>_scratch\prototypes\statinspector-settings.html</c>, 2026-11-12). A fixed-size 420 px GlassMenu window:
/// ToolHeader (accent bar-chart tile + live status) → HUD panel (Columns chips + "?" help) → STATS panel (search +
/// clear, then the searchable / grouped / collapsible picker over the ~1252 classified attributes as a scroll-windowed
/// <c>VirtualListElement</c> over the flattened <see cref="_snapshot"/>) → a 28 px footer (selected count / click
/// feedback + Clear all, which turns into an inline confirm). Rows: Plugin.Settings.Rows.cs; feedback, footer and the
/// clear-all confirm: Plugin.Settings.Feedback.cs; help popover: Plugin.Settings.Help.cs; open + centring:
/// Plugin.Settings.Open.cs; colours / components: Plugin.Style*.cs.
/// </summary>
public sealed partial class Plugin
{
    // Window: 420 wide, FIXED height (WindowSpec.FixedHeight), so nothing the window shows — search results, loading,
    // the confirm, feedback — can grow or shrink it (house rule 14). Height = title bar 32 + body padding 2×12 + header
    // ≈42 + HUD panel ≈64 + STATS panel ≈74 + list + footer 28 + 3 gaps of 10 = 292 + list; with the 9-row list (250)
    // ≈ 544. The list viewport is FLEXIBLE (no VirtualList.FixedHeight), so if a measured line height differs from the
    // estimate the list absorbs the few px instead of the footer being squeezed or a gap opening below it.
    private const float SettingsWindowWidth  = 420f;
    private const float SettingsWindowHeight = 544f;
    private const int   PickerVisibleRows    = 9;
    private static readonly float PickerViewportH = ListHeight(PickerVisibleRows);   // 250
    private const int   SettingsPoolSize     = 14;   // rendered row pool: ⌈viewport ÷ 28⌉ + margin (viewport may flex a bit)
    private const float SettingsGap          = 10f;  // between header / panels / footer (the prototype's 10)
    // Body width 396 (420 − 2×12) − panel padding 2×10 = 376 inside a panel; the search field takes it minus the ✕ (28)
    // and the 6 px gap. Fixed: InputElement.Width wins over the row layout (WindowBuilder-Patterns §SingleLine).
    private const float SearchInputW         = 376f - IconButtonSize - 6f;
    // "Columns" label box: the longest translation (fil "Mga column" ≈ 78 px at 14 px) + headroom, so the chips start
    // at the same x in every language.
    private const float ColumnsLabelW        = 88f;
    private const float ColumnChipW          = 34f;

    private int _winFirst;   // first visible logical row index (set by the VirtualListElement OnWindow callback)
    private string _search = string.Empty;
    private bool _scrollReset;   // search text changed → the list jumps back to the top on its next refresh

    private IWindowControl BuildAndRegisterSettings()
    {
        var spec = new WindowSpec(
            Id:          "statinspector.settings",
            Title:       _loc.T("stat.settings.title"),
            DefaultRect: new WindowRect(800f, 20f, SettingsWindowWidth, SettingsWindowHeight),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            TitleProvider = () => _loc.T("stat.settings.title"),   // live-localized; Title stays the fallback
            StartVisible = _windowSection.Get<bool>("settings_visible", false),
            Draggable    = true,
            Closable     = true,
            FixedHeight  = true,   // the height is DefaultRect.Height, never the content's (see the sizing note above)
            // Companion picker for the in-world stat HUD: only meaningful while playing, so gate to World (v2)
            // and hide during loading screens.
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                 && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            // Framework pre-build (fw 2.30.0): the ~14-row picker pool is built at the title screen and filled off-screen
            // once in the world, so the first open is a re-show. Build-time callbacks (labels, TitleProvider) read only
            // _loc / plugin state; the value lambdas read _snapshot (plugin data) and IPlayerStats/GameData exactly as
            // they do when shown. _snapshot is kept current while hidden (OnUpdate → RebuildSnapshotIfDirty, a dirty-
            // flag no-op almost always). Signature = classification ready + flattened row count (loading → real rows).
            Prebuild          = true,
            PrebuildSignature = () => HashCode.Combine(_classificationBuilt, _snapshot.Count),
        };
        return _services.Windows.Register(new WindowRegistration(spec, BuildSettingsRoot(),
            OnClose: CloseSettings));   // Plugin.Settings.Open.cs
    }

    // Built ONCE. Every Func re-pulls live state on the framework's capped refresh; the picker's slot Funcs index the
    // _snapshot at _winFirst+slot (rebuilt only when search / collapse / classification changes).
    private HudElement BuildSettingsRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader("stat-header-24.png", () => _loc.T("stat.tool.title"), SettingsStatus),
        BuildHudSection(),
        BuildStatsSection(),
        BuildSettingsFooter(),   // Plugin.Settings.Feedback.cs
    }, Gap: SettingsGap);

    // HUD panel: "Columns" + chips 1–4 + the round "?" at the right edge (opens the help popover).
    private HudElement BuildHudSection()
    {
        var row = new HudElement[4 + 3];
        row[0] = new CellElement(new TextElement(() => _loc.T("stat.columns"), Color: () => TextColor(), NoWrap: true)
            { Ellipsize = true }, Width: ColumnsLabelW);
        for (var n = 1; n <= 4; n++) row[n] = ColumnChip(n);
        row[5] = new SpacerElement(0f);   // flexible: the "?" lands on the panel's right edge
        row[6] = HelpDot();               // Plugin.Settings.Help.cs
        return SectionPanel(() => _loc.T("stat.section.hud"), () => _loc.T("stat.section.hud.hint"),
            new RowElement(row, Gap: 6f));
    }

    // One columns chip: on = bold accent on a 22 % accent wash, off = muted on the Tile. Two complete chips in the same
    // 34 px cell, swapped by a Conditional (bold can't toggle live), so picking a count never moves anything.
    private HudElement ColumnChip(int n)
    {
        var hover = new HoverFlag();
        var label = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Label() => label;
        void Pick() => SetColumns(n);
        return new CellElement(new ConditionalElement(() => _columns == n,
            ChipButton(DropBold(new TextElement(Label, Color: () => EnsureContrast(AccentColor(), ChipOnFill(), SolidMinContrast),
                    Width: ColumnChipW, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true }),
                ChipOnFill, hover, Pick, ColumnChipW),
            ChipButton(new TextElement(Label, Color: () => MutedColor(), Width: ColumnChipW, Align: TextAlign.Center, NoWrap: true),
                TileColor, hover, Pick, ColumnChipW)), Width: ColumnChipW);
    }

    private ColorRgba ChipOnFill() => Over(AccentColor() with { A = AccentWashAlpha }, PanelColor());

    // STATS panel: search + ✕, then the picker (or the loading note) in a list area that never changes height.
    private HudElement BuildStatsSection()
    {
        var search = new RowElement(new HudElement[]
        {
            new InputElement(() => _search, SetSearch, SearchInputW, OnChange: SetSearch)
            {
                SingleLine  = true,   // one fixed 28 px line: a long paste scrolls sideways instead of growing the panel
                Placeholder = () => _loc.T("stat.search.placeholder"),
            },
            IconButton(() => "✕", () => SetSearch(string.Empty)),
        }, Gap: 6f);

        var slots = new HudElement[SettingsPoolSize];
        for (var i = 0; i < SettingsPoolSize; i++) { var s = i; slots[s] = BuildPickerSlot(s); }   // Plugin.Settings.Rows.cs
        var picker = new VirtualListElement(() => _snapshot.Count, ListRowPitch, slots, f => _winFirst = f,
            Height: PickerViewportH)
        {
            ResetScroll = ConsumeScrollReset,
        };

        // Game data not classified yet (title screen, or GameData still loading): a centred note in the same area.
        var loading = new ColumnElement(new HudElement[]
        {
            new SpacerElement(0f, PickerViewportH / 2f - 24f),
            new TextElement(() => _loc.T("stat.loading"), Color: () => MutedColor(), Align: TextAlign.Center),
            new TextElement(() => _loc.T("stat.loading.hint"), Color: () => MutedColor(), Align: TextAlign.Center),
        }, Gap: 4f);

        return SectionPanel(() => _loc.T("stat.section.stats"), StatsHint,
            search,
            new ConditionalElement(() => _classificationBuilt, picker, loading, Fill: true));
    }

    private readonly FmtCache _statusFmt = new();
    private readonly FmtCache _matchesFmt = new();

    // The header's live status (the ONE status builder): "{N} stats on the HUD · {C} columns", or loading. Each count
    // can be singular, so one full template per combination (no runtime concatenation → FmtCache stays alloc-free).
    private string SettingsStatus()
    {
        if (!_classificationBuilt) return _loc.T("stat.status.loading");
        int n = _selected.Count;
        var key = n == 1
            ? (_columns == 1 ? "stat.status.oneBoth" : "stat.status.oneStat")
            : (_columns == 1 ? "stat.status.oneColumn" : "stat.status");
        return _statusFmt.Get(_loc.T(key), n, _columns);
    }

    // STATS caption hint: how-to while browsing, the match count while searching, nothing while loading.
    private string StatsHint()
    {
        if (!_classificationBuilt) return string.Empty;
        return _searching
            ? _matchesFmt.Get(_loc.T(_matchCount == 1 ? "stat.section.stats.matches.one" : "stat.section.stats.matches"),
                _matchCount)
            : _loc.T("stat.section.stats.hint");
    }

    private void SetSearch(string s)
    {
        s ??= string.Empty;
        if (s == _search) return;
        _search = s;
        _scrollReset = true;
        MarkSnapshotDirty();
    }

    private bool ConsumeScrollReset()
    {
        if (!_scrollReset) return false;
        _scrollReset = false;
        return true;
    }

    private const int AttrIdSettingsScanMax = 14000;
    private const string OtherGroup = "Other";

    // Display order — Offensive first, Other last, unknown groups alphabetical between.
    private static readonly string[] DefaultGroupOrder =
    {
        "Offensive", "Defensive", "Support", "Elemental Attack", "Elemental Bonus",
    };
}
