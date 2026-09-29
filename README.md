# Quaykeep SSH Manager

English | [Русский](README.ru.md)

Quaykeep is a personal Windows tool for working with lots of SSH servers:
servers, passwords and keys in an encrypted vault, one-click sessions, automatic switching of a server
from password to key login, and a built-in SSH agent (like Pageant, for OpenSSH)
that lives in the tray and starts with Windows.

## Installation

Download `Quaykeep-X.Y.Z-win-x64.zip` from [Releases](https://github.com/nickolsky/quaykeep/releases),
unpack it into any folder (for example `C:\Tools\Quaykeep`) and run `Quaykeep.exe`.
It needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64) and the system OpenSSH client
(included in Windows 10/11). Data is kept in the `data` folder next to the program.

**Updates**: once a day the program checks GitHub releases (Settings → Updates, which also has "Check for updates"
and "Update and restart"). The archive is verified by SHA-256 and only program files are replaced; `data` is not touched.
Files in use (for example `qk.exe` in an open terminal) are renamed to `*.sshm-old` and deleted on the next start.

## Building from source

```powershell
.\publish.ps1                 # build into .\app (data goes to .\data next to Quaykeep.sln)
.\app\Quaykeep.exe            # first start: create the master password
.\publish-dev.ps1             # a test copy in .\app-dev with its own data, runs side by side with the main one
.\release.ps1 [-Publish]      # release artifact artifacts\Quaykeep-<version>-win-x64.zip (+ .sha256) and a GitHub release
```

The version is set in `Directory.Build.props` (`<Version>`); the release tag is `v<version>`.

## Upgrading from SSH Manager

Quaykeep was called **SSH Manager** up to version 1.0.1 (the program `SshManager.exe`, the console helper `sshm.exe`).
Version 1.0.1 cannot update itself to Quaykeep, so do it once by hand: exit SSH Manager from the tray, unpack the Quaykeep
zip into the same folder and start `Quaykeep.exe`. Your `data` folder, vault and settings are used as they are; the old
program files and the old autostart entry are removed on the first start. What you may need to redo yourself:

- **AI agents**: the helper is now `qk.exe`. Register the MCP server again with the command from "Settings → AI agents
  (MCP)" (for example `claude mcp remove sshmanager`, then `claude mcp add … quaykeep -- "…\qk.exe" mcp`).
- **Console**: type `qk <server>` instead of `sshm <server>`.
- **`SSH_AUTH_SOCK`**: if you pointed it at `\\.\pipe\sshmanager-agent-<user>`, that pipe keeps working; the new name is
  `quaykeep-agent-<user>`.
- `SSHMANAGER_DATA` / `SSHMANAGER_INSTANCE` still work; the new names are `QUAYKEEP_DATA` / `QUAYKEEP_INSTANCE`.
- Old `sshmanager-data-*.zip` backups can still be restored and are rotated together with the new `quaykeep-data-*.zip` ones.

On servers nothing changes: script variables (`SSHM_RESULT`, `SSHM_HOST`…), `/tmp/sshm-*`, `~/.cache/sshm` and the `sshm`
markers in `sshd_config` and iptables comments keep their names, so earlier installs and forwards are still recognized.

On first start the program asks you to choose a master password and offers to start with Windows
(checked by default; can be changed in Settings). When started with Windows, the program goes straight to the tray,
**locked**; the unlock window appears the first time a server or the agent is used.

## Features

- **Servers**: name, group, host/IP, port, user, password or key, jump host, extra ssh arguments, notes.
  A tree with nested groups (`VPN/Europe`) that collapse and are remembered; search (Ctrl+F), including
  by OS, region and containers; double click / Enter connects, Ctrl+N adds a server, F5 refreshes everything.
- **Server menu** (right click), grouped: connect, edit, copy, duplicate → install, port forwards, keys, AI agents →
  ping, checks, monitoring, history → reboot, delete. **Copy ▸**: host name (reverse DNS for an IP), IP address (resolved
  through DNS from this PC for a name), password (kept out of the Windows clipboard history and cleared from the clipboard
  after 30 seconds), public key, ssh command, and `http://` / `https://` addresses when port 80 / 443 / 8080 / 8443 is open
  on the server.
