# NetworkIsolator

🇬🇧 English · [🇫🇷 Français](README.fr.md)

A small Windows tool that stops **one specific application** from reaching the network, without touching anything else on your PC. It works by creating targeted rules in the Windows Firewall, so Windows itself enforces the block.

- **Zero third-party dependency**: C# / WPF on .NET, native Win32 calls only.
- **Reversible**: nothing but firewall rules is created. No driver, no registry change, no change to the application.
- **Bilingual interface**: English and French, switchable on the fly (the choice is remembered).

## Two ways to use it

| | What it does |
|---|---|
| 🔒 **Block for good** | Creates permanent firewall rules. The application can no longer connect, however you start it (Start menu, shortcut, double-click). The tool does not need to stay open. |
| ▶ **Start without a connection (once)** | Starts the application with its network cut for that session only. The rules are removed when the application closes. |

Unblocking, or removing an application from the list, deletes its rules.

## Features

- "My applications" list showing the **real** firewall state of every entry (blocked / free / incomplete).
- Block level: **whole network**, or **Internet only** (keeps your local network reachable).
- Optional blocking of incoming connections (overrides the allow rules some installers add).
- Several programs per entry (an application plus its helper programs).
- One-time launch with: no admin rights for the application, process-tree tracking (child processes are cut too), dead proxy variables as an extra layer.
- Crash safety: a write-ahead journal lets the tool clean up temporary rules left behind by a crash or a kill.
- Desktop shortcuts, import / export of the list, activity log.
- Command line for scripts and shortcuts.
- Refuses to block programs inside the Windows folder (could cut your Internet access or break the system).

## Requirements

- Windows 10 / 11
- Administrator rights (the tool asks once at start-up; they are needed to create and remove firewall rules)
- Windows Firewall turned on
- To build: .NET 10 SDK

## Build

```powershell
dotnet build -c Release
```

Single-file publish:

```powershell
dotnet publish NetworkIsolator/NetworkIsolator.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Command line

```text
NetworkIsolator.exe <app.exe> [options] [-- application arguments]
NetworkIsolator.exe --profile "Name" [options]
NetworkIsolator.exe --block "Name"          block a profile for good
NetworkIsolator.exe --unblock "Name"        remove a profile's permanent block
NetworkIsolator.exe --list | --cleanup | --version | --help
NetworkIsolator.exe --shortcut "Name"       create a Desktop shortcut for a profile
NetworkIsolator.exe --export <file.json> | --import <file.json>
NetworkIsolator.exe                         open the window
```

Options: `--lan`, `--block-all`, `--no-inbound`, `--no-proxy-env`, `--no-children`, `--keep-admin`, `--args "..."`, `--cwd <folder>`, `--wait`.

Exit codes: `0` ok, `1` error, `2` syntax, `3` not found, `4` UAC declined, `5` firewall, `6` launch failed.

## How it works

- Every rule is a **block** rule targeting the full path of an `.exe`, created with `netsh advfirewall`.
- Permanent rules are named `NetIsolator_P_<hash>_<name>_<out|in>`; the name is derived from the path, so they can always be found, checked and removed.
- Temporary rules (one-time launch) are named `NetIsolator_...` and recorded in a journal before they are created.
- "Internet only" mode blocks the public address ranges (a block always beats an allow in Windows Firewall).

## Data

Everything is stored in `%APPDATA%\NetworkIsolator`: `profiles.json` (your list), `sessions\` (journal), `logs\`, `language.txt`.

## Undoing everything by hand

Open `wf.msc` (Windows Defender Firewall with Advanced Security) and delete the rules whose name starts with `NetIsolator_`.

## Limits

- The block targets an application, not Windows: system services are unaffected.
- Looking up a site name goes through a Windows service and is not blocked, but the connection itself is.
- Microsoft Store (UWP) apps are not supported.
- The block follows the file path: if the application is moved or reinstalled elsewhere, block it again.
- A blocked application loses its online features (multiplayer, updates, license checks, syncing). Some programs refuse to start without a connection: unblock them.
