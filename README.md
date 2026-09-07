# AmiGotekMediaBuilder — C#/.NET

**Language:** [polski / Polish](README.md) | English

A C#/.NET 10 migration of `ami-gotek-media-builder`. It scans ADF, DSK, and
ZIP files, parses TOSEC-style names, groups multi-disk releases, maintains
JSONL/SQLite catalogs, downloads public metadata and artwork, writes NFO files,
and exports a safe Gotek staging tree.

The demoscene pipeline is a separate product: `AmiGotekMediaBuilder.Demoscene`
with its own CLI and Avalonia GUI.

## Requirements

- .NET SDK 10.0 or newer;
- Windows 10/11, Linux desktop, or macOS for the Avalonia GUI;
- a source directory containing `.adf`, `.dsk`, or `.zip` files.

The source directory does not have to be named `original`. If `original_dir` is
omitted, `library_root` itself is scanned. Inputs are read-only, and managed
directories (`assets`, `catalog`, `work`, `demoscene`, and others) are excluded
automatically when the root is scanned.

In the main GUI, the working directory is the base containing `assets`,
`catalog`, and `work`. Selecting an existing `assets` or `catalog` directory is
safe: its managed parent is detected, so an `assets\assets` tree is not made.

## Theme and fonts

The main GUI uses a dark navy/blue Hall-of-Light-inspired theme and embeds the
[Zerove font](https://ggbot.itch.io/zerove-font), a readable retro-technical
CC0 1.0 font with Polish-character support. The TTF is bundled, so the look is
consistent on Windows, Linux, and macOS. Sources, licensing, and the file hash
are documented in [THIRD_PARTY.md](THIRD_PARTY.md).

The demoscene GUI is independent and uses a green Matrix-style theme with the
MatrixType Display font.

## Build and test

```powershell
dotnet build AmiGotekMediaBuilder.slnx
dotnet test AmiGotekMediaBuilder.Core.Tests\AmiGotekMediaBuilder.Core.Tests.csproj --no-build --no-restore
```

The solution builds the Core library, the main CLI/GUI, and the independent
demoscene CLI/GUI.

## Initialize a library

```powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- init `
  --library-root C:\AmigaLibrary `
  --config C:\AmigaLibrary\config\config.toml
```

The source may be the library root, or a separate directory:

```powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- init `
  --library-root C:\AmigaLibrary `
  --original-dir C:\AmigaImages
```

The default layout is:

```text
C:\AmigaLibrary\
├── *.adf, *.dsk, *.zip        # input may be stored in the root
├── original\                  # optional separate input directory
├── catalog\
│   ├── catalog.db             # shared metadata/artwork SQLite cache
│   ├── scan.jsonl
│   ├── parse.jsonl
│   ├── groups.jsonl
│   └── metadata-cache\
├── assets\
│   ├── nfo\
│   ├── artwork-original\
│   └── artwork-processed\
├── work\staging\
├── output\
├── unknown\, review\, reports\, logs\
├── config\manual-approvals\
└── demoscene\downloads\, metadata\, cache\
```

## Configuration and scan

```powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- config show `
  --config C:\AmigaLibrary\config\config.toml
dotnet run --project AmiGotekMediaBuilder.Cli -- config validate `
  --config C:\AmigaLibrary\config\config.toml

dotnet run --project AmiGotekMediaBuilder.Cli -- scan `
  --library-root C:\AmigaLibrary
dotnet run --project AmiGotekMediaBuilder.Cli -- scan `
  --library-root C:\AmigaLibrary --json
```

Configuration precedence is: command-line option, `AMIGA_ADF_*` environment
variable, then `--config` TOML. A minimal TOML file is:

```toml
library_root = "C:\\AmigaLibrary"
# optional; if omitted, library_root itself is scanned
original_dir = "C:\\AmigaLibrary\\original"
staging_dir = "C:\\AmigaLibrary\\work\\staging"
cache_dir = "C:\\AmigaLibrary\\catalog\\metadata-cache"
demoscene_dir = "C:\\AmigaLibrary\\demoscene"
# optional local SQLite database converted from GameBase .mdb
gamebase_db = "C:\\Databases\\AmigaGameBase.sqlite3"
```

Scanning recursively calculates SHA-256 for ADF/DSK files and ADF/DSK entries
inside ZIP archives. ZIP files are read without extraction or modification;
entries use the virtual path `collection.zip::Game.adf`. The parser extracts
title, year, language, chipset, version, crack group, disk number, edition,
and special-disk roles.

## Build and cache

Offline build performs scan → parse → group, writes catalogs and NFO files, and
creates a local game thumbnail when needed:

```powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- build `
  --library-root C:\AmigaLibrary --json
```

Offline mode makes no network requests. The embedded
`default-game-artwork.jpg` is written to `assets\artwork-original` and
`assets\artwork-processed`; it is never applied to demoscene data.

`catalog/catalog.db` is a multisystem cache. Records are separated by
`system_id`, `platform`, and `content_type` (`game`, `demo`, `tool`). The main
key is the SHA-256 of each disk (or all disk hashes for a set), with
`release_key` as a compatibility fallback.

The first online build stores provider results and artwork indexes in SQLite.
Later builds reuse valid metadata and existing artwork without repeating the
same HTTP lookup or image download. Legacy
`catalog/metadata-cache/metadata-*.json` files remain readable and are imported
into SQLite; they are not deleted.

## Online metadata and artwork

`--online` enables credential-free public providers: Hasheous, Playmatch,
OpenRetro, Hall of Light, Wikipedia, and optional local GameBase. No API keys,
passwords, or credentials are stored in this repository. If no provider finds
an image, metadata falls back to filename data and artwork falls back to
`default-artwork`.

```powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- build `
  --library-root C:\AmigaLibrary --online
```

The JSON adapter accepts `results`, `games`, `data`, and `items`, plus common
image fields such as `artwork_url`, `image_url`, `cover.url`, `images[]`, and
`screenshots[]`. OpenRetro supplies Amiga title data and front/first-screen
artwork. Hasheous and Playmatch use public hash-identification endpoints,
Wikipedia is a title-based fallback, and Hall of Light reads the public Amiga
game catalogue. Hall of Light anti-bot pages are treated as a safe miss; CAPTCHA
is not bypassed.

Artwork is stored as:

```text
assets\artwork-original\<Release>.<ext>
assets\artwork-processed\<Release>.<ext>
assets\artwork-original\<Release>.<ext>.source.json
```

The original bytes and provenance are preserved. Artwork downloads follow up to
three validated redirects, honor the system proxy, block private hosts, limit
response size, and verify JPG/PNG/WEBP/GIF signatures. An HTML challenge saved
as `.jpg` is rejected. Missing-artwork cache records and the embedded placeholder
are retried on a later online build. Legacy game records tagged `pouet` or
`demozoo` are considered stale and cannot leak demoscene titles, descriptions,
or thumbnails into the game pipeline.

### GameBase

GameBase is local-only and needs no credentials. Convert a `.mdb` database to
SQLite with Skyscraper's `mdb2sqlite` helper, then configure it with
`--gamebase-db`, TOML, or `AMIGA_ADF_GAMEBASE_DB`:

```powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- init `
  --library-root C:\AmigaLibrary `
  --gamebase-db C:\Databases\AmigaGameBase.sqlite3
dotnet run --project AmiGotekMediaBuilder.Cli -- build `
  --library-root C:\AmigaLibrary --online
```

The provider recognizes common table layouts and resolves local artwork from
the database directory and folders such as `Screenshots`, `Cover`, and
`Images`. See the [OpenRetro](https://gemba.github.io/skyscraper/SCRAPINGMODULES/#openretro)
and [GameBase DB](https://gemba.github.io/skyscraper/SCRAPINGMODULES/#gamebase-db)
module descriptions.

## Demoscene application

`AmiGotekMediaBuilder.Demoscene` catalogs Amiga productions from Pouët and
Demozoo by `ocs-ecs`, `aga`, and `ppc-rtg`. It downloads metadata, descriptions,
thumbnails, and permitted ADF/DSK files. ZIP and GZip containers are supported.
DMS, LHA, ROM, FTP, and executable files are skipped, never run or converted.

Demozoo uses `production_type=1` (Demo). Pouët IDs found on Demozoo are used
for deduplication; otherwise title, group, year, and platform are compared.
Demozoo platform IDs are OCS/ECS (5), AGA (6), and PPC/RTG (26).

Discovery without downloading:

```powershell
dotnet run --project AmiGotekMediaBuilder.Demoscene.Cli -- list `
  --platform aga --type demo --max-items 50
dotnet run --project AmiGotekMediaBuilder.Demoscene.Cli -- show `
  --pouet-id 6548 --json
```

Resumable mass synchronization requires explicit confirmation:

```powershell
dotnet run --project AmiGotekMediaBuilder.Demoscene.Cli -- sync `
  --platform aga --type demo --all --acknowledge-downloads `
  --max-concurrency 2 --max-bytes-total 4294967296
```

Without `--all`, at most 100 productions from the first page are downloaded.
Binary files go to `demoscene\downloads`, metadata to `demoscene\metadata`,
and thumbnails to `assets\demoscene`. Every download has a `.source.json`
sidecar; identical files are skipped on later runs. Demoscene thumbnails are
never used as game artwork.

The independent Avalonia GUI is started with:

```powershell
dotnet run --project AmiGotekMediaBuilder.Demoscene.Gui
```

## Gotek export

Export requires explicit gate acknowledgement and verified artwork dimensions:

```powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- export `
  --library-root C:\AmigaLibrary `
  --export-gate-acknowledged `
  --verified-artwork-width 320 `
  --verified-artwork-height 240 `
  --run-id review-001 --json
```

Add `--online` to download artwork during the same operation. Output is written
below `<library_root>\work\staging\<run-id>\`, under `ADF` or `DSK`.

Multi-disk output uses simple names `<Release>-1` through `<Release>-n`.
TOSEC markers such as `(Disk 2 of 2)(Data)` are used for grouping and numbering
but are not copied to output names. Special images keep their role, for example
`<Release>-Save.adf`; a single unnumbered image becomes `<Release>-1.adf` or
`<Release>-1.dsk`. NFO and artwork are copied beside the disk images.

Files in one shared input subdirectory are one game. Thus
`Atlantis - 01.adf` through `Atlantis - 11.adf` become
`Atlantis-1.adf` through `Atlantis-11.adf`, while `Atlantis - Save.adf` becomes
`Atlantis-Save.adf` in the same directory. See the
[TOSEC Naming Convention](https://www.tosecdev.org/tosec-naming-convention).

Use `--verify-only` to validate without writing new files.

## Main Avalonia GUI

```powershell
dotnet run --project AmiGotekMediaBuilder.Gui
```

The workflow is locked to `Scan` → `Build` → `Export`: only Scan is enabled at
startup, Build follows a successful scan, and Export follows a successful build.
The GUI has source, working, and destination pickers. Paths are stored locally
in `%LOCALAPPDATA%\AmiGotekMediaBuilder\gui-settings.json` on Windows or the
equivalent `LocalApplicationData` directory on Linux/macOS; no credentials are
stored.

The upper activity box shows the current file, ZIP entry, provider, artwork
download, or export item. The lower operation log retains the full history and
errors. Supported screen profiles are 480×320 Guition JC3248W535C 3.5",
800×480 Waveshare ESP32-S3-Touch-LCD-7, and 320×240 Waveshare
ESP32-S3-Touch-LCD-2.8, matching the
[Gotek Touchscreen interface](https://mesarim.github.io/Gotek-Touchscreen-interface/).

## GitHub releases

`.github/workflows/release.yml` runs for a published Release or a `v*` tag such
as `v1.1.1`. It publishes self-contained, single-file CLI and GUI binaries for
both products with the .NET RIDs `win-x64`, `win-arm64`, `linux-x64`,
`linux-arm64`, `osx-x64`, and `osx-arm64`.

Example asset names:

```text
ami-gotek-media-builder-cli-v1.1.1-win-x64.zip
ami-gotek-media-builder-gui-v1.1.1-win-x64.zip
ami-gotek-media-builder-demoscene-cli-v1.1.1-win-x64.zip
ami-gotek-media-builder-demoscene-gui-v1.1.1-win-x64.zip
```

The ZIP files include the .NET runtime. A target machine therefore does not
need a separate .NET installation.

## Security and scope

Input images are never modified, moved, or deleted. Paths are normalized and
checked for containment/symlinks; staging is isolated by a validated run ID;
export never writes directly to an SD card; public HTTP blocks private hosts,
unsafe redirects, oversized responses, and invalid image bytes; NFO output is
limited to 512 UTF-8 bytes; and the application ships no credentials.

Implemented: offline/online Core, CLI, catalog, ZIP scanning, TOSEC grouping,
Gotek export, NFO, SQLite metadata/artwork cache, public game providers, local
GameBase support, separate Pouët/Demozoo demoscene cataloging and downloads,
safe HTTP transport, and cross-platform Avalonia GUIs.

Out of scope: DMS/LHA conversion to ADF, OCR/PDF artwork extraction, manual
approval workflows, and complete compatibility with every feature of the
original Python application.

