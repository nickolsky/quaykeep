using Quaykeep.Core.Models;
using Quaykeep.Core.Storage;

namespace Quaykeep.Core.Scripts;

/// <summary>
/// Stores a finished script run on its server: the run history, the results as server attributes and the ports to
/// monitor. Shared by the run window and AI agents (MCP).
/// </summary>
public static class ScriptRunRecorder
{
    public const int KeepRuns = 30;

    /// <summary>Only the parameters worth keeping in the history: visible ones, never secrets.</summary>
    public static Dictionary<string, string> HistoryParams(ScriptManifest manifest, IReadOnlyDictionary<string, string> values) =>
        manifest.Params.Where(p => p.IsVisible(values) && p.Type != ScriptParamType.Secret && values.ContainsKey(p.Name))
            .ToDictionary(p => p.Name, p => values[p.Name].Trim());

    /// <summary>
    /// Completes <paramref name="run"/> (value="…${PARAM}…" results of a successful run) and saves it.
    /// Returns true when ports were put on monitoring, so the caller can check the server now.
    /// </summary>
    public static bool Record(VaultService vault, ServerEntry server, ScriptEntry script, ScriptManifest manifest, ScriptRun run,
        IReadOnlyDictionary<string, string> values, ScriptEntry? installer = null)
    {
        if (run.ExitCode == 0)
        {
            var vars = new Dictionary<string, string>(values) { ["SSHM_HOST"] = server.Host, ["SSHM_SERVER_NAME"] = server.Name };
            foreach (var (key, value) in manifest.TemplateResults(vars)) run.Results.TryAdd(key, value);
        }
        var addedPorts = false;
        vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == server.Id);
            if (s == null) return;
            if (run.IsUninstall && run.ExitCode == 0 && installer != null)
                RemoveInstallEntries(s, installer, manifest, values);
            s.ScriptRuns.Add(run);
            if (s.ScriptRuns.Count > KeepRuns) s.ScriptRuns.RemoveRange(0, s.ScriptRuns.Count - KeepRuns);
            foreach (var (key, value) in run.Results)
            {
                var def = manifest.Result(key);
                var attr = s.Attributes.FirstOrDefault(a => a.Key == key);
                if (attr == null) s.Attributes.Add(attr = new ServerAttribute { Key = key });
                attr.Label = def?.Label ?? key;
                attr.Value = value;
                attr.Source = script.Name;
                attr.SourceScriptId = run.IsUninstall ? null : script.Id;
                attr.SourceRunId = run.IsUninstall ? null : run.Id;
                attr.Updated = DateTime.Now;
                if (def?.MonitorName is { } monitor && int.TryParse(value, out var port) && port is > 0 and < 65536 &&
                    s.MonitoredPorts.All(p => p.Port != port))
                {
                    s.MonitoredPorts.Add(new MonitoredPort { Port = port, Name = monitor });
                    addedPorts = true;
                }
            }
            if (run.ExitCode == 0)
                s.Attributes.RemoveAll(a => a.Source == script.Name && manifest.IsStale(a.Key, run.Results));
        });
        return addedPorts;
    }

    internal static void RemoveInstallEntries(ServerEntry server, ScriptEntry installer, ScriptManifest uninstall,
        IReadOnlyDictionary<string, string> values)
    {
        var install = ScriptManifest.Parse(installer.Body);
        // An account or instance parameter limits cleanup to that installation.
        var scope = uninstall.Params.Where(p => p.Type != ScriptParamType.Secret &&
            install.Params.Any(i => i.Name == p.Name && i.Type != ScriptParamType.Secret) && values.ContainsKey(p.Name))
            .Select(p => p.Name).ToArray();
        var runs = server.ScriptRuns.Where(r => !r.IsUninstall && r.ScriptId == installer.Id).ToList();
        var removed = runs.Where(r => scope.All(key => r.Params.TryGetValue(key, out var value) && value == values[key]))
            .Select(r => r.Id).ToHashSet();
        server.Attributes.RemoveAll(a =>
        {
            if (a.SourceScriptId is { } source && source != installer.Id) return false;
            if (a.SourceRunId is { } id)
                return removed.Contains(id) || (scope.Length == 0 &&
                    install.Result(a.Key) != null);

            // Earlier vaults stored only the script name. Use the most recent matching result where available.
            var latest = server.ScriptRuns.LastOrDefault(r => !r.IsUninstall && r.ScriptName == a.Source &&
                r.Results.TryGetValue(a.Key, out var value) && value == a.Value);
            return latest != null ? removed.Contains(latest.Id) :
                scope.Length == 0 && a.Source == installer.Name && install.Result(a.Key) != null;
        });
        server.ScriptRuns.RemoveAll(r => removed.Contains(r.Id));
    }
}
