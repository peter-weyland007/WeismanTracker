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
