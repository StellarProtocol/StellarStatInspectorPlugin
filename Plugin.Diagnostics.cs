using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.StatInspector;

/// <summary>
/// Developer-mode logging for <see cref="Plugin"/>. All entry points short-circuit on the framework's
/// Developer mode (<c>IFrameworkDeveloperMode.DeveloperMode</c>, fw 2.30.0) or the older <see cref="StellarDiagnostics.IsEnabled"/>
/// flag, so production partials can call them
/// unconditionally and a normal player's log carries none of these Info lines. Real failures (e.g. a Subscribe warning) are logged unconditionally at their call sites.
/// </summary>
public sealed partial class Plugin
{
    // Read live (cheap properties) so toggling Developer mode takes effect without a restart. The old diagnostics flag
    // (StellarDiagnostics.IsEnabled) still enables the lines too, so existing diagnostic setups keep working.
    private bool DevLog => StellarDiagnostics.IsEnabled || _services.Framework.DeveloperMode;

    private void LogStarterName(int attrId, string? name)
    {
        if (!DevLog) return;
        _services.Log.Info(
            $"[StatInspector] starter-list verify: #{attrId} -> {(name is null ? "<unknown>" : "\"" + name + "\"")}");
    }

    private void LogFormatDetect(int attrId, string name, string kind)
    {
        if (!DevLog) return;
        _services.Log.Info($"[StatInspector] format detect: attr {attrId} \"{name}\" -> {kind}");
    }

    private void LogPercentScale(int attrId, long sample, string resolved)
    {
        if (!DevLog) return;
        _services.Log.Info($"[StatInspector] percent scale resolve: attr {attrId} raw={sample} -> {resolved}");
    }

    private void LogOrphanAttr(int attrId)
    {
        if (!DevLog) return;
        if (!_loggedOrphans.Add(attrId)) return;
        _services.Log.Info($"[StatInspector] orphan attr in selected: #{attrId}");
    }

    private void LogSubscribed(IReadOnlyCollection<int> ids)
    {
        if (!DevLog) return;
        _services.Log.Info($"[StatInspector] subscribed: count={ids.Count}");
    }

    private void LogReconciliation(int added, int removed)
    {
        if (!DevLog) return;
        _services.Log.Info($"[StatInspector] reconciliation: +{added} -{removed}");
    }

    private void LogClassificationBuilt(int groups, int ambiguous)
    {
        if (!DevLog) return;
        _services.Log.Info($"[StatInspector] group classification built ({groups} groups, {ambiguous} ambiguous IDs labelled Base/Total)");
    }

    private void LogFormatCacheBuilt(int classified)
    {
        if (!DevLog) return;
        _services.Log.Info($"[StatInspector] format cache built ({classified} attrs classified)");
    }
}
