# TarkovMonitor (Fork von the-hideout/TarkovMonitor)

.NET 10 / WinForms + Blazor Hybrid (MudBlazor). Projekt: `TarkovMonitor/TarkovMonitor.csproj`, Ziel `net10.0-windows10.0.17763.0`.

## Befehle
- Build: `dotnet build TarkovMonitor/TarkovMonitor.csproj -c Debug`
- Starten: `dotnet run --project TarkovMonitor/TarkovMonitor.csproj`
- Release wie CI: `dotnet publish TarkovMonitor/TarkovMonitor.csproj -c Release --self-contained --runtime win-x64 -p:PublishSingleFile=true --output publish`
- Keine Tests im Repo; CI (`.github/workflows/build-dev.yml`) baut nur.

## Git-Workflow
- `origin` = Scholle1309/TarkovMonitor (Fork), `upstream` = the-hideout/TarkovMonitor.
- Features auf Branches entwickeln, nach `origin` pushen; `master` mit `git pull upstream master` aktuell halten.
- Repo liegt auf Netzlaufwerk Z: (UNC), safe.directory ist global eingetragen.
