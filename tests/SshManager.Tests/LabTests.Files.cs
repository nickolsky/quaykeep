using Renci.SshNet.Common;
using SshManager.Core.Crypto;
using SshManager.Core.Localization;
using SshManager.Core.Models;
using SshManager.Core.Ssh;

namespace SshManager.Tests;

/// <summary>File transfer scripts (SFTP user, vsftpd) and "turn off password login" of key setup, on the lab servers.</summary>
public partial class LabTests
{
    [Fact]
    public async Task Sftp_User_Is_Jailed_And_Can_Switch_To_Key_Only()
    {
        if (!Enabled("sftp")) return;
        await OnEachServer(async lab =>
        {
            Sh(lab, "userdel labsftp 2>/dev/null; rm -rf /srv/sftp; sed -i '/^# BEGIN sshm-sftp /,/^# END sshm-sftp /d' /etc/ssh/sshd_config; true", check: false);
            var r = await Run(lab, "sftp-user", new() { ["SFTP_USER"] = "labsftp" });
            var password = r["SFTP_PASSWORD"];
            Assert.StartsWith("sftp://labsftp@", r["SFTP_URL"]);
            var user = new ServerEntry { Name = lab.Server.Name + "-sftp", Host = lab.Server.Host, Port = lab.Server.Port, Username = "labsftp", Password = password };

            using (var sftp = lab.Ssh.ConnectSftp(user))
            {
                Assert.Contains(sftp.ListDirectory("/"), f => f.Name == "files");
                using (var ms = new MemoryStream("hello"u8.ToArray())) sftp.UploadFile(ms, "/files/hello.txt");
                using (var ms = new MemoryStream("x"u8.ToArray()))
                    Assert.ThrowsAny<SshException>(() => sftp.UploadFile(ms, "/escape.txt")); // the chroot root is root's
                Assert.DoesNotContain(sftp.ListDirectory("/"), f => f.Name is "etc" or "root"); // nothing outside the chroot
            }
            Assert.Equal("hello", Sh(lab, "cat /srv/sftp/labsftp/files/hello.txt").Trim());
            Assert.Contains("forcecommand internal-sftp", Sh(lab, "sshd -T -C user=labsftp,host=x,addr=127.0.0.1").ToLowerInvariant());

            // key only: the password stops working, the key works
            var key = KeyService.Generate("labsftp", "labsftp@lab", KeyAlgorithm.Ed25519);
            lab.Vault.Update(d => d.Keys.Add(key));
            var r2 = await Run(lab, "sftp-user", new() { ["SFTP_USER"] = "labsftp", ["SFTP_ALLOW_PASSWORD"] = "0", ["SFTP_PUBKEY"] = key.PublicKey.Trim() }, "-key");
            Assert.False(r2.ContainsKey("SFTP_PASSWORD"));
            Assert.ThrowsAny<SshAuthenticationException>(() => lab.Ssh.ConnectSftp(user).Dispose());
            var byKey = user.Clone();
            byKey.Auth = AuthMode.Key;
            byKey.KeyId = key.Id;
            using (var sftp = lab.Ssh.ConnectSftp(byKey))
                Assert.Contains(sftp.ListDirectory("/files"), f => f.Name == "hello.txt");
        });
    }