- **Ping…**: ICMP ping from this PC once a second in its own window: time, TTL, loss, min / avg / max. Many VPN servers drop
  ICMP, so monitoring does not use it; when there are no replies, the window shows the state of the SSH port instead.
- **"Check availability now"** and **"Test connection"**: the first only opens a TCP connection to the SSH port (the sshd
  greeting) and to the monitored ports, without logging in, and records the result in the uptime history; the second does
  a full SSH login with the saved password or key, runs `uname -a` and shows the result in a window.
- **Русский / English**: the "Язык (Language)" menu, the tray menu or Settings; switches instantly, no restart.
- **Availability monitoring**: a TCP connection to the SSH port and the sshd greeting, every 5 minutes by default (the interval
  is set globally and per server: right click → "Monitoring ▸", from every minute to once a day, or "Do not check").
  If SSH does not answer but at least one monitored port is open,
  the server counts as reachable (yellow dot). A coloured dot in the tree and a tray notification when a server goes down.
- **Uptime**: every check is written to `data\uptime`; the "Uptime" column shows availability over a day / week /
  30 days. "Uptime history…" in the context menu has availability and latency charts for 24 h … a year and a list of
  outages. Time when the program was not running counts neither as uptime nor as downtime.
- **Port monitoring**: mark TCP ports for a server (443 VPN, 2053 panels…), by hand or from the ones found
  on the server (`ss -tlnp`): "Port monitoring…" or "Monitor this port" in the context menu. A port that
  stops answering turns red in the "Ports" column, the server turns yellow, and the tray shows a notification. For a forwarded
  port the whole chain to the target server is checked. UDP is not checked: UDP has no connection, and a closed port
  cannot be told from an open one from outside.
- **Port forwards** (iptables DNAT, the "Port forwards" button): add, edit (double-click a row: the old forward is removed and the
  new one created by one script; if the new one fails, the old one comes back), delete.
  The "Monitoring" checkbox in a row puts the port under monitoring. Forwards across several servers are shown as a chain:
  `A:443 → B:8443 → C:443 (nginx)`, in the window, in the "Forwards" column and in the tooltip of a forward in the tree.
  "Go to …" in a forward's menu (or a double click) jumps to the server at the other end, straight to the next hop.
- **CPU / memory / disk**: after every successful check the program logs in over SSH and reads `/proc`
  (the CPU, "Memory" and "Disk" columns; disk is free space on `/`; the tooltip has load, disk, uptime).
- **What is on the server**: on the first connection (and when a server is expanded, if the data is older than 10 minutes) the program
  finds the Linux distribution, Docker containers, known services (nginx, Xray, 3X-UI, WireGuard, OpenVPN, Marzban…),
  listening ports and iptables forwards. Everything is shown in the expanded server; containers and services have logs,
  status and restart in a terminal.
- **Scheduled jobs**: the "Cron and timers" section of a server lists the crontabs of all users, `/etc/crontab`,
  `/etc/cron.d`, the `cron.hourly…monthly` scripts and systemd timers (schedule, next run). A row's menu has
  copy, open the file in the editor, `crontab -e` in a terminal, timer status and log. From the console: `qk cron <server>`.
- **Reboot**: the "Reboot" button (and the server menu item) after a confirmation; the program waits for the server to
  come back and says in the tray how many seconds it took (or that it did not come back within 10 minutes).
- **Region**: country and city by IP (the online service ipwho.is, ip-api.com as a fallback; cached for 30 days, can be turned off).
- **Port forwards (iptables)**: "Port forwards…" shows the existing DNAT rules and adds new ones
  (`443 → another server:443`, tcp/udp, ranges): DNAT + MASQUERADE + FORWARD, `ip_forward`, saved with
  netfilter-persistent / iptables-save (offers to install iptables-persistent when needed).
  Forwards are shown in the main tree, both outgoing and incoming.
