# WeismanTracker

WeismanTracker is a new .NET 11 Blazor application scaffolded with:

- **Frontend:** Blazor Web App + MudBlazor
- **Backend:** ASP.NET Core Web API
- **Database:** SQLite (used in both development and production)

## Solution layout

- `WeismanTracker.slnx`
- `apps/web` — Blazor UI
- `apps/api` — API + EF Core SQLite

## Current scaffold status

- MudBlazor package and providers are wired into the web app.
- API is configured with EF Core SQLite (`data/weismantracker.db`).
- Database bootstraps automatically with `EnsureCreated()` at startup.
- CAT ET UI pages are available in the web app:
  - `/catet/people`
  - `/catet/computers`
  - `/catet/licenses`
- Baseline API endpoints available:
  - `GET /api/health`
  - `POST /api/auth/login` (seed default user: `admin` / `admin`)
  - `GET /api/users`
  - `GET/POST /api/catet/people`
  - `GET/POST /api/catet/computers`
  - `GET/POST /api/catet/licenses`

## Recent release notes

### 2026-04-06 — Integrations + scalable asset tables

- Added **Integration Settings** UI for Ninja + Microsoft configuration and sync controls.
- Added background **resource sync pipeline** with per-source status breakdown:
  - Graph Users
  - Graph Devices
  - Intune Managed Devices
  - Azure Virtual Machines
- Added entity/resource reference model so each person/computer can track which external resources exist and which are linked.
- Added **auto-create on sync** behavior for missing local entities:
  - Microsoft users create `TrackedPeople`
  - Microsoft/Intune/Ninja devices create `TrackedComputers`
- Added sync status persistence and API surface for run status, counts, and per-source detail.
- Upgraded CAT ET People/Computers to **server-side pagination**.
- Added People/Computers **search, sort, and filters**:
  - People: sort by name/email/created, filter with/without email
  - Computers: sort by hostname/asset/assignee/created, filter assigned/unassigned

### 2026-04-21 — Printer telemetry ingest

- Added `POST /api/printers/telemetry` for collector pushes.
- Added `GET /api/printers` for printer telemetry display in the web app.
- Added live `/assets/printers` table for status, toner, usage, and alert visibility.
- Added collector contract doc: `docs/printer-collector-contract.md`.
- Added example collector-side POST helper: `scripts/post_printer_telemetry.py`.

## Build

```bash
dotnet build WeismanTracker.slnx
```

## Run API

```bash
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://*:5199 dotnet run --project apps/api/api.csproj --no-launch-profile
```

## Run Web

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://*:5188 dotnet run --project apps/web/web.csproj --no-launch-profile
```
