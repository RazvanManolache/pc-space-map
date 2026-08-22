# PC Space Map

PC Space Map is a read-only Windows disk inventory app inspired by tools like SpaceSniffer. It scans a drive or folder, renders a treemap of storage usage, highlights large files, and groups conservative cleanup candidates without deleting anything.

The goal is simple: make it easy to understand what is using space, then let the user decide what to remove in the owning app or in Windows.

## Features

- Treemap view for drives and folders
- Multiple tabs so several disks or folders can stay open at once
- Largest-files view
- Cleanup review grouped by confidence and rationale
- Scan notes for inaccessible paths and skipped links/junctions
- Folder-scoped refresh so a changed folder can be rescanned without redoing the whole disk
- CSV export of the current inventory
- Local loopback control API for assistant-style inspection without desktop takeover

## Safety model

- The app is read-only.
- It does not delete files.
- Cleanup suggestions are heuristics, not proof.
- Links and junctions are skipped on purpose to avoid loops and double counting.

Suggested cleanup groups are intentionally conservative:

- **Likely safe**: stale temporary files, old crash dumps, Recycle Bin contents
- **Usually safe**: browser and developer caches that can be rebuilt
- **Review first**: installers, archives, and large logs that may still matter

## Screens and workflow

1. Open a drive or folder in its own tab.
2. Run a scan.
3. Review the treemap, largest files, and cleanup candidates.
4. If only one folder changed, use **Update this folder** instead of rescanning everything.
5. Export the inventory if you want to review it elsewhere.

When the selected item is a drive root, the details panel also shows free space and total capacity.

## Build and run

Requirements:

- Windows
- .NET 9 SDK for development builds

Run from source:

```powershell
dotnet build .\PcSpaceMap\PcSpaceMap.csproj -c Release
.\PcSpaceMap\bin\Release\net9.0-windows\PC Space Map.exe
```

Create a portable self-contained build:

```powershell
.\Build-Portable.ps1
```

## Local assistant access

While the app is running, it also exposes a loopback-only HTTP control surface for structured inspection. It is bound to `127.0.0.1` only and uses a per-run token stored in:

`%LOCALAPPDATA%\PCSpaceMap\agent-session.json`

This is intended for semantic inspection and navigation, not desktop automation. See [AGENT_ACCESS.md](AGENT_ACCESS.md) for the endpoint contract.

## Privacy and public repo notes

This repository is intended to contain source code and documentation only.

It does **not** include:

- personal scan exports
- local reports
- built binaries
- cache contents
- machine-specific paths generated during local builds

If you use the app on your own machine, keep your exported scan results outside version control.

## Limitations

- Very large scans can use substantial memory.
- Protected locations may be partially omitted by the operating system.
- Cleanup analysis is rule-based and intentionally cautious.
- The app currently focuses on inventory and review, not automated cleanup execution.
