using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.StatInspector;

/// <summary>
/// Settings window (Phase E — native uGUI). A searchable + grouped + collapsible attribute picker over the
/// ~1252 classified attributes, rendered through the framework's scroll-windowed <c>VirtualListElement</c>
/// (a fixed ~24-row pool over the flattened <see cref="_snapshot"/>; see Plugin.Settings.Snapshot.cs). Group
/// classification is built once (Plugin.Settings.Groups.cs); the picker rows + their value/peek logic live in
/// Plugin.Settings.Rows.cs. Reset-to-defaults is a small confirm state machine (kept from the IMGUI build).
/// </summary>
public sealed partial class Plugin
{
    private const float SettingsWindowWidth  = 300f;
    private const float SettingsWindowHeight = 480f;
    private const float PickerRowH           = 22f;   // uniform virtual-list row height
    private const float PickerViewportH      = 360f;  // scroll viewport height
    private const int   SettingsPoolSize     = 24;    // rendered row pool (viewport ≈ 16 rows + margin)

    private int _winFirst;   // first visible logical row index (set by the VirtualListElement OnWindow callback)
    private string _search = string.Empty;

    private IWindowControl BuildAndRegisterSettings()
    {
        var spec = new WindowSpec(
            Id:          "statinspector.settings",
            Title:       "StatInspector Settings",
            DefaultRect: new WindowRect(800f, 20f, SettingsWindowWidth, SettingsWindowHeight),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            StartVisible = _windowSection.Get<bool>("settings_visible", false),
            Draggable    = true,
            Closable     = true,
        };
        return _services.Windows.Register(new WindowRegistration(spec, BuildSettingsRoot(),
            OnClose: () => { _settingsWindow.SetVisible(false); ClearPeek(); }));
    }

    // Built ONCE: search row + HUD-columns stepper + the virtualized picker + footer (caption + reset). The
    // picker's slot Funcs index the per-refresh _snapshot at _winFirst+slot; the snapshot is rebuilt only when
    // search/collapse/classification changes (Plugin.Settings.Snapshot.cs), driven from OnUpdate while shown.
    private HudElement BuildSettingsRoot()
    {
        var search = new RowElement(new HudElement[]
        {
            new TextElement(() => "Search:", Width: 60f),
            new InputElement(() => _search, SetSearch, 180f, OnChange: SetSearch),
            new ButtonElement(() => "Clear", () => SetSearch(string.Empty), Enabled: () => !string.IsNullOrEmpty(_search)),
        }, Gap: 4f);

        var colButtons = new List<HudElement> { new TextElement(() => "HUD columns:", Width: 96f) };
        for (var n = 1; n <= 4; n++)
        {
            var c = n;
            colButtons.Add(new ButtonElement(() => c.ToString(CultureInfo.InvariantCulture), () => SetColumns(c),
                Active: () => _columns == c, Width: 30f));
        }

        var slots = new HudElement[SettingsPoolSize];
        for (var i = 0; i < SettingsPoolSize; i++) { var s = i; slots[s] = BuildPickerSlot(s); }   // Plugin.Settings.Rows.cs
        var picker = new VirtualListElement(() => _snapshot.Count, PickerRowH, slots, f => _winFirst = f, Height: PickerViewportH);

        var footer = new RowElement(new HudElement[]
        {
            new TextElement(() => FooterCaption(), () => MenuMutedRgba()),
            new SpacerElement(),
            new ButtonElement(() => ResetLabel(), () => HandleResetClick(SafeTimeNow())),
        }, Gap: 8f);

        return new ColumnElement(new HudElement[]
        {
            search,
            new RowElement(colButtons.ToArray(), Gap: 4f),
            new SeparatorElement(),
            picker,
            new SeparatorElement(),
            footer,
        });
    }

    private void SetSearch(string s)
    {
        s ??= string.Empty;
        if (s == _search) return;
        _search = s;
        MarkSnapshotDirty();
    }

    private string FooterCaption()
    {
        var trimmed = (_search ?? string.Empty).Trim();
        return trimmed.Length > 0
            ? $"Showing {_snapshot.Count} rows  •  Selected: {_selected.Count}"
            : $"Selected: {_selected.Count} stats";
    }

    private const int AttrIdSettingsScanMax = 14000;
    private const string OtherGroup = "Other";

    // Display order — Offensive first, Other last, unknown groups alphabetical between.
    private static readonly string[] DefaultGroupOrder =
    {
        "Offensive", "Defensive", "Support", "Elemental Attack", "Elemental Bonus",
    };

    private string ResetLabel() => _resetState switch
    {
        ResetState.Prompting => "Click again to confirm reset",
        ResetState.Flashing  => "✓ Reset",
        _                    => "Reset",
    };

    private void HandleResetClick(float now)
    {
        switch (_resetState)
        {
            case ResetState.Idle:
                _resetState = ResetState.Prompting;
                _resetStateChangedAt = now;
                break;
            case ResetState.Prompting:
                DoReset();
                MarkSnapshotDirty();
                _resetState = ResetState.Flashing;
                _resetStateChangedAt = now;
                break;
            case ResetState.Flashing:
                // ignore clicks during flash.
                break;
        }
    }

    private void AdvanceResetState(float now)
    {
        if (_resetState == ResetState.Prompting && now - _resetStateChangedAt > ResetPromptDurS)
            _resetState = ResetState.Idle;
        else if (_resetState == ResetState.Flashing && now - _resetStateChangedAt > ResetFlashDurS)
            _resetState = ResetState.Idle;
    }

    private static float SafeTimeNow()
    {
        try { return Time.realtimeSinceStartup; } catch { return 0f; }
    }
}
