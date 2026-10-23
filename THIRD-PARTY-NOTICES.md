# Third-party components

Quaykeep is released under the MIT License (see `LICENSE`). It includes or uses these components under their own licenses:

| Component | Use | License |
|---|---|---|
| [SSH.NET](https://github.com/sshnet/SSH.NET) | SSH, SFTP, the built-in terminal | MIT |
| [Bouncy Castle for .NET](https://github.com/bcgit/bc-csharp) | Argon2, key formats | MIT |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) with Avalonia.Controls.DataGrid and the Inter font | the user interface on Windows, macOS and Linux | MIT (Inter: SIL Open Font License 1.1) |
| [FluentIcons.Avalonia](https://github.com/davidxuang/FluentIcons) with [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) | icons | MIT |
| [NativeWebView](https://github.com/wieslawsoltes/NativeWebView) | hosts the terminal and the editor pages in the system web engine (WebView2, WKWebView, WebKitGTK) | MIT |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | the Windows web engine behind NativeWebView | BSD-3-Clause (Microsoft), see the package's LICENSE.txt |
| [Velopack](https://github.com/velopack/velopack) | installers and updates | MIT |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | Linux session lock and idle time over D-Bus | MIT |
| [xterm.js](https://github.com/xtermjs/xterm.js) and its fit, web-links, unicode11 addons | the built-in terminal (`Assets/terminal/vendor`) | MIT, `Assets/terminal/vendor/LICENSE-xterm.txt` |
| [QRCoder](https://github.com/Shane32/QRCoder) | QR codes of script results (VPN links, Amnezia keys) | MIT |
| [CodeMirror 6](https://codemirror.net) with its language packages, Lezer parsers and legacy modes | the built-in editor (`Assets/editor/vendor/codemirror.js`, built from `tools/codemirror`) | MIT, `Assets/editor/vendor/LICENSE-codemirror.txt` |

The built-in VLESS scripts are adapted from [vless_docker_install_scripts](https://github.com/nickolsky/vless_docker_install_scripts).
The other install scripts download and run, on the user's server, the official images or packages of Xray, Hysteria,
AmneziaWG, nginx, Caddy, Nextcloud, Seafile, File Browser, PostgreSQL, MariaDB and Redis; none of them are shipped with
Quaykeep. The Amnezia VPN key format (`vpn://`) follows the [Amnezia VPN client](https://github.com/amnezia-vpn/amnezia-client).
On Windows the terminal and editor pages also use the Segoe Fluent Icons / Segoe MDL2 Assets fonts shipped with Windows
(not redistributed); elsewhere they fall back to plain symbols.
