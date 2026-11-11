using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.StatInspector;

/// <summary>
/// Live player-attribute inspector with a customisable mini-HUD. The Settings window
/// presents a grouped, searchable picker over all game attributes from
/// <c>Bokura.AttrDescriptionBase</c>; the mini-HUD shows the user-selected stats with
/// live values sourced from <see cref="IPlayerStats"/> and delta-flash highlighting on
/// change. Demonstrates persisted user selection via <see cref="IPluginConfig"/> and
/// a searchable multi-select list inside a uGUI window.
///
/// Hotkey: <b>F6</b> toggles mini-HUD visibility (user can rebind in Settings).
/// </summary>
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "StatInspector";

    // Combat-delta highlight: a stat that changed flashes green (▲) / red (▼)
    // then fades to neutral over this window.
    private const float DeltaFadeSeconds = 2.0f;

    private const float ResetPromptDurS    = 4.0f;   // the Settings "Clear all" confirm times out after this

    // Starter-list IDs.
    private static readonly int[] StarterAttrIds = { 11011, 11021, 11321, 11331, 11341, 12511 };

    // Slug map for group-collapse keys.
    private static readonly Dictionary<string, string> GroupSlugMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "Offensive",         "offensive"  },
            { "Defensive",         "defensive"  },
            { "Support",           "support"    },
            { "Elemental Attack",  "elemattack" },
            { "Elemental Bonus",   "elembonus"  },
            { "Other",             "other"      },
        };

    // ---- Services + config -----------------------------------------------------------
    private readonly IPluginServices _services;
    private readonly ILocalization _loc;
    private readonly IConfigSection _statSection;
    private readonly IConfigSection _windowSection;

    private IWindowControl _miniHudWindow;
    private IWindowControl _settingsWindow;
    private readonly IHotkeyAction _toggleAction;

    // ---- Selection state (mirrors the IPlayerStats subscription set) -----------------
    // _hudOrder = the stats on the HUD IN HUD ORDER (persisted as-is, user reorders it by drag); _selected = the same ids
    // as a set for the O(1) Contains every picker row polls. Mutated together only through Plugin.Selection.cs.
    private readonly HashSet<int> _selected = new();
    private readonly List<int> _hudOrder = new();

    // ---- Mini-HUD grid columns (1..4, user-configurable in settings) -----------------
    private int _columns = 2;

    // ---- Display-only "peek" subscriptions: while the settings window is open we
    // subscribe the rows currently on screen so EVERY visible stat shows its live
    // value (not just ticked ones). Dropped when a row scrolls/collapses away or the
    // window closes. Kept disjoint from _selected so we never unsubscribe a picked stat.
    private readonly HashSet<int> _peek = new();
    private readonly HashSet<int> _drawnThisFrame = new();

    // ---- Bundled stat-icon atlas + combat-delta tracking -----------------------------
    private readonly StatIconAtlas _iconAtlas = new();
    // attrId -> atlas cell, cached so IndexFor's ToLowerInvariant runs once per stat
    // (not per visible row per frame). Only cached once the name resolves.
    private readonly Dictionary<int, int> _iconIndexOf = new();
    private readonly Dictionary<int, long> _lastValue = new();        // attrId -> last sampled value
    private readonly Dictionary<int, StatChange> _changes = new();    // attrId -> last change + when
    private readonly struct StatChange
    {
        public readonly long Delta;
        public readonly float At;
        public StatChange(long delta, float at) { Delta = delta; At = at; }
    }

    // ---- Group classification (attrId -> group display name); built once -------------
    private readonly Dictionary<int, string> _groupOf = new();
    private readonly Dictionary<string, List<int>> _groupMembers = new(StringComparer.Ordinal);
    private readonly List<string> _orderedGroups = new();
    private readonly Dictionary<string, bool> _groupCollapsed = new(StringComparer.Ordinal);
    private readonly HashSet<int> _ambiguousIds = new();
    private readonly Dictionary<int, string> _disambiguationLabel = new();
    private bool _classificationBuilt;

    // ---- Settings-window visibility persistence ---------------------------------------
    // The framework persists window visibility only on drag, and the GlassMenu chrome ✕
    // just calls Hide() with no save. So we mirror the window's visibility into our own
    // settings_visible key whenever it changes — covers the chrome ✕ closing the panel.
    private bool _lastSettingsVisible;

    // ---- Orphan-attr one-shot log set (drops repeated diag lines) --------------------
    private readonly HashSet<int> _loggedOrphans = new();

    public Plugin(IPluginServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _loc = services.Localization;

        _statSection = _services.Config.GetSection("statinspector");
        _windowSection = _services.Config.GetSection("window");

        _services.Log.Info("[StatInspector] plugin constructed");

        LoadConfig();
        VerifyStarterNames();
        InitializeSubscriptions();
        BuildFormatCache();
        BuildGroupClassification();

        var settingsVisible = _windowSection.Get<bool>("settings_visible", false);
        _lastSettingsVisible = settingsVisible;

        // uGUI window toolkit (Phase E — both surfaces migrated off IMGUI). The trees are built once;
        // their Funcs re-pull live state on the framework's capped refresh.
        _tipWindow      = BuildAndRegisterTip();         // Plugin.Settings.Help.cs — before Settings (its "?" reads it)
        _settingsWindow = BuildAndRegisterSettings();   // Plugin.Settings.cs
        _miniHudWindow  = BuildAndRegisterMiniHud();     // Plugin.MiniHud.cs

        _toggleAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction(
                Id:              "statinspector.toggle",
                Description:     _loc.T("stat.hotkey.toggle"),
                SuggestedDefault: new KeyBinding(StellarKeyCode.F6)),
            callback: ToggleMiniHudAndPersist);

        _services.Framework.Update += OnUpdate;
        _services.Config.SectionChanged += OnConfigChanged;
    }

    public void Dispose()
    {
        _services.Config.SectionChanged -= OnConfigChanged;
        _services.Framework.Update -= OnUpdate;
        FlushHudBgSave();   // a background-slider drag released just before unload still persists
        FlushOrderSave();   // … and so does a HUD reorder (Plugin.Selection.cs)

        _toggleAction.Dispose();
        _settingsWindow.Remove();
        _tipWindow.Remove();
        _miniHudWindow.Remove();

        // Unsubscribe but DO NOT clear persisted selection.
        foreach (var id in _selected.ToArray())
        {
            try { _services.PlayerStats.Unsubscribe(id); } catch { /* swallow */ }
        }
        try { ClearPeek(); } catch { /* swallow */ }
    }

    private void ToggleMiniHudAndPersist()
    {
        // Persist through the framework's layout slot (the single source of truth the framework reapplies on
        // launch — framework 1.4.0). The old private "minihud_visible" key desynced from the slot, so the slot's
        // stale value won on relaunch and the mini-HUD came back hidden even after F6 turned it on.
        _miniHudWindow.SetVisiblePersist(!_miniHudWindow.IsShown);
        _services.Log.Info($"[StatInspector] mini-HUD {(_miniHudWindow.IsShown ? "shown" : "hidden")}");
    }

    private void OnUpdate(float deltaTime)
    {
        // Retry classification when GameData becomes available.
        if (!_classificationBuilt && _services.GameData.IsAvailable)
        {
            BuildGroupClassification();
            MarkSnapshotDirty();   // "Loading…" placeholder → real grouped rows
        }
        TrackStatChanges();
        RefreshMiniSnapshot();
        // Flattened picker rows: dirty-flag gated (a no-op almost every frame), kept current while the Settings window
        // is hidden too so its framework pre-build fill (WindowSpec.Prebuild) lays out real rows, not an empty list.
        RebuildSnapshotIfDirty();
        // Settings picker upkeep — only while the window is shown (peek subscriptions must not run while hidden).
        if (_settingsWindow.IsShown)
        {
            RefreshWindowPeek();
            AdvanceClearConfirm(_services.Framework.TimeNow);   // Plugin.Settings.Feedback.cs
        }
        SettingsCentreTick();   // Plugin.Settings.Open.cs — the open-centred burst (a no-op once it ends)
        TipRepositionTick();    // Plugin.Settings.Help.cs — anchors the help popover under its "?" on open
        HudBgSaveTick();        // Plugin.Settings.Background.cs — debounced config write after a slider drag
        OrderSaveTick();        // Plugin.Selection.cs — debounced config write after a HUD reorder
        SyncSettingsVisibility();
    }

    // Persist settings-window visibility when it changes (the chrome ✕ → Hide() does not).
    private void SyncSettingsVisibility()
    {
        var vis = _settingsWindow.IsShown;
        if (vis == _lastSettingsVisible) return;
        _lastSettingsVisible = vis;
        if (!vis)
        {
            ClearPeek();             // window closed — stop sampling display-only rows
            CloseTip();              // its help popover goes with it
            _clearConfirmAt = -1f;   // and a pending Clear-all confirm is dropped
        }
        _windowSection.Set("settings_visible", vis);
        _windowSection.Save();
    }

    // Per-frame diff of each selected stat against its last sampled value. A
    // change records the delta + timestamp so the HUD can flash green/red and
    // fade. First sight (no prior value) seeds without flashing.
    private void TrackStatChanges()
    {
        if (!_services.PlayerStats.IsAvailable) return;
        var now = _services.Framework.TimeNow;
        foreach (var id in _selected)
        {
            var cur = _services.PlayerStats.TryGetAttribute(id);
            if (!cur.HasValue) continue;
            if (_lastValue.TryGetValue(id, out var prev) && prev != cur.Value)
                _changes[id] = new StatChange(cur.Value - prev, now);
            _lastValue[id] = cur.Value;
        }
    }

    private void LoadConfig()
    {
        // Selection, in the stored order (= the HUD order). Empty/absent → use starter list as the in-memory default.
        // Existing configs were saved id-sorted, so nothing moves until the user reorders.
        var saved = _statSection.Get<int[]>("selected", null) ?? Array.Empty<int>();
        SetSelectionFrom(saved.Length == 0 ? StarterAttrIds : saved);   // Plugin.Selection.cs — drops dupes / invalid

        _columns = Math.Clamp(_windowSection.Get<int>("minihud_columns", 2), 1, 4);
        LoadHudBgOpacity();   // Plugin.Settings.Background.cs — mini-HUD background, default 0 (none)

        // Group collapse defaults: Offensive + Defensive expanded; rest collapsed.
        _groupCollapsed["offensive"]  = _windowSection.Get<bool>("group_offensive_collapsed",  false);
        _groupCollapsed["defensive"]  = _windowSection.Get<bool>("group_defensive_collapsed",  false);
        _groupCollapsed["support"]    = _windowSection.Get<bool>("group_support_collapsed",    true);
        _groupCollapsed["elemattack"] = _windowSection.Get<bool>("group_elemattack_collapsed", true);
        _groupCollapsed["elembonus"]  = _windowSection.Get<bool>("group_elembonus_collapsed",  true);
        _groupCollapsed["other"]      = _windowSection.Get<bool>("group_other_collapsed",      true);
    }

    private void InitializeSubscriptions()
    {
        foreach (var id in _selected)
        {
            try { _services.PlayerStats.Subscribe(id); }
            catch (Exception ex) { _services.Log.Warning($"[StatInspector] Subscribe({id}) failed: {ex.Message}"); }
        }
        LogSubscribed(_selected);
    }

    private void VerifyStarterNames()
    {
        foreach (var id in StarterAttrIds)
        {
            try
            {
                var name = _services.GameData.IsAvailable ? ResolveAttrName(id) : null;
                LogStarterName(id, name);
            }
            catch
            {
                // never fail startup on a diagnostic check.
            }
        }
    }

    private void SetColumns(int n)
    {
        n = Math.Clamp(n, 1, 4);
        if (_columns == n) return;
        _columns = n;
        _windowSection.Set("minihud_columns", n);
        _windowSection.Save();
        RebuildMiniHud();
    }

    // The mini-HUD grid's column count is fixed when its element tree is built, so a column change
    // tears the window down and re-registers it (preserving the on-screen rect). Rare (a settings click).
    private void RebuildMiniHud()
    {
        var rect = _miniHudWindow.Rect;
        var wasShown = _miniHudWindow.IsShown;
        _miniHudWindow.Remove();
        _miniHudWindow = BuildAndRegisterMiniHud();
        if (rect.Width > 0f) _miniHudWindow.SetRect(rect);
        _miniHudWindow.SetVisible(wasShown);
    }

    // Ensure a visible picker row is sampled so it can show a live value, even when
    // not ticked. Idempotent; skips rows already subscribed via _selected.
    private void EnsurePeekSubscribed(int attrId)
    {
        if (_selected.Contains(attrId)) return;
        if (_peek.Add(attrId)) _services.PlayerStats.Subscribe(attrId);
    }

    // Drop peek subscriptions for rows not drawn this frame (collapsed / scrolled away).
    private void ReconcilePeek()
    {
        if (_peek.Count == 0) return;
        _peekScratch.Clear();
        foreach (var id in _peek) if (!_drawnThisFrame.Contains(id)) _peekScratch.Add(id);
        foreach (var id in _peekScratch)
        {
            if (!_selected.Contains(id)) _services.PlayerStats.Unsubscribe(id);  // never drop a picked stat
            _peek.Remove(id);
        }
    }

    private readonly List<int> _peekScratch = new();

    private void ClearPeek()
    {
        if (_peek.Count == 0) return;
        foreach (var id in _peek) if (!_selected.Contains(id)) _services.PlayerStats.Unsubscribe(id);
        _peek.Clear();
    }

    /// <summary>
    /// Resolves the display name for an attribute. Prefers <c>AttributeProfile.Name</c> — the game's
    /// short attribute name read as a LOCALIZED string (Japanese on a JP client, e.g. 会心 / 火属性強度 /
    /// レジストダメージ軽減), the same source the game's own attribute panel uses. Falls back to
    /// <c>GetAttribute.Name</c> (the description table / English catalog, English regardless of locale)
    /// only for attrs the profile UI doesn't name — internal/derived stats that land in the "Other"
    /// group. This is why the picker showed English before: the old order preferred GetAttribute first.
    /// </summary>
    private string? ResolveAttrName(int attrId)
    {
        var profileName = _services.GameData.Combat.GetAttributeProfile(attrId)?.Name;
        if (!string.IsNullOrEmpty(profileName)) return profileName;
        return _services.GameData.Combat.GetAttribute(attrId)?.Name;
    }

    /// <summary>
    /// Atlas cell for an attribute, cached per attrId. Keys on the numeric <paramref name="attrId"/>
    /// first (<see cref="StatIconAtlas.IndexForAttr"/>) so JP/any-locale clients get the right icon —
    /// the localized display name only matches the keyword table on English clients (会心/幸運/火属性
    /// 強度/… contain no ASCII keyword → 📊 placeholder). The attrId is stable across locales AND
    /// framework versions (NO dependency on the framework catalog / EnumName, absent from the shipped
    /// 2.1.0 framework). Falls back to the name-based <see cref="StatIconAtlas.IndexFor"/> for anything
    /// unmapped. The empty→0 placeholder is NOT cached so the real icon is picked up once GameData
    /// resolves; both surfaces (Settings picker + mini-HUD) funnel through here.
    /// </summary>
    private int IconIndexFor(int attrId)
    {
        if (_iconIndexOf.TryGetValue(attrId, out var idx)) return idx;

        var byAttr = StatIconAtlas.IndexForAttr(attrId);
        if (byAttr >= 0) { _iconIndexOf[attrId] = byAttr; return byAttr; }

        var name = ResolveAttrName(attrId);
        var i = StatIconAtlas.IndexFor(name ?? string.Empty);
        if (i != 0) _iconIndexOf[attrId] = i;   // cache only a real hit; keep retrying the placeholder
        return i;
    }
}
