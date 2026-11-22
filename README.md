# MVNCP - Minimal Virtual Network Computing Project

A tiny, dependency-free VNC server and viewer for your **own local network**.
Two executables, no installer, no service, no tray icon, no Start menu entry.

> ## Disclaimer
>
> **Use MVNCP only on machines you own or have explicit written permission to access.**
>
> MVNCP is a plain LAN remote-desktop tool. It does **not** hide itself, does not
> evade antivirus, does not hide a desktop from the local user, and does not
> survive uninstall. The process is visible in Task Manager, the autostart task is
> visible in Task Scheduler, and the firewall rule is visible in Windows Firewall.
>
> Accessing someone else's computer without their consent is illegal in most
> countries. If you are looking for a covert RAT, this is not it, and this project
> is not intended to be used as one.
>
> The software is provided "as is", without warranty of any kind. You are
> responsible for how you use it.

---

## Why this exists

Most VNC servers insist on a tray icon, a Start menu folder, a Windows service and
an installer. If you just want to drive the second laptop on your desk while
testing an app, all of that is noise. MVNCP is deliberately minimal:

- **server**: one `winexe`, no window, no tray icon, no installer;
- **viewer**: one window with an address field;
- **autostart**: optional, one visible Task Scheduler task per side;
- **process names**: `mvncp-server.exe` / `mvncp-viewer.exe`, visible in Task Manager.

It is transparent by design. Nothing is hidden from the owner of the machine.

## Features

- RFB 3.8 (standard VNC protocol), VNC Authentication (DES), Raw encoding.
- Works with the built-in viewer and with most third-party VNC viewers.
- Server: primary-monitor capture, 128x128 dirty-tile diffing, raw BGRA updates.
- Viewer: fits the remote screen to the window with preserved aspect ratio,
  smooth scaling, correct mouse mapping, keyboard and wheel forwarding.
- No installation, no registry writes (unless you opt into autostart),
  no background service.
- ~60 KB of source, builds with the C# compiler that ships with .NET Framework.

## What this is not

- Not a hidden/stealth remote-access tool. There is no process hiding, no
  keylogging, no hidden desktop, no AV evasion, no self-deletion.
- Not encrypted. VNC Authentication proves the password but does not encrypt
  the session. Use it on a trusted LAN only.
- Not a full remote-support product: no clipboard sync, no file transfer,
  no audio, no multi-monitor support.

## Requirements

- Windows 7 SP1 / Windows 10 / Windows 11 (tested on Windows 10 Enterprise LTSC 1809 x64).
- .NET Framework 4.x (preinstalled on Windows 10/11).
- Administrator rights **once**, only if you want the Task Scheduler autostart
  and the firewall rule. The exes themselves run as a normal user.

## Build

No Visual Studio, no SDK, no NuGet. From the project folder:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output:

```
bin\mvncp-server.exe
bin\mvncp-viewer.exe
bin\mvncp-test.exe      (self-test)
bin\mvncp-server.ini    (created from the example on first build)
```

## Quick start

### 1. On the PC you want to control (server)

Edit `bin\mvncp-server.ini`:

```ini
port=5900
password=changeme
name=mvncp
bind=0.0.0.0
```

The password is limited to **8 characters** by VNC authentication. An empty
password disables authentication and is meant for debugging only.

Start `bin\mvncp-server.exe` (no window appears; check `bin\mvncp-server.log`
or Task Manager), or run `server-task-add.cmd` once to register it at logon and
open the firewall for private/domain networks.

### 2. On the PC you control from (viewer)

Run `bin\mvncp-viewer.exe`, type the server address (`192.168.1.10` or
`192.168.1.10:5900`) and press Connect. The password dialog can remember the
password in `bin\mvncp-viewer.ini`.

Or launch it directly:

```
bin\mvncp-viewer.exe 192.168.1.10:5900
```

Or run `viewer-task-add.cmd` once (edit `ADDR` inside it first) to start the
viewer minimized and auto-connect at logon.

### 3. Find the address and password

Run `show-connect-info.cmd` on the server PC. It reads `mvncp-server.ini`, lists
the LAN IPv4 addresses (marking the default-route one), shows whether the server
is running, and copies the address, the password, or both to the clipboard.

