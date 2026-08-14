# Lingarr Agent Guide

## Toolchain And Setup

- Use .NET 10 (`global.json` pins `10.0.0` with latest-minor roll-forward) and Node 24 (CI and Docker).
- `Lingarr.Client/` and `Lingarr.Docs/` are independent npm projects; run `npm ci` in each directory, not at the repository root.
- Enable the repository hook with `git config core.hooksPath .githooks`. It expects a running `Lingarr.Client` container, runs client lint there, then runs the full .NET test suite locally.

## Verification

Run the backend CI sequence from the repository root:

```bash
dotnet restore Lingarr.slnx
dotnet build Lingarr.slnx --no-restore --configuration Release /p:TreatWarningsAsErrors=false
dotnet test Lingarr.slnx --no-build --configuration Release --verbosity normal --filter "Category!=Integration"
```

- `dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj --filter "FullyQualifiedName~LocalAiServiceTests"` runs a focused backend test class.
- `dotnet test Lingarr.Migrations.Tests/Lingarr.Migrations.Tests.csproj` exercises SQLite, MySQL, and PostgreSQL; MySQL/PostgreSQL use Testcontainers, so Docker must be available.
- In `Lingarr.Client/`, run `npm run lint` and `npm run build`; the build already runs `vue-tsc --noEmit`. `npm run format` rewrites `src/`. There is no frontend test script.
- In `Lingarr.Docs/`, run `npm run build` after documentation changes.

## Runtime And Boundaries

- `Lingarr.Server/Program.cs` delegates DI/database/Hangfire/plugin wiring to `Extensions/ServiceCollectionExtensions.cs` and middleware/startup migration wiring to `Extensions/ApplicationBuilderExtensions.cs`.
- `Lingarr.Core/` owns entities, `LingarrDbContext`, database configuration, and `SettingKeys`; `Lingarr.Contracts/` is the public dependency boundary for external plugins.
- `Lingarr.Migrations/` owns schema and seed changes. `Lingarr.Client/` is the Vue 3/Pinia/Tailwind SPA; `Lingarr.Docs/` is a separate VitePress site.
- The production Dockerfile builds the client and copies `dist/` into `Lingarr.Server/wwwroot`. In development, Vite serves port `9876` and proxies `/api` and `/signalr` to `VITE_BASE_SERVER_URL` (default `Lingarr.Server:9876`); Compose exposes the backend as host port `9877`.
- `DB_CONNECTION` is required at server startup and accepts `mysql`, `postgres`, `postgresql`, or `sqlite`. Migrations run automatically before controllers are mapped.
- `docker-compose.dev.yml` runs two services (`Lingarr.Server` with SQLite, `Lingarr.Client` via Vite); start with `just up` or `docker compose -f docker-compose.dev.yml up -d --build`. Client source hot-reloads, but backend changes require rebuilding `Lingarr.Server` (`just rebuild`).

## Change Traps

- Settings are database rows, not `appsettings` values. For a built-in setting, add its constant in `Lingarr.Core/Configuration/SettingKeys.cs` and seed/remove it in a migration. If it is environment-configurable, also update `StartupService.ApplySettingsFromEnvironment`; if changing it has runtime effects, update `SettingChangedListener`.
- Use FluentMigrator, not EF migrations. Add `Lingarr.Migrations/Migrations/M{NNNN}_{Name}.cs` with the next unique `[Migration(N)]`, support all three databases (use `IfDatabase` when needed), and implement a real `Down()`.
- Built-in translation providers need the service/factory case, a manifest registered in `ServiceCollectionExtensions`, and settings migration/constants. External plugins reference only `Lingarr.Contracts`, require plugin API major version `1`, and are loaded from `PLUGINS_PATH` only at startup; `samples/CloudflarePlugin/` is the reference.
- NuGet versions belong in `Directory.Packages.props`, not individual project files.
- Preserve Conventional Commit branch/commit naming (`feat/...`, `fix/...`) and never add AI co-author tags; project policy rejects them.