- **Firewall** ("Firewall…" in the server menu, "Firewall for the group…" in a group's menu): **block** chosen IPs and
  networks, or **allow only** them, for ports (tcp/udp, lists, ranges) or for the whole server; IPv4 and IPv6.
  - **Where it covers:** ports published by Docker containers and port forwards are covered too, so Docker's usual way
    around ufw doesn't apply.
  - **Order:** blocks → port allow-lists → the whole-server allow-list. Loopback, open connections, DHCP and ICMPv6 always
    pass, so an allow-list can't take away the server's address.
  - **Only restricts:** it doesn't open what the server's own firewall (ufw, firewalld) closes.
  - **Apply is safe:** the rules go in together with a timer on the server that puts the old ones back, and they are kept
    only when a *new* SSH login still works. The window also warns first when the rules would cut SSH from this PC.
  - **Saved** in `quaykeep-firewall.service`, so they come back after a reboot.
  - **Sources:** "+ this PC", "+ everyone" (`0.0.0.0/0` and `::/0`), or "+ server ▾", which adds another server's IP and
    remembers that it belongs to that server.
  - **Presets:** named rule sets (for example "SSH from the office only"), kept once and ticked per server. A server
    uses its own rules plus its presets, and editing a preset applies it on every server that uses it.
  - **Several servers:** "Copy to servers…" (add or replace), or the group window.
  - **A server's IP changes:** when you save a new host in its editor, Quaykeep lists the firewall rules (linked or the
    same address typed in) and the **port forwards** on other servers that point to the old IP. It updates and applies
    them in one go.
  - **On the server** the rules live in Quaykeep's own iptables chains, `QK-IN` / `QK-FWD`; "Turn firewall off…" removes
    them.
  - **Proxies:** clients behind a CDN or proxy arrive with the proxy's IP.