    [Fact]
    public async Task Vsftpd_Ftps_Login_Upload_And_Password_Change()
    {
        if (!Enabled("vsftpd")) return;
        await OnEachServer(async lab =>
        {
            Sh(lab, "systemctl stop vsftpd 2>/dev/null; userdel labftp 2>/dev/null; rm -rf /srv/ftp /etc/vsftpd.sshm-users; true", check: false);
            var r = await Run(lab, "vsftpd", new() { ["FTP_USER"] = "labftp" });
            var password = r["FTP_PASSWORD"];
            Assert.Equal("21", r["FTP_PORT"]);
            string Ftp(string pw, string args) => Sh(lab,
                $"curl -s -o /dev/null -w '%{{http_code}}' --max-time 20 --ftp-skip-pasv-ip --user 'labftp:{pw}' {args}", check: false).Trim();

            Assert.Equal("226", Ftp(password, "--ssl-reqd -k -T /etc/hostname ftp://127.0.0.1/files/up.txt"));
            Assert.Equal(Sh(lab, "cat /etc/hostname"), Sh(lab, "cat /srv/ftp/labftp/files/up.txt"));
            Assert.NotEqual("226", Ftp(password, "ftp://127.0.0.1/files/")); // TLS is required: plain FTP is refused
            Assert.NotEqual("226", Ftp(password, "--ssl-reqd -k -T /etc/hostname ftp://127.0.0.1/escape.txt")); // the chroot is read-only

            var r2 = await Run(lab, "vsftpd", new() { ["FTP_USER"] = "labftp", ["FTP_PASSWORD"] = "lab-ftp-password-2026" }, "-rerun");
            Assert.False(r2.ContainsKey("FTP_PASSWORD"));
            Assert.Equal("226", Ftp("lab-ftp-password-2026", "--ssl-reqd -k ftp://127.0.0.1/files/"));
            Assert.NotEqual("226", Ftp(password, "--ssl-reqd -k ftp://127.0.0.1/files/"));
        });
    }

    [Fact]
    public async Task KeySetup_Turns_Off_Password_Login()
    {
        if (!Enabled("keysetup")) return;
        await OnEachServer(lab =>
        {
            var server = lab.Server.Clone();
            var lines = new List<string>();
            var setup = new KeySetupService(lab.Vault, lab.Ssh);
            var key = setup.SetupKeyAuth(server, null, lines.Add, disablePassword: true);
            try
            {
                var text = string.Join('\n', lines);
                Assert.True(text.Contains(L.Get("KeySetup.PasswordOff")) && text.Contains(L.Get("KeySetup.PasswordRefused")), text);
                using var c = lab.Ssh.Connect(server, key);
                Assert.Contains("no", RemoteShell.Run(c, server, "sshd -T | grep -i '^passwordauthentication'", elevated: true).Output);
            }
            finally
            {
                Restore();
            }

            // RHEL 8 and older have no Include of sshd_config.d: the setting goes into a block at the top of sshd_config
            string KeySh(string command)
            {
                using var c = Connect(key);
                var r = RemoteShell.Run(c, server, command, elevated: true);
                Assert.True(r.Ok, $"`{command}`: {r.Combined}");
                return r.Output;
            }
            KeySh(@"sed -i 's|^Include /etc/ssh/sshd_config.d/\*\.conf|#sshm-lab Include /etc/ssh/sshd_config.d/*.conf|' /etc/ssh/sshd_config");
            try
            {
                lines.Clear();
                Assert.True(setup.DisablePasswordLogin(server, key, lines.Add), string.Join('\n', lines));
                Assert.StartsWith("# BEGIN sshm: password login off", KeySh("head -1 /etc/ssh/sshd_config"));
                Assert.Contains("SSHM_NO_DROPIN", KeySh("[ -e /etc/ssh/sshd_config.d/00-sshm-no-password.conf ] || echo SSHM_NO_DROPIN"));
            }
            finally
            {
                Restore();
                KeySh("sed -i 's|^#sshm-lab Include|Include|' /etc/ssh/sshd_config && sshd -t");
            }
            Assert.DoesNotContain("sshm", KeySh("head -3 /etc/ssh/sshd_config"));

            // the lab keeps logging in with the password
            void Restore()
            {
                using var c = Connect(key);
                var back = RemoteShell.Run(c, server, KeySetupService.RestorePasswordScript, elevated: true);
                Assert.True(back.Ok, back.Combined);
            }

            // sshd may still be re-executing after a reload
            Renci.SshNet.SshClient Connect(KeyEntry? with)
            {
                for (var i = 1; ; i++)
                {
                    try
                    {
                        return lab.Ssh.Connect(server, with);
                    }
                    catch (Exception ex) when (i < 8 && ex is not SshAuthenticationException)
                    {
                        Thread.Sleep(1000);
                    }
                }
            }

            Connect(null).Dispose(); // password login is back
            return Task.CompletedTask;
        });
    }
}
