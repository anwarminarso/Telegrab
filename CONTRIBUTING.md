# Contributing to Telegrab

Thanks for your interest in improving Telegrab. This document explains how to set up your
environment, the conventions we follow, and how to propose changes.

## Code of Conduct

By participating in this project you agree to abide by our
[Code of Conduct](./CODE_OF_CONDUCT.md). Please report unacceptable behavior as described there.

## Before You Start

- Check [`Planning.md`](./Planning.md) for the current roadmap and known gaps, and
  [`Audit.md`](./Audit.md) for the technical hardening backlog. Many high-value tasks are
  already scoped there.
- For non-trivial changes, open an issue first to discuss the approach before investing time.
- Project conventions live in `.kiro/steering/` (product, tech stack, and structure).

## Development Environment

- Windows 10 (build 17763) or later.
- [.NET 10 SDK](https://dotnet.microsoft.com/) with the MAUI workload:
  ```cmd
  dotnet workload install maui
  ```
- A Telegram `api_id` / `api_hash` from <https://my.telegram.org/apps> for runtime testing.

### Build and test
```cmd
dotnet restore src\Telegrab\Telegrab.csproj
dotnet build   src\Telegrab\Telegrab.csproj -f net10.0-windows10.0.19041.0
dotnet test
```

Run the application manually from your own terminal (do not rely on automated tooling to launch
long-running processes):
```cmd
dotnet run --project src\Telegrab\Telegrab.csproj -f net10.0-windows10.0.19041.0
```

## Coding Conventions

These are enforced by review; please follow them to keep changes consistent.

- **Architecture:** MVVM. Keep Models, Services, ViewModels, and Views separated.
- **Pure logic vs MAUI:** `ManifestDbService`, `DocumentationRenderer`, `CaptionResolver`,
  `RootValidator`, `DownloadRootState`, and `MessageWindow` must stay free of MAUI/WTelegram
  dependencies so they remain unit-testable. Do not add UI references to these files.
- **Language:**
  - User-facing UI text (buttons, labels, messages, dialogs) is written in **English**.
  - Code comments and internal documents (steering, specs) are written in **Indonesian**, to
    match the existing codebase style.
- **Dependency injection:** Register new services in `MauiProgram.cs` (singletons for services,
  transient for modal pages/view models).
- **Documentation markers:** Never write outside the `<!-- TELEGRAB:BEGIN/END -->` markers
  programmatically; content outside the markers belongs to the user.
- **Storage:** Store media paths relative to the download root using `/` separators. Store dates
  as UTC in the database and display local/current-culture time in the UI and README.
- **Nullable reference types** are enabled; resolve warnings rather than suppressing them.

## Making Changes

1. Fork the repository and create a feature branch:
   ```cmd
   git checkout -b feature/short-description
   ```
2. Keep commits focused and write clear messages (imperative mood, e.g. "Add full-chat download").
3. Add or update tests:
   - New behavior in pure-logic services must come with unit tests.
   - Changes to `DownloadQueueService`, `TelegramService`, or `DbLifecycleCoordinator` should
     include behavior tests, as these areas are the most regression-prone.
4. Ensure `dotnet build` is clean and all tests pass. Clean up any temporary files created during
   testing.
5. Do not commit secrets. Never commit `appsettings.json` with real credentials, `session.dat`,
   or any populated `telegrab.db`.

## Submitting a Pull Request

- Push your branch and open a pull request against the default branch.
- Describe **what** changed, **why**, and **how you tested it**. Reference any related issue.
- Note any user-visible behavior changes and any tradeoffs.
- Keep PRs reasonably small and single-purpose; large, mixed PRs are hard to review.

By contributing, you agree that your contributions are licensed under the project's
[MIT License](./LICENSE).

## Reporting Security Issues

Please do not open public issues for security vulnerabilities. Follow the process in
[`SECURITY.md`](./SECURITY.md).
