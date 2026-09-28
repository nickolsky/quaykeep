using SshManager.Core.Crypto;
using SshManager.Core.Models;
using SshManager.Core.Storage;

namespace SshManager.Core.Ssh;

/// <summary>Uses SSH.NET for non-interactive operations: connection test and "password → key" setup.</summary>
public sealed class KeySetupService(VaultService vault, SshClientFactory ssh)
{
    public string Test(ServerEntry server)
    {
        using var client = ssh.Connect(server);
        var cmd = client.RunCommand("uname -a 2>/dev/null || ver");
        return (cmd.Result + cmd.Error).Trim();
    }

    /// <summary>
    /// Logs in with the stored password, installs a key into ~/.ssh/authorized_keys, verifies key login
    /// and switches the server to key authentication. With <paramref name="disablePassword"/>, then turns
    /// password login off in sshd (see <see cref="DisablePasswordLogin"/>).
    /// </summary>
    public KeyEntry SetupKeyAuth(ServerEntry server, Guid? existingKeyId, Action<string> log, bool disablePassword = false)
    {
        if (string.IsNullOrEmpty(server.Password))
            throw new InvalidOperationException(L.Get("KeySetup.NeedPassword"));

        log(L.F("KeySetup.Connecting", server.Display));
        using var client = ssh.Connect(server, key: null);
        log(L.Get("KeySetup.PasswordOk"));

        KeyEntry key;
        bool isNew;
        if (existingKeyId is { } id)
        {
            key = ssh.FindKey(id) ?? throw new InvalidOperationException(L.Get("KeySetup.KeyNotFound"));
            isNew = false;
            log(L.F("KeySetup.UsingExisting", key.Name, key.Fingerprint));
        }
        else
        {
            var comment = KeyService.SanitizeComment($"{server.Username}@{server.Name}-{DateTime.Now:yyyyMMdd}");
            key = KeyService.Generate($"{server.Username}@{server.Name}", comment, KeyAlgorithm.Ed25519);
            isNew = true;
            log(L.F("KeySetup.Generated", key.Fingerprint));
        }

        var pub = key.PublicKey.Trim();
        if (pub.Contains('\'')) throw new InvalidOperationException(L.Get("KeySetup.BadChar"));
        var script =
            "umask 077; mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && " +
            "chmod 600 ~/.ssh/authorized_keys && " +
            $"(grep -qxF '{pub}' ~/.ssh/authorized_keys || echo '{pub}' >> ~/.ssh/authorized_keys) && echo SSHM_OK";
        log(L.Get("KeySetup.Adding"));
        var cmd = client.RunCommand(script);
        if (!cmd.Result.Contains("SSHM_OK"))
            throw new InvalidOperationException(L.Get("KeySetup.WriteFailed") + " " + (cmd.Error + cmd.Result).Trim());

        if (isNew) vault.Update(d => d.Keys.Add(key));
        KeyService.EnsurePublicKeyFile(key);
        log(L.Get("KeySetup.Installed"));

        using (var check = ssh.Connect(server, key))
        {
            var r = check.RunCommand("echo SSHM_KEY_OK");
            if (!r.Result.Contains("SSHM_KEY_OK"))
                throw new InvalidOperationException(L.Get("KeySetup.CheckFailed"));
        }
        log(L.Get("KeySetup.KeyWorks"));

        vault.Update(d =>
        {
            var s = d.Servers.First(x => x.Id == server.Id);
            s.Auth = AuthMode.Key;
            s.KeyId = key.Id;
        });
        log(L.Get("KeySetup.Switched"));

        if (disablePassword) DisablePasswordLogin(server, key, log);
        return key;
    }