## Configuration

`mvncp-server.ini` (next to the server exe):

| Key | Default | Meaning |
|---|---|---|
| `port` | `5900` | TCP port |
| `password` | `changeme` | VNC password, first 8 chars |
| `name` | `mvncp` | Name shown in the viewer title |
| `bind` | `0.0.0.0` | Interface to listen on (`any` or an IP) |

`mvncp-viewer.ini` is written by the viewer when you tick "Remember password":

```ini
last=192.168.1.10:5900
password=changeme
```

It is plain text. Do not commit it to git.

## Autostart (Task Scheduler)

`server-task-add.cmd` and `viewer-task-add.cmd` are separate on purpose: run each
one on its own machine. Both self-elevate via UAC, create an `ONLOGON` task for
the current user, and (server only) add an inbound firewall rule for private and
domain profiles.

Remove everything:

```
schtasks /Delete /TN mvncp-server /F
schtasks /Delete /TN mvncp-viewer /F
netsh advfirewall firewall delete rule name="MVNCP Server"
```

If you cannot elevate at all, use the per-user Startup folder
(`Win+R` -> `shell:startup`) or the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
key instead. Both are visible in Task Manager -> Startup. Note that the firewall
rule still requires an administrator once, otherwise LAN connections are blocked.

## Security

- **No transport encryption.** VNC Authentication only proves knowledge of the
  password; the screen stream is plain. Never expose port 5900 to the internet
  and never forward it on your router.
- **Password length is effectively 8 characters.** That is a protocol limit.
- The firewall rule is scoped to `private,domain` profiles, so a network marked
  as "Public" will not accept connections.
- The viewer stores a remembered password in plain text.
- Run the server only on machines and networks you control.

## Limitations

- Primary monitor only.
- UAC prompts and the Windows lock screen are not visible or controllable
  (secure desktop). A UAC prompt on the remote PC must be answered locally.
- GDI capture: GPU-composited content (some games, protected video) may show
  artifacts or black areas.
- Raw encoding: fine on gigabit or good Wi-Fi, slow on weak links.
- Keyboard mapping is layout-aware on the server via `VkKeyScan`; typing
  non-Latin text works if the server has that layout installed.
- No clipboard, no audio, no file transfer.

## Troubleshooting

| Symptom | Check |
|---|---|
| Viewer: "Authentication rejected" | password in `mvncp-server.ini` vs. the one entered |
| Viewer: timeout | `netstat -ano \| findstr :5900` on the server; firewall rule; network profile |
| Server silent | `bin\mvncp-server.log` |
| Black frame | the Windows session is locked (VNC cannot see the lock screen) |
| Lag | weak Wi-Fi, large screen, heavy animation |

Self-test without the viewer (on the server):

```
bin\mvncp-test.exe 127.0.0.1 5900 changeme test.png
```

Expected output ends with `PASS` and writes `test.png`.

## Uninstall

1. Delete the scheduled tasks and firewall rule (see above).
2. Delete the project folder. There is nothing in Program Files or the registry
   unless you added autostart yourself.

## Project layout

```
src/Common.cs      RFB 3.8 client, VNC DES auth, network helpers, pixel copy
src/Server.cs      screen capture, dirty tiles, input injection, config
src/Viewer.cs      viewer window, connect/reconnect, scaling, input mapping
src/TestClient.cs  console self-test (mvncp-test.exe)
build.ps1          csc-based build
show-connect-info.*  address/password helper
*-task-add.cmd     per-machine Task Scheduler setup
```

## Publishing

`bin/`, logs, and the local `*.ini` files are already ignored by `.gitignore`,
so no passwords or binaries end up in the repository. `.gitattributes` keeps
`*.cmd` files in CRLF, which `cmd.exe` needs.

```
git init
git add .
git commit -m "MVNCP: minimal LAN VNC server and viewer"
git branch -M main
git remote add origin https://github.com/<you>/mvncp.git
git push -u origin main
```

Before you publish, check that `bin/mvncp-server.ini` (with your real password)
is not committed. `mvncp-server.ini.example` is the file that belongs in git.

## License

MIT. See [LICENSE](LICENSE).