- **Install scripts**: "Settings → Install scripts" (kept in the encrypted vault), run with a right click →
  "Install ▸". A script runs in a program window: a parameter form, live output, results
  saved with the server (the "Script results" section in the tree, "Copy value"); links (`vless://`,
  `hysteria2://`, `vpn://` keys, site addresses) can be shown as a QR code for a phone ("Show QR code"). Interactive
  installers have an "In terminal" button. The script's OS is chosen in a drop-down list (several, optionally with a version:
  `debian:12`); scripts for the server's OS are listed first.
  Built-in scripts (grouped in the menu):

  | Group | Script | What you get |
  |---|---|---|
  | VPN | **VLESS REALITY** (Xray in Docker): Ubuntu and CentOS; Debian 12 with a small disk | a `vless://…` link, the port under monitoring |
  | VPN | **Hysteria 2** (official image): self-signed certificate with pinSHA256 or Let's Encrypt, Salamander obfs | a `hysteria2://…` link |
  | VPN | **AmneziaWG** (official `amneziavpn/amneziawg-go`, no kernel module, 512 MB of memory or more); clients by name: `phone,laptop` | a `vpn://` key for Amnezia VPN per client, a QR code of the config (AmneziaWG and Amnezia VPN), `.conf` in `/opt/amneziawg/clients` |
  | Web server | **Static site, nginx without Docker**, HTTPS via certbot | the folder `/var/www/sshm-site` |
  | Web server | **Static site, Caddy in Docker**, HTTPS turns on by itself with a domain | the folder `/opt/static-site/site` |
  | Cloud storage | **Nextcloud** (+ PostgreSQL, Redis, cron; HTTPS via Caddy with a domain) | address, admin login and password |
  | Cloud storage | **Seafile 13** (+ MariaDB, Redis; HTTPS via Caddy with a domain) | address, admin login and password |
  | Cloud storage | **File Browser**: web access to a folder, 256 MB of memory or more | address, admin login and password |
  | File transfer | **SFTP user** through the OpenSSH that is already there: SFTP only, chroot into its own folder, password and/or key (password login can be kept for this user only, even when it is off on the server) | an `sftp://` address, login and password |
  | File transfer | **FTP / FTPS (vsftpd)** for cameras, scanners and old clients: TLS only by default (Let's Encrypt for a domain or self-signed), chroot, passive ports | an `ftp://` address, login and password, the port under monitoring |
  | — | **Docker Engine + Compose**, **Uptime Kuma** (Docker Compose) | versions / address |

  New scripts target Ubuntu 22.04+, Debian 12+ and CentOS Stream / Rocky / AlmaLinux / RHEL 8+ (checked on the test
  lab). On CentOS, packages are installed with dnf (what the base repositories lack comes from EPEL), Docker from the
  download.docker.com repository, and SELinux is set up for nginx. Running a script again keeps keys, passwords and data
  (links stay the same); ports are opened only in a firewall that is already on (ufw, firewalld or iptables). For Nextcloud and Seafile on servers
  with less than 2 GB of memory a swap file is created. VLESS REALITY comes from
  [vless_docker_install_scripts](https://github.com/nickolsky/vless_docker_install_scripts). How to check the scripts
  on test servers: [tools/script-lab](tools/script-lab/README.md).
- **Backup**: a zip of the whole `data` folder to a local folder or to a server over SFTP, rotation, automatic backup every
  N hours, a "Back up now" button. **Restore**: "Restore from backup…" (Settings → Backup
  or the "File" menu): the latest backup from the folder / server or any `quaykeep-data-*.zip`; it needs the master password
  as of the backup; the current data is saved to `data\backups\before-restore-*.zip` before it is replaced.
- **One-click connection**: a session opens in a Windows Terminal tab (or a new window / a plain console,
  see Settings). With a key it goes through the built-in agent; with a password, the password is filled in automatically through
  `SSH_ASKPASS`. Neither the password nor the private key ever reaches the command line or the disk.
- **Built-in terminal** (Settings → Terminal → "Built-in terminal"): sessions in tabs of the program window
  (xterm.js). For bash / zsh an integration is loaded from `~/.cache/sshm` (rc files are not changed): suggestions while typing
  from the server's history, commands, subcommands and flags of docker / compose / systemctl / journalctl / apt / git / ufw…, files,
  containers, services, packages; a grey suggestion from history (→ accepts), Ctrl+Space shows the list, Tab inserts.
  The `edit FILE` command opens a file in the built-in editor, Ctrl+Shift+F lists the files in the current folder.
- **File manager (SFTP)**: the "Files" button, two panels in the Far style, this PC and the server; F5 copy,
  F6 move, F7 folder, F8 delete, F2 rename, F4 edit; Insert / Shift+arrows select files,
  Num + / Num − select / deselect by mask (`*.yml;*.conf`), Num * inverts, Ctrl+click / Shift+click, Shift+right click (drag it over several);
  drag and drop between panels and from Explorer, a transfer queue with progress and cancel, permissions (chmod).
- **Editor** (CodeMirror 6): highlighting for shell / yaml / json / nginx / ini / Dockerfile…, find and replace, Ctrl+S.
  Encoding and line endings stay as they were; a file changed on the server is not silently overwritten;
  without write access, saving goes through sudo (the file keeps its owner and permissions).
- **"Set up key login"**: logs in with the saved password, generates an Ed25519 key (or takes an existing one),
  adds it to `~/.ssh/authorized_keys` (no duplicates, permissions 700/600), checks key login and switches the server
  to the key. With the **"Turn off password login"** checkbox, and only once key login has been verified, the program turns
  `PasswordAuthentication` off in sshd (the file `/etc/ssh/sshd_config.d/00-sshm-no-password.conf`, or a block at the top of
  `sshd_config`), checks the settings with `sshd -t` / `sshd -T` before sshd is reloaded, then logs in with the key again; if that
  fails, password login is put back through the connection that is still open. It needs root or sudo; the saved password
  stays (sudo needs it).
- **Keys**: generate Ed25519 / RSA-4096, import (OpenSSH, PEM, PuTTY .ppk, including with a passphrase),
  copy the public key, export the private key (the file is readable only by your Windows user).
- **Server key checks**: the server's key fingerprint is shown on the first connection; a changed key
  is shown as a warning. The program uses its own `data\known_hosts`.
- **Tray**: quick access to servers (the "Connect" menu), lock, exit. Closing the window minimizes it to the tray.
- **Hotkey** **Win+Alt+X** (system-wide) shows the window, pressing it again hides it; change it or
  turn it off in "Settings → Interface" (click the field and press the combination).
- **Auto-lock**: after N minutes of PC inactivity (120 by default) and, optionally, on Win+L.
- **Console**: `qk <server name>` connects to a saved server right in the current terminal,
  `qk list` lists the servers, `qk cron <server>` shows scheduled jobs. Add the `app` folder to PATH
  to run `qk` from anywhere.
- **AI agents (MCP)**: Claude Code, Codex and others can manage servers through Quaykeep, see below.

## AI agents (MCP)

Quaykeep works as an MCP server: the agent calls its tools, and the program runs the commands with the passwords and
keys from the vault; the agent never gets them. Turn it on in "Settings → AI agents (MCP)", which also has ready-made
connection commands with a "Copy" button:

```powershell
claude mcp add --scope user quaykeep -- "D:\…\app\qk.exe" mcp          # Claude Code (stdio)
```

```toml
# Codex: ~/.codex/config.toml
[mcp_servers.quaykeep]
command = 'D:\…\app\qk.exe'
args = ["mcp"]
tool_timeout_sec = 900
```

Optionally also over HTTP on `127.0.0.1` (Streamable HTTP, a token in the `Authorization: Bearer …` header; foreign
`Origin` / `Host` headers are rejected). If the program is not running, `qk mcp` starts it in the tray; when the vault
is locked, the agent asks you to unlock it.

**What an agent may do is set per server** ("Edit…" → "AI agent access", or the "AI agent access ▸" menu);
each level includes the previous ones:

| Level | Tools |
|---|---|
| Off (default) | the server is invisible to the agent |
| Read only | `list_servers`, `server_status` (availability, CPU / memory / disk, uptime), `list_containers`, `list_services`, `list_cron_jobs`, `list_ports`, `list_firewall`, `container_logs`, `service_logs` |
| Read and reboot | + `reboot_server` |
| Menu commands | + `container_action` (start / stop / restart / autostart / remove), `service_action`, `refresh_info`, `list_install_scripts`, `run_install_script` + `get_job` |
| Full access | + `run_command` (sudo included), `list_directory`, `read_file`, `write_file`, `delete_path`, `upload` / `download` (only to the server's allowed local folders), `script_results`, `add_firewall_rule` / `remove_firewall_rule` (asks you; a rule that would cut Quaykeep's SSH is refused) |

- Reboots and deletions (of a container, file or folder), as well as stopping SSH, are first shown in an
  "Allow / Deny" window (no answer in 2 minutes means deny; can be turned off in Settings).
- System folders (`/`, `/etc`, `/usr`…) can never be deleted by an agent at any level; passwords in script results
  are visible only with full access.
- Servers behind a jump host / with extra ssh parameters are not available to agents (except their status).
- **Log** of every call, including denied ones: "Agent log…" in the server menu or in Settings, in real
  time, per server or for all. The "Console" view shows the log like a terminal: every call has a prompt
  with the agent and server names, the command (`#` means sudo) and its output in colour, errors and denials in red;
  the "Lines" view shows the file's lines as they are. Files are in `data\agent-logs`: a new file every day and when full; the total
  size per server and the retention period are set in Settings. The log is not included in backups.

## SSH agent

The agent listens on the named pipe `\\.\pipe\openssh-ssh-agent`, the standard address used by
`ssh.exe`, `git` and VS Code Remote-SSH. If that pipe is already taken by another agent (for example **1Password** or
the Windows `ssh-agent` service), Quaykeep uses `\\.\pipe\quaykeep-agent-<user>`.
Sessions started from Quaykeep work either way. For other programs to take keys
from Quaykeep too, free the standard pipe (for example, turn off the SSH agent in 1Password) or set a
user environment variable:

```powershell
[Environment]::SetEnvironmentVariable('SSH_AUTH_SOCK', '\\.\pipe\quaykeep-agent-<user>', 'User')
```

The exact agent address is shown in the status bar of the main window. While the vault is locked, the agent does not
hand out keys (when a client asks, the unlock window appears; can be turned off in Settings).

## Data and security

All data lives in the `data` folder: next to `Quaykeep.exe`, or next to `Quaykeep.sln` in a source checkout (the folder is
in `.gitignore`):

| File | Contents |
|---|---|
| `vault.dat` | servers, passwords, private keys: AES-256-GCM, key derived from the master password with Argon2id (64 MB, 3 iterations) |
| `backups\` | the 20 latest versions of `vault.dat` and `before-restore-*.zip` snapshots taken before a restore |
| `settings.json` | interface settings (no secrets) |
| `known_hosts` | server keys in OpenSSH format |
| (in `vault.dat`) | collected server facts (OS, containers, region), monitored ports, install scripts |
| `pub\` | public keys (`ssh.exe` needs them to pick the right key from the agent) |
| `uptime\` | availability check history (a CSV per server: time, state, latency), kept for 400 days |

A backup = a copy of the `data` folder + the master password (the "Backup" section does this automatically;
to restore, unpack the archive into the `data` folder). **A forgotten master password cannot be recovered.**
The data folder can be overridden with the `QUAYKEEP_DATA` environment variable.

## Your own scripts with parameters and results

Parameters and results are described by comments in the script itself; the run form is built from them:

```bash
#!/usr/bin/env bash
# @name    My service
# @os      ubuntu,debian:12
# @group   VPN                                              # submenu in "Install ▸" (VPN, Web, Cloud, Files or your own)
# @description What the script does (shown in the run window; @description_en is the English text)
# @param   DOMAIN  text   required label="Domain" default=example.com hint="A hint under the field"
# @param   MODE    choice options=fast,safe default=safe label="Mode"
# @param   PATH_X  text   default=/x when=MODE=fast          # the field is shown only when MODE=fast
# @param   PORT    number default=8443 label="Port"
# @param   DEBUG   bool   label="Verbose output"             # 1 / 0
# @param   TOKEN   secret label="Token"                      # hidden while typing, not saved in the history
# @result  URL     label="Link"
# @result  PORT    label="Port" monitor=MyService            # a port value is put under monitoring
# @result  KEY_*   label="Key"                               # KEY_phone, KEY_laptop… (the ones that disappear are removed)
set -euo pipefail
echo "Installing on $SSHM_SERVER_NAME ($SSHM_HOST): $DOMAIN $MODE"
echo "URL=https://$DOMAIN:$PORT" >> "$SSHM_RESULT"
echo "PORT=$PORT" >> "$SSHM_RESULT"
```

Parameters arrive as environment variables (through a file in a private folder `/tmp/sshm-*`, not on the command line); `label_en=` / `hint_en=`
set the English text. Results are `NAME=value` lines in the `$SSHM_RESULT` file; the latest values are shown with the server,
and the run history (parameters without secrets, exit code, the tail of the output) is kept in the vault too.

## Solution layout

```
src/Quaykeep.Core   vault, cryptography, key formats, SSH agent, IPC, launching ssh, key setup (SSH.NET),
                      RU/EN strings, monitoring, metrics, inventory, GeoIP, iptables, scripts, backup
src/Quaykeep        the WPF app: windows, server tree, tray, autostart, auto-lock
src/qk              console helper: terminal tab, SSH_ASKPASS, `qk <server>`
tests/Quaykeep.Tests  unit tests + compatibility with ssh-keygen/ssh-add + e2e
```

Tests: `dotnet test`. The end-to-end test with a real sshd is turned on with the variable
`SSHM_E2E=host:port:user:password` (use a throwaway test server/container; checking
port forwards needs root and `--cap-add NET_ADMIN`). Working on the code with an AI agent: see [AGENTS.md](AGENTS.md).

## License

[MIT](LICENSE) © 2026 Artem Nickolsky. Third-party components and their licenses are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