    /// <summary>
    /// Turns password login off in sshd once key login is known to work. A key connection stays open for the
    /// whole change: sshd applies new settings to new logins only, so if a fresh key login fails afterwards,
    /// that connection puts password login back. Failures are logged, not thrown: the key setup itself has
    /// succeeded by then.
    /// </summary>
    /// <returns>true when password login is off and a new key login works.</returns>
    public bool DisablePasswordLogin(ServerEntry server, KeyEntry key, Action<string> log)
    {
        log(L.Get("KeySetup.PasswordDisabling"));
        using var admin = ssh.Connect(server, key);
        var r = RemoteShell.Run(admin, server, DisablePasswordScript, elevated: true);
        if (RemoteShell.SudoFailed(r))
        {
            log(L.Get("KeySetup.PasswordNoSudo"));
            return false;
        }
        if (!r.Ok)
        {
            log(L.F("KeySetup.PasswordNotChanged", r.Combined));
            return false;
        }
        foreach (var line in r.Output.Split('\n'))
            if (line.StartsWith("SSHM_MATCH=", StringComparison.Ordinal))
                log(L.F("KeySetup.PasswordMatch", line["SSHM_MATCH=".Length..].Trim()));

        try
        {
            using var check = ConnectAfterReload(server, key);
            if (!check.RunCommand("echo SSHM_KEY_OK").Result.Contains("SSHM_KEY_OK"))
                throw new InvalidOperationException(L.Get("KeySetup.CheckFailed"));
        }
        catch (Exception ex)
        {
            log(L.F("KeySetup.PasswordRollback", ex.Message));
            var back = RemoteShell.Run(admin, server, RestorePasswordScript, elevated: true);
            log(back.Ok ? L.Get("KeySetup.PasswordRestored") : L.F("KeySetup.PasswordRestoreFailed", back.Combined));
            return false;
        }

        try
        {
            using var _ = ConnectAfterReload(server, key: null);
            log(L.Get("KeySetup.PasswordStillAccepted"));
        }
        catch (Renci.SshNet.Common.SshAuthenticationException)
        {
            log(L.Get("KeySetup.PasswordRefused"));
        }
        catch (Exception ex)
        {
            log(L.F("KeySetup.PasswordCheckSkipped", ex.Message));
        }
        log(L.Get("KeySetup.PasswordOff"));
        return true;
    }

