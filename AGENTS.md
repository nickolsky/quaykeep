# Working on Quaykeep (for AI agents)

This is a Windows WPF app (.NET 10) for managing many SSH servers: an encrypted vault, one-click sessions, a built-in SSH
agent, monitoring, install scripts and an MCP server. The user-facing docs are in [README.md](README.md) (English) and
[README.ru.md](README.ru.md) (Russian). Keep both up to date when a feature changes.

The app was called **SSH Manager** up to 1.0.1 (`SshManager.exe`, helper `sshm.exe`). Some old names are kept on purpose:
- **On servers**, everything keeps the `sshm` name, so earlier installs, forwards and user scripts keep working:
  - `SSHM_*` script variables;
  - `@@sshm:` output markers;
  - `sshm:<id>` iptables comments;
  - `/tmp/sshm-*`, `~/.cache/sshm`;
  - the `sshm` blocks in `sshd_config`.
- **Locally**, the `.sshm-old` suffix and the `Local\SshManager-<user>` single-instance lock stay.

Old installs and user setups are still handled: the `SSHMANAGER_*` variables and the old agent pipe (`AppPaths`), the old
autostart entry (`Autostart`), `sshmanager-data-*` backups (`BackupService`), and the old program files
(`UpdateService.CleanupOld`). Don't rename these.

## Layout

| Path | What is there |
|---|---|
| `src/Quaykeep.Core` | All logic, with no WPF: vault and crypto, SSH.NET connections (`Ssh/`), monitoring (`Monitoring/`), inventory, iptables forwards, install scripts (`Scripts/`, built-ins in `Scripts/Builtin/*.sh`), MCP server (`Mcp/`), RU/EN strings (`Localization/Strings.cs`). |
| `src/Quaykeep` | The WPF app: `Views/*.xaml`, `ViewModels/MainViewModel.cs` (server tree, context menu, commands), `Services/AppHost.cs` (wires the services together). |
| `src/qk` | Console helper: terminal tab, `SSH_ASKPASS`, `qk <server>`, `qk mcp`. |
| `tests/Quaykeep.Tests` | xUnit tests: unit tests, plus `LabTests*.cs` (need the script lab) and `E2ETests` (need `SSHM_E2E`). |
| `tools/script-lab` | Docker lab servers (Ubuntu 24.04, Debian 12, CentOS Stream 9; `-Extra` adds more) for testing scripts over real SSH. |

Put logic in Core so it can be unit-tested, and keep the UI layer thin. `CopyTargets`, `PingService` and
`KeySetupService` are examples of this.

## Build, test, run

```powershell
dotnet build Quaykeep.sln
dotnet test tests\Quaykeep.Tests                         # unit tests; lab tests return early without SSHM_LAB
tools\script-lab\lab.ps1 up                               # start the lab servers (Docker Desktop)
tools\script-lab\lab.ps1 test sftp,vsftpd                 # lab tests for some scripts; ids come from Enabled("…") in LabTests
.\publish-dev.ps1                                         # test build in .\app-dev (own data in data-dev, runs next to the main copy)
```

- Try UI changes in **`app-dev`** (`publish-dev.ps1`).
- Build the main **`app`** (`publish.ps1`) only when the user asks for it. Never build it over a running copy. First check
  that `app\Quaykeep.dll` can be opened exclusively. If it is locked, ask the user to exit the app from the tray.
  Never kill the user's process.
- Run `release.ps1` or create a GitHub release only when the user explicitly asks, and at most once a week. The version
  is in `Directory.Build.props`.
- Commit or push only when asked. Commit messages are in English: a single summary line listing the features, as in `git log`.

## Conventions

- **Localization**: every UI string is a key in `src/Quaykeep.Core/Localization/Strings.cs` with **both** Russian and
  English text, and the `{0}` placeholders must match. Use `{l:Tr Key}` in XAML and `L.Get("Key")` / `L.F("Key", args)` in C#.
  `LocalizationTests` checks that the keys used in code and XAML exist.
- **Code style**: match the surrounding code: file-scoped namespaces, primary constructors, `<summary>` comments that say
  *why*, terse naming. Some files use CRLF and others LF; keep each file's line endings. `*.sh` / `*.yml` must be LF
  (`.gitattributes`).
- **Server context menu**: there is one `ContextMenu` on the tree in `MainWindow.xaml`. Items show or hide through styles
  keyed on `SelectionKind` (`MiServer`, `MiContainer`, …). Dynamic submenus (Install, Copy, Monitoring, AI agent access)
  are `ObservableCollection<MenuEntry>`s that `MainViewModel.PrepareContextMenu()` fills when the menu opens. Server
  items are grouped: connect/edit/copy → install/forwards/keys/agents → ping/checks/monitoring → reboot/delete.
- **Remote commands**: use `RemoteShell.Run(client, server, script, elevated)`. It runs POSIX `sh`, not bash, and passes the
  sudo password on stdin.
  - Never put a password or key on a command line, in a file on the server, or in a log.
  - Quote with `RemoteShell.Quote`.
- **Changing sshd or other access settings on a server**:
  - Validate first (`sshd -t`, and check the effective value with `sshd -T`).
  - Keep an already-open connection as the way back.
  - Log in afresh after the reload, and roll back automatically if that fails.
  - Right after `systemctl reload ssh`, sshd re-executes itself, so retry connection-level failures (never failed logins)
    for a few seconds. See `KeySetupService.DisablePasswordLogin`.

## Built-in install scripts (`src/Quaykeep.Core/Scripts/Builtin/*.sh`)

- A new `.sh` file there is embedded automatically. The header comments are its manifest: `@name` / `@name_en`, `@group`
  (VPN, Web, Cloud, Files, or a new name), `@os ubuntu,debian,centos,rhel`, `@description(_en)`, `@param`, `@result`.
  See `ScriptManifest.cs` and the README section "Your own scripts".
- Every default value must pass validation. Every `@param` must be used in the code, and every `@result` must be reported
  with `sshm_result NAME value`. `BuiltinScriptTests` enforces this; add each new script to its `[InlineData]` list.
- The shared helper blocks (`# ---- shared helpers: base|services|site ----`) are copied **byte for byte** into every
  script that uses them. A test compares the copies, so to change a helper, edit every copy. Take the blocks from
  `filebrowser.sh`.
- Conventions:
  - Use `require_root` and `require_os` (which sets `OS_FAMILY` to debian or rhel).
  - Install packages with `pkg_install`, using Debian package names; they are mapped for dnf, with EPEL as a fallback.
  - `open_port` opens a port only in a firewall that is already active, and never enables one.
  - `require_free_port` stops the script if a port is taken.
  - Reruns must be idempotent: keep existing passwords, keys and data, and report a generated password only on the first
    run. Never lock or take over an account or folder the script did not create.
  - Watch out for `set -e` with `a && b` as the last command of a function. Use `if`.
- Test new scripts in the lab on Ubuntu, Debian and CentOS: add a check to `LabTests*.cs` and a row to
  `tools/script-lab/README.md`. SELinux and firewalld do not run in containers; list them as "check on a real server".

## MCP / agent safety

The MCP tools (`src/Quaykeep.Core/Mcp`) are limited by each server's `McpAccess` level (`McpPolicy`). New tools need a
level, an entry in the agent log, and confirmation for destructive actions. If this machine has the `quaykeep` MCP server
(`sshmanager` in setups from before the rename) connected, it runs against the user's **real** servers, so do not use it for testing. Use the lab.
