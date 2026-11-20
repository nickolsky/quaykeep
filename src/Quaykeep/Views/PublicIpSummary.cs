using Avalonia.Controls;
using Avalonia.Threading;
using Quaykeep.Core.Models;
using Quaykeep.Core.Storage;

namespace Quaykeep.Views;

/// <summary>Shows the effective public addresses independently of the SSH or listener address.</summary>
public sealed class PublicIpSummary : SelectableTextBlock
{
    public static string Value(string? address, bool current) =>
        address == null ? L.Get("PublicIp.Unavailable") : current ? address : L.F("PublicIp.LastKnown", address);

    public static string Format(ServerEntry server, bool includeName = false)
    {
        var observation = server.AutoDetectPublicIps ? server.Facts?.PublicIps : null;
        var text = L.F("PublicIp.Summary",
            Value(server.PublicIpv4, observation?.Ipv4Current != false),
            Value(server.PublicIpv6, observation?.Ipv6Current != false));
        return includeName ? $"{server.Name}  ·  {text}" : text;
    }

    public void Track(Window owner, VaultService vault, IReadOnlySet<Guid> ids, bool includeNames = false)
    {
        IsVisible = ids.Count > 0;
        if (ids.Count == 0) return;
        void Refresh()
        {
            if (vault.TryRead(d => d.Servers.Where(s => ids.Contains(s.Id))
                .Select(s => Format(s, includeNames)).ToList(), out var lines))
                Text = string.Join("\n", lines);
        }

        void RefreshIfOpen()
        {
            if (owner.IsVisible) Refresh();
        }

        void OnFactsChanged(object? sender, Guid id)
        {
            if (ids.Contains(id)) Dispatcher.UIThread.Post(RefreshIfOpen);
        }

        void OnDataChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RefreshIfOpen);

        Refresh();
        vault.FactsChanged += OnFactsChanged;
        vault.DataChanged += OnDataChanged;
        owner.Closed += (_, _) =>
        {
            vault.FactsChanged -= OnFactsChanged;
            vault.DataChanged -= OnDataChanged;
        };
    }
}