    /// <summary>
    /// A reload makes sshd re-exec itself, and a connection in that moment is dropped before the SSH greeting:
    /// connection-level failures are retried for a few seconds. A refused login is an answer, never retried.
    /// </summary>
    private Renci.SshNet.SshClient ConnectAfterReload(ServerEntry server, KeyEntry? key)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ssh.Connect(server, key);
            }
            catch (Exception ex) when (attempt < 8 && ex is not Renci.SshNet.Common.SshAuthenticationException and not HostKeyNotTrustedException)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    // Shared by both scripts: paths, the marked block at the top of sshd_config, the reload. POSIX sh (RemoteShell
    // runs sh). /run/sshd is created because "sshd -t" needs it and a socket-activated sshd may not have made it yet.
    private const string SshdPrologue = """
        CFG=/etc/ssh/sshd_config
        DIR=/etc/ssh/sshd_config.d
        DROP="$DIR/00-sshm-no-password.conf"
        BEGIN='# BEGIN sshm: password login off'
        END='# END sshm: password login off'
        SSHD=$(command -v sshd 2>/dev/null || echo /usr/sbin/sshd)
        [ -x "$SSHD" ] || { echo "sshd not found" >&2; exit 3; }
        [ -f "$CFG" ] || { echo "$CFG not found" >&2; exit 3; }
        [ -d /run/sshd ] || mkdir -p -m 0755 /run/sshd 2>/dev/null || true
        strip_block(){ sed -i "/^$BEGIN\$/,/^$END\$/d" "$CFG"; }
        undo(){ rm -f "$DROP"; strip_block; }
        reload_sshd(){
          if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
            for u in ssh sshd; do
              if systemctl is-active --quiet "$u.service" 2>/dev/null; then systemctl reload "$u.service"; return; fi
            done
            # socket activation with no daemon running: every new sshd reads the file anyway
            for u in ssh sshd; do systemctl is-active --quiet "$u.socket" 2>/dev/null && return 0; done
          fi
          service ssh reload 2>/dev/null || service sshd reload 2>/dev/null ||
            { [ -f /run/sshd.pid ] && kill -HUP "$(cat /run/sshd.pid)"; } ||
            { [ -f /var/run/sshd.pid ] && kill -HUP "$(cat /var/run/sshd.pid)"; }
        }

        """;

    /// <summary>
    /// Turns password login off: a drop-in that sorts first in sshd_config.d (sshd keeps the first value it reads,
    /// so it wins over 50-cloud-init.conf), or a marked block at the top of sshd_config where there is no Include
    /// or the drop-in is overridden. Checked with sshd -t and the effective sshd -T value before the reload; a custom
    /// AuthenticationMethods is left alone. Prints SSHM_MATCH= for Match blocks that still allow passwords.
    /// </summary>
    internal const string DisablePasswordScript = SshdPrologue + """
        "$SSHD" -t || { echo "sshd -t fails before any change; the configuration is left as it is" >&2; exit 4; }
        methods=$("$SSHD" -T 2>/dev/null | awk 'tolower($1) == "authenticationmethods" { print $2; exit }')
        case "${methods:-any}" in
          any) ;;
          *) echo "AuthenticationMethods is set ($methods); the configuration is left as it is" >&2; exit 5 ;;
        esac
        effective(){ "$SSHD" -T 2>/dev/null | awk 'tolower($1) == "passwordauthentication" { print tolower($2); exit }'; }
        apply(){ # dropin | main, then the keywords to set to "no"
          where="$1"; shift
          if [ "$where" = dropin ]; then
            { echo "# SSH Manager: password login turned off after key login was verified"
              for k in "$@"; do echo "$k no"; done; } > "$DROP"
            chmod 644 "$DROP"
          else
            [ -f "$CFG.sshm-bak" ] || cp -p "$CFG" "$CFG.sshm-bak"
            tmp=$(mktemp) || exit 7
            { echo "$BEGIN"; for k in "$@"; do echo "$k no"; done; echo "$END"; cat "$CFG"; } > "$tmp"
            cat "$tmp" > "$CFG"   # keeps the owner, mode and SELinux label of sshd_config
            rm -f "$tmp"
          fi
        }
        mode=main
        if [ -d "$DIR" ] && grep -Eiq '^[[:space:]]*Include[[:space:]]+(/etc/ssh/)?sshd_config\.d/\*\.conf' "$CFG"; then mode=dropin; fi
        ok=0
        for where in $mode main; do
          # KbdInteractiveAuthentication is OpenSSH 8.7+; older versions call it ChallengeResponseAuthentication
          for keys in "PasswordAuthentication KbdInteractiveAuthentication" "PasswordAuthentication ChallengeResponseAuthentication" "PasswordAuthentication"; do
            undo
            apply "$where" $keys
            if "$SSHD" -t 2>/dev/null && [ "$(effective)" = no ]; then ok=1; break 2; fi
          done
        done
        if [ "$ok" != 1 ]; then
          undo
          echo "sshd still reports passwordauthentication $(effective) after the change; nothing was changed" >&2
          exit 6
        fi
        echo "SSHM_MODE=$where"
        for f in "$CFG" "$DIR"/*.conf; do
          [ -f "$f" ] || continue
          awk -v f="$f" 'tolower($1) == "match" { m = $0 } m != "" && tolower($1) == "passwordauthentication" && tolower($2) == "yes" { print "SSHM_MATCH=" f ": " m }' "$f"
        done
        reload_sshd || { undo; echo "Could not reload sshd; nothing was changed" >&2; exit 8; }
        echo SSHM_PASSWORD_OFF
        """;

    /// <summary>Puts password login back: removes what <see cref="DisablePasswordScript"/> added and reloads sshd.</summary>
    internal const string RestorePasswordScript = SshdPrologue + """
        undo
        "$SSHD" -t || echo "WARNING: sshd -t fails" >&2
        reload_sshd
        echo SSHM_PASSWORD_ON
        """;
}
