# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Lingarr is a self-hosted app that auto-translates subtitle files using pluggable translation services (LibreTranslate, DeepL, OpenAI/Anthropic/Gemini/etc., Google/Bing/Yandex). It syncs media from Radarr/Sonarr, runs translation jobs via Hangfire, and serves a Vue SPA. .NET 10 backend, Vue 3 + TypeScript frontend, licensed AGPL-3.0.

## Commands

```bash
# Backend tests (xunit.v3; Migrations.Tests uses Testcontainers → needs Docker)
dotnet test Lingarr.slnx
dotnet test Lingarr.Server.Tests                       # one project
dotnet test Lingarr.Server.Tests --filter "FullyQualifiedName~SomeTestName"  # one test

# Frontend (in Lingarr.Client/)
npm run lint      # oxlint
npm run format    # oxfmt
npm run build     # vue-tsc type check + vite build

# Full dev environment (backend, frontend, DBs, LibreTranslate, Sonarr/Radarr)
docker-compose -f docker-compose.dev.yml up -d
```

Dev URLs: frontend http://localhost:9876 (hot reload), backend/Swagger http://localhost:9877/swagger, Hangfire dashboard http://localhost:9877/hangfire. The frontend proxies `/api` and `/signalr` to the backend (vite.config.ts). The backend runs in Docker and must be rebuilt after changes; the Vite client hot-reloads.

Pre-commit hooks (`git config core.hooksPath .githooks`) run `docker exec Lingarr.Client npm run lint` + `dotnet test Lingarr.slnx`.

Commits and branch names follow Conventional Commits (`feat/...`, `fix/...`). Do not add AI co-author tags to commits — PRs containing them are rejected.

## Architecture

Projects (Lingarr.slnx):
- **Lingarr.Core** — entities, `LingarrDbContext` (EF Core), enums, `SettingKeys` constants. `BaseEntity` gives every entity `CreatedAt`/`UpdatedAt`, stamped in `SaveChangesAsync`. For PostgreSQL, all DateTimes are converted to UTC `timestamp without time zone` via value converters in the DbContext.
- **Lingarr.Contracts** — plugin-facing contracts (`ITranslationService`, manifests, settings access). Plugins reference only this project.
- **Lingarr.Server** — ASP.NET Core app: Controllers (REST API), Services, Hangfire Jobs, SignalR Hubs. DI wiring lives in `Extensions/ServiceCollectionExtensions.cs`; startup logic in `Services/StartupService.cs`.
- **Lingarr.Migrations** — FluentMigrator migrations shared by SQLite/MySQL/PostgreSQL (selected via `DB_CONNECTION` env var). Applied automatically at startup. New migrations go in `Migrations/M{NNNN}_{Name}.cs` with a unique sequential `[Migration(N)]` number and a working `Down()`.
- **Lingarr.Client** — Vue 3 + TypeScript + Pinia + Tailwind 4. `@` aliases `src/`. SignalR client for live progress.
- **samples/CloudflarePlugin** — reference translation plugin.

Key flows:
- **Settings live in the database** (`Setting` entity), not appsettings. Read/write through `ISettingService`; env vars can seed them. `Listener/SettingChangedListener.cs` reacts to setting changes (e.g. reschedules Hangfire jobs, reinitializes integrations) and broadcasts via the `SettingUpdatesHub`. New setting keys are constants in `Lingarr.Core/Configuration/SettingKeys`.
- **Translation services** are created by `Services/Translation/TranslationFactory.cs` from the service-type string. AI providers share `IRequestTemplateService` for prompts and may implement `IBatchTranslationService`. Adding a provider means a new service class + a case in the factory (plugins instead register as keyed `ITranslationService` and are picked up by the factory's fallback).
- **Plugins**: `PluginLoader` loads DLLs from `PLUGINS_PATH` at startup and registers each as a keyed `ITranslationService` by manifest name.
- **Jobs** (`Jobs/`) run on Hangfire: Radarr/Sonarr sync, automated + manual translation, cleanup, statistics. Concurrency capped by `MAX_CONCURRENT_JOBS`. Progress streams to the client over SignalR hubs (`Hubs/`).
- **Media sync**: `Integration` services pull movies/shows from Radarr/Sonarr; `PathConversionService`/`PathMapping` translate their paths to Lingarr's mounted volumes.

## Conventions

- Backend: async/await throughout, XML doc comments on public APIs, DI for everything.
- Frontend: TypeScript, Tailwind for styling, follow the existing component structure under `src/components`/`src/pages`; state in Pinia stores (`src/store`), API calls in `src/services`.
- NuGet package versions are centralized in `Directory.Packages.props`.
