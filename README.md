# Ragnarok Rebuild (geocine fork)

A fork of [Doddler's Ragnarok Rebuild](https://github.com/Doddler/RagnarokRebuildTcp), a Ragnarok Online–like server and Unity client.

**It adds one helper, `rr`, that sets up, runs and builds the whole thing for you.**

No game assets are included. You need a Ragnarok client's `data.grf`, or the `rebuild-pack.grf` or baked bundle this fork shares.

## Start

In PowerShell on Windows:

```powershell
irm https://raw.githubusercontent.com/geocine/RagnarokRebuildTcp/dev/setup/bootstrap.ps1 | iex
```

It installs what's missing, clones this repo and runs `rr setup`.

Already cloned? Run `.\rr setup` in the repo root.

Given our shared files, set up from them instead. In a clone, run `.\rr use-grf <rebuild-pack.grf>` or `.\rr use-bundle <RagnarokRebuild-baked-date.7z>`. On a bare machine, pass the file to the bootstrap with `-Grf` or `-Bundle`:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/geocine/RagnarokRebuildTcp/dev/setup/bootstrap.ps1))) -Bundle D:\Downloads\RagnarokRebuild-baked-2026-10-02.7z
```

## Guide

Everything else is in [`setup/guide.html`](setup/guide.html). Open it in a browser; GitHub only shows its source.

## What the fork adds

| Path | What it is |
| --- | --- |
| `rr.cmd`, `setup/rr.ps1` | The `rr` helper |
| `setup/bootstrap.ps1` | Setup for a machine with nothing installed |
| `setup/config.json` | Defaults. `config.local.example.json` shows the machine paths `rr init` asks for |
| `setup/git-hooks/` | Keep `master` an exact copy of upstream |
| `setup/tools/RebuildPack/` | Builds the client data pack from your GRFs |
| `setup/pack/` | Stand-ins for files no client ships, and our pack's fingerprint |
| `RebuildClient/Assets/Scripts/Editor/Automation/` | Headless Unity import, bake and build |
| `RebuildClient/Assets/Scripts/Automation/` | The player side of `rr smoke` |
| `setup/guide.html` | The guide |

All of it lives in new paths, and `rr sync` keeps our README when upstream edits theirs.

## Branches

`dev` is where the work goes. `master` mirrors upstream and never gets commits.

## Upstream

Manual setup, contribution guidelines and the Discord are in [Doddler's README](https://github.com/Doddler/RagnarokRebuildTcp#readme).
