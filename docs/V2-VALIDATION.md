# XPS Manager v2 validation

This is the historical v2.0.0 validation record. The v2.0.1 MSI and changed BIOS coordination require their own regression and installer checks; see [release readiness](RELEASE-READINESS.md). Do not treat the earlier results below as validation of the new installer.

Validation date: 2026-11-12. Hardware: Dell XPS 14 DA14260. The user confirmed Dell Optimizer thermal synchronization was disabled before live checks.

## Evidence collected

- Release build and single-file publish succeed; telemetry, thermal policy and trailing-edge debounce checks pass without Dell hardware dependencies.
- Automatic section rendered with fixture data for visual inspection. Existing Cobalt tokens, embedded fonts, keyboard focus and inline operation status are retained.
- A temporary SYSTEM scheduled task successfully read the Windows overlay and Dell thermal attribute. Session 0 returned the same efficiency overlay as the desktop. BIOS write returned status 0.
- This laptop's BIOS advertises **UltraPerformance**, unlike the plan's assumed **Performance**. Both firmware spellings are supported, and the actual advertised value is validated before writing.
- The service is installed as LocalSystem, delayed automatic start, using the quoted Program Files executable path. Recovery is 5 seconds then 30 seconds, reset daily. Administrators/SYSTEM have write access; ordinary users have read/execute access to the directory and read access to configuration.
- First plug/unplug test found efficiency mode on both sources: correctly remained Quiet with no write.
- User changed AC mode to Best performance: observed `ded574b5-45a0-4f42-8737-46345c09c238` and `UltraPerformance`; unplug returned to efficiency overlay `961cc777-2547-4f9d-8174-7d86181b8a7a` and Quiet. Service status reported the unplugged trigger without an error.
- Battery IOCTL serial query works on this machine. A bounded elevated firmware check found a Dell PPID string in SMBIOS type 22. The implementation accepts one matching string in one Dell battery record; missing, malformed or ambiguous records show unavailable. Parser checks cover truncation, wrong record type, non-Dell and multiple-battery cases.
- Live service checks passed: disabling leaves a manual Cool selection untouched; re-enabling applies the current mapping; a manual override persists without a mode change; invalid mappings are logged/skipped and a corrected configuration clears the error; killing the service triggers SCM recovery; removal cleans the service, executable, directory, event source and registry; installation is repeatable.
- The initial update check exposed SCM's stopped-before-image-release timing. Replacement now retries briefly while retaining the old binary on failure. The rerun passed (exit 0); service running and SHA-256 hashes of the published and installed executables matched.
- Final elevated end-to-end run passed every service check, including update, and exited 0. Live telemetry verified both serial and PPID. The test runner now catches failed assertions and exits 1 with console diagnostics; the earlier unhandled update assertion caused a Windows test-helper crash dialog, not an XPS Manager service crash.

Local evidence is kept in ignored `artifacts/`: `system-verification.txt`, `power-transitions.txt`, `service-checks-final.txt`, `service-checks-exit.txt`, `final-live-telemetry.txt`, and rendered panel images. These may contain machine identifiers; they are not release assets.

## Reproducible checks

```powershell
dotnet run --project tools/TelemetryChecks -c Release
dotnet run --project tools/TelemetryChecks -c Release -- --render
dotnet publish src/XpsManager.App -p:PublishProfile=SingleFile
# Elevated, explicitly opt-in; changes thermal settings and exercises service lifecycle:
tools/TelemetryChecks/bin/Release/net10.0-windows/TelemetryChecks.exe --service-checks dist/XpsManager.exe
# Elevated SYSTEM probe; restores the original thermal mode:
tools/Test-SystemAccess.ps1 -WriteThermal
```

The service checks require the service to be installed and no concurrent power-mode changes. They restore the saved configuration in `finally`. Do not run them on CI or while editing power settings.

## Release status and remaining checks

On 2026-11-12, the owner requested publication of v2 through GitHub Actions with the remaining checks deferred. Energy saver on/off, quick unplug/replug, sleep on AC followed by unplug and wake, and reboot without opening the app remain unverified. The newer-version update offer also needs visual confirmation with an older installed version; same-version replacement was exercised successfully. These limits are disclosed in the README and release notes. An installed service and passing policy tests do not prove those transitions. Dell Optimizer removal and Dolby Vision verification remain separate owner decisions.
