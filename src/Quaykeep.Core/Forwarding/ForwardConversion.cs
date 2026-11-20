using Quaykeep.Core.Models;

namespace Quaykeep.Core.Forwarding;

/// <summary>Stages the new backend before removing the old forward and cleans up on failure.</summary>
internal static class ForwardConversion
{
    public static string Script(PortForward old, IReadOnlyList<PortForward> next)
    {
        if (next.Count == 0) throw new ArgumentException(L.Get("Fwd.BadProtocol"));
        string add, removeOld, discard;
        if (old.IsRelay)
        {
            var rules = next.Select(f => (Id: IptablesCommands.NewId(), Forward: f)).ToArray();
            add = string.Join("\n", rules.Select(r => IptablesCommands.AddBody(r.Id, r.Forward.Protocol,
                r.Forward.ListenPort, r.Forward.TargetIp, r.Forward.TargetPort)));
            discard = string.Join("\n", rules.Select(r => $$"""
                (
                  set -e
                {{IptablesCommands.RemoveById(r.Id)}}
                )
                [ "$?" -eq 0 ] || failed=1
                """));
            removeOld = RelayCommands.Change(old, []);
        }
        else
        {
            foreach (var relay in next) relay.RelayId = Guid.NewGuid().ToString("N");
            add = RelayCommands.Change(null, next);
            discard = string.Join("\n", next.Select(relay => $$"""
                if [ -f '/etc/quaykeep/relays/{{relay.RelayId}}.conf' ]; then
                  (
                    set -e
                {{RelayCommands.Change(relay, [])}}
                  )
                  [ "$?" -eq 0 ] || failed=1
                fi
                """));
            removeOld = IptablesCommands.RemoveForConversion(old);
        }
        return Switch(add, removeOld, discard);
    }

    internal static string Switch(string add, string removeOld, string discard) => $$"""
        set -e
        exec 8>/run/quaykeep-forward-conversion.lock
        flock -n 8 || { echo 'Another forward conversion is in progress.' >&2; exit 1; }
        pending=1
        rollback() {
          status=$?
          trap - EXIT HUP INT TERM
          if [ "$pending" -eq 1 ]; then
            set +e
            (
              failed=0
        {{discard}}
              [ "$failed" -eq 0 ]
            ) >/dev/null
            if [ "$?" -ne 0 ]; then
              echo 'Could not fully remove the replacement forward.' >&2
            fi
          fi
          exit "$status"
        }
        trap rollback EXIT
        trap 'exit 1' HUP INT TERM
        (
        {{add}}
        ) >/dev/null
        (
        {{removeOld}}
        ) >/dev/null
        pending=0
        echo {{IptablesCommands.OkMarker}}
        """ + "\n" + IptablesCommands.Persist;
}
