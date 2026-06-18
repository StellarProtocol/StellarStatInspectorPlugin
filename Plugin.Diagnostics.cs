using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.StatInspector;

/// <summary>
/// Diagnostic-mode logging for <see cref="Plugin"/>. All entry points short-circuit
/// on <see cref="StellarDiagnostics.IsEnabled"/> so production partials can call
/// them unconditionally — keeps the production code clean of inline gates
/// (per coding-standards § Diagnostics; same pattern as
/// <c>FileConfigStore.Diagnostics.cs</c>).
/// </summary>
public sealed partial class Plugin
{
    private void LogStarterName(int attrId, string? name)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[StatInspector] starter-list verify: #{attrId} -> {(name is null ? "<unknown>" : "\"" + name + "\"")}");
    }

    private void LogFormatDetect(int attrId, string name, string kind)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[StatInspector] format detect: attr {attrId} \"{name}\" -> {kind}");
    }

    private void LogPercentScale(int attrId, long sample, string resolved)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[StatInspector] percent scale resolve: attr {attrId} raw={sample} -> {resolved}");
    }

    private void LogOrphanAttr(int attrId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (!_loggedOrphans.Add(attrId)) return;
        _services.Log.Info($"[StatInspector] orphan attr in selected: #{attrId}");
    }

    private void LogSubscribed(IReadOnlyCollection<int> ids)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[StatInspector] subscribed: count={ids.Count}");
    }

    private void LogReconciliation(int added, int removed)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[StatInspector] reconciliation: +{added} -{removed}");
    }
}
