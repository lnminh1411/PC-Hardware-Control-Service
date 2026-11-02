# Scripts

- `scripts/publish.ps1`: runs `dotnet publish` for `src/PcHwCtl.Service` (Release, win-x64, framework-dependent) into `publish/`. Run it with no arguments, or let `install.ps1` call it.
- `scripts/install.ps1`: elevated. Publishes unless `-SkipPublish`, stops the service and agents, copies `publish\*` to `C:\Program Files\PcHwCtl`, creates or updates service `PcHwCtl`, then starts it. Keeps the deployed config unless `-ForceConfig`; `-DisableFanControl` and `-DisableOpenRGB` turn off those apps.
- `scripts/uninstall.ps1`: elevated. Stops and deletes service `PcHwCtl` and removes the install. `-RestoreFanControl` and `-RestoreOpenRGB` undo what the install disabled; `-RemoveData` also removes the data directory.
