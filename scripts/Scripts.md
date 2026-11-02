# Scripts

- `scripts/publish.ps1`: runs `dotnet publish` for `src/PcHwCtl.Service` (Release, win-x64, framework-dependent) into `publish/`. Run it with no arguments, or let `install.ps1` call it.
- `scripts/install.ps1`: elevated. Refuses a source tree others can write, publishes into an empty `publish\` unless `-SkipPublish`, stops the service and agents, replaces `C:\Program Files\PcHwCtl` with `publish\*`, secures both directories, creates or updates service `PcHwCtl`, then starts it. Keeps the deployed config unless `-ForceConfig`; stops on non-admin-owned data files unless `-TrustExistingData`; `-DisableFanControl` and `-DisableOpenRGB` turn off those apps.
- `scripts/uninstall.ps1`: elevated. Stops and deletes service `PcHwCtl` and removes the install. `-RestoreFanControl` and `-RestoreOpenRGB` undo what the install disabled; `-RemoveData` also removes the data directory.
- `scripts/acl.ps1`: dot-sourced by install and uninstall, not run on its own. Junction checks, non-admin writer and owner checks, `Set-AdminOnlyAcl`, and junction-safe deletes.
