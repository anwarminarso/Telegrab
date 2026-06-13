# Telegrab

**Telegrab** is a Windows desktop application for downloading, archiving, and documenting media
from Telegram groups, supergroups, and channels. It gives you a **self-contained, portable
backup** of media and conversations that can otherwise disappear when chats or topics are
deleted, or when a group is removed entirely.

> Built with .NET MAUI. Read-only against Telegram — Telegrab never sends or modifies anything
> in your account.

---

## Why Telegrab

Telegram groups sometimes delete specific chats or topics, and entire groups can vanish.
Telegrab lets you preserve the media you care about along with human-readable documentation, so
your archive stays intact and portable even when the original source is gone.

---

## Features

### Bulk and per-file downloads
- Download media (photos, videos, files) from any group, supergroup, channel, or forum topic.
- **Download all** loaded media in one batch, or **download a single file** on demand.
- Files are saved into a clean, organized folder structure:
  `{root}/{Chat}/{Topic?}/{date}_{messageId}_{mediaId}_{fileName}`.

### Strict, portable download root
- A single **download root** workspace managed by the app, containing a `telegrab.db` manifest
  (SQLite) and all downloaded subfolders.
- Media paths are stored **relative** to the root, so you can move the entire folder (with its
  database) to another location or machine without breaking references.
- If the root is not configured or becomes invalid, all download operations are blocked with a
  clear prompt to open Configuration.

### Smart caching and resume
- Download state is tracked in `telegrab.db`. Already-downloaded media is skipped automatically
  and reloaded from disk as a local cache.
- Pointing the app at a previously used root restores the full download history — **no migration
  required**.

### Media preview
- Built-in viewer for photos and videos.
- **Zoom and pan** (mouse wheel follows the cursor, click-and-drag to pan), 1x–8x.
- **Filmstrip** for quick navigation between media in a session.
- Swipe / click-zone navigation, auto-hiding chrome, and keyboard support.

### Chat documentation as Markdown
- Generates a `README.md` per folder from the SQLite manifest as a readable backup of the
  conversation.
- Media is grouped per post (albums merged), with captions, caption-source labels, sender,
  local-time date, and file size.
- Generated content is wrapped between `<!-- TELEGRAB:BEGIN -->` and `<!-- TELEGRAB:END -->`
  markers; any text you add outside the markers is preserved on regeneration.
- **Preview in-app** (Markdig → HTML in a WebView), **edit** with live preview, and open in any
  **external Markdown viewer** — relative media links remain valid.

### Caption association
Telegrab attaches descriptions to media as accurately as possible and is transparent about the
source: `own`, `album`, `reply`, `inferred` (adjacency heuristic, same sender, within 60s), or
`none`. See `.kiro/specs/storage-and-documentation/requirements.md` (Requirement 7 and
Appendix A) for the full case catalog.

---

## Tech Stack

| Area | Technology |
|------|------------|
| Framework | .NET 10, .NET MAUI (MVVM) |
| Target | `net10.0-windows10.0.19041.0` (Windows, unpackaged) |
| Telegram client | [WTelegramClient](https://github.com/wiz0u/WTelegramClient) |
| Storage | SQLite via `Microsoft.Data.Sqlite` (WAL mode) |
| Markdown | [Markdig](https://github.com/xoofx/markdig) |
| MVVM / UI | CommunityToolkit.Mvvm, CommunityToolkit.Maui (+ MediaElement) |
| Icons | MauiIcons.Material |

Core domain logic (`ManifestDbService`, `DocumentationRenderer`, `CaptionResolver`,
`RootValidator`) is intentionally **free of MAUI/WTelegram dependencies** so it can be unit
tested in isolation.

---

## Getting Started

### Prerequisites
- Windows 10 (build 17763) or later.
- [.NET 10 SDK](https://dotnet.microsoft.com/) with the MAUI workload:
  ```cmd
  dotnet workload install maui
  ```
- A Telegram **api_id** and **api_hash** from <https://my.telegram.org/apps>.

### Build and run
```cmd
dotnet restore src\Telegrab\Telegrab.csproj
dotnet build   src\Telegrab\Telegrab.csproj -f net10.0-windows10.0.19041.0
dotnet run --project src\Telegrab\Telegrab.csproj -f net10.0-windows10.0.19041.0
```

### First-time setup
1. Launch Telegrab and sign in with your `api_id`, `api_hash`, and phone number. A login code is
   sent to your **Telegram app** (not via SMS); enter it, then your 2FA password if enabled.
2. When prompted, choose a **download root** folder. This is where media and the `telegrab.db`
   manifest are stored.
3. Select a chat or topic, then download individual media or use **Download all** for the loaded
   messages.

---

## How It Works

```
Telegram (WTelegramClient)
        │  read messages / media
        ▼
TelegramService ──► DownloadQueueService ──► files on disk + telegrab.db (manifest)
                              │
                              ▼
                    DocumentationService ──► README.md (per folder, marker-wrapped)
```

- Downloads run **sequentially** through a channel to stay friendly to Telegram rate limits,
  with `FLOOD_WAIT` handling and per-media locking.
- The manifest is the single source of truth; `README.md` files are regenerated projections.
- Changing the download root closes the old database and opens the one in the new root — each
  root is fully self-contained.

---

## Project Structure

```
Telegrab/
├─ src/Telegrab/            # MAUI application
│  ├─ Models/               # POCO/DTOs
│  ├─ Services/             # Telegram, download queue, SQLite manifest, documentation
│  ├─ ViewModels/           # MVVM view models
│  ├─ Views/                # Pages and modals (login, config, viewers)
│  └─ Platforms/            # Platform-specific code (Windows active)
├─ tests/                   # Test project
├─ Audit.md                 # Technical code audit and hardening backlog
└─ Planning.md              # Goal-vs-implementation gap analysis and roadmap
```

See `.kiro/steering/` for the product overview, tech stack, and structure conventions.

---

## Roadmap

Telegrab covers all core goals today. Active areas of work are tracked in `Planning.md` and
`Audit.md`, including:
- A true "download entire chat/topic" that walks the full history (current "Download all" covers
  loaded messages only).
- Folder schema hardening (`{id}_{title}`) to prevent collisions between identically named chats.
- Richer sender resolution in documentation.
- A single, consistent English UI.

---

## Privacy and Safety

- **Read-only**: Telegrab only reads from Telegram (chats, topics, messages, media).
- Your password is never stored. The login session is handled by WTelegram in an encrypted
  `session.dat` file in the app data folder.
- Downloaded files with potentially executable extensions are revealed in Explorer rather than
  launched automatically.

---

## Credits

- **Author:** Anwar Minarso
- **Company:** a2n Technology

Built on the excellent [WTelegramClient](https://github.com/wiz0u/WTelegramClient),
[Markdig](https://github.com/xoofx/markdig), and the .NET MAUI Community Toolkit.
