# Release validation notes

## v2.1.0

The owner requested publication of v2.1.0 on 2026-11-14. The **Build and release** workflow publishes matching version tags only after compilation, dependency auditing, automated BIOS/timer checks, installer structure checks and hosted-runner install/repair/upgrade/downgrade/uninstall checks pass. It verifies the MSI inside the ZIP and supplies checksums and build provenance. See [GitHub Actions](https://github.com/dklasens/XPSManager/actions/workflows/release.yml) for the exact commit, results and installer logs; the release's `build-info.json` links its producing run.

Local Release builds, deterministic 24-hour ExpressCharge tests, rendered UI checks and MSI compilation/validation passed during implementation. Portable service removal was also verified on the owner's laptop. Live 24-hour charging, the remaining hardware sleep/restart cases, clean Windows 11 VM installation and failure/rollback scenarios remain unverified. The new charging monitor checks a persisted deadline every 30 seconds; earlier service-idle measurements do not cover it.

The release remains unsigned and has only been developed/tested on the XPS 14 DA14260. Publication does not imply independent security certification or validation of every scenario listed below. The older preparation record is retained as history; its draft-only workflow and publication status have been superseded by the v2.1.0 workflow and the owner's release request.

## v2.0.1 candidate preparation (historical)

This candidate adds a Windows x64 MSI that installs the app, Start menu shortcut, bundled .NET runtime and background service. Fresh installations leave automatic switching off. Existing settings survive MSI repair, upgrade and uninstall/reinstall. The service runs as LocalSystem and starts with Windows; BIOS changes still require administrator access.

**Status: candidate preparation, not approval for a stable public release.** The candidate is unsigned by the owner's decision. No claim of an independent security audit or universal Dell compatibility is made.

## Changes made during the review

- Removed the elevated on-disk attribute cache and diagnostic writes under user-writable LocalAppData. Firmware keys are now cached only in memory. First discovery per process takes about two seconds on the previously tested laptop.
- Added administrator/SYSTEM-only coordination of complete BIOS operations across the app, CLI and service. Manual changes re-read current state under the lock and preserve fields the user did not edit.
- Enforced firmware read-only attributes; added tests for threshold ordering, invalid settings, concurrent edits, partial failures and verification failures. BIOS writes are not transactional; the app re-reads after a failed write where possible.
- Checked the Intel frequency-domain enumeration result and returned count before accessing stack-allocated handles.
- Fixed a minimise-during-startup telemetry race and handled recoverable monitoring errors without presenting stale readings as live.
- Allowed automatic switching to retry after a WMI error on a later event even if the power mode is unchanged. No repeating retry or BIOS polling timer was added. A "No change" mapping avoids BIOS access.
- Added MSI lifecycle management, protected service/directory/registry ACLs, runtime/font notices, a Start menu shortcut and downgrade rejection. MSI-owned installations cannot be modified by the portable service updater/remover.
- Limited the release workflow's build job to read permissions, pinned actions to commit hashes, made NuGet audit warnings/failures block builds, added dependency-update checks and made tag builds create **draft releases only**.

## Verification and remaining gates

Local Release builds, hardware-independent checks, MSI compilation and MSI structure checks have been exercised during preparation. NuGet's direct/transitive advisory query reported no known vulnerable packages in the application and WiX project on 2026-11-12. This does not cover every runtime, firmware or driver defect.

Before publishing a stable release, record evidence for:

- [ ] A successful GitHub Actions run against the exact release commit. The workflow includes fresh installation, repair, upgrade from an older **fixture MSI**, downgrade rejection, uninstall, settings preservation and restricted-token access checks. Authoring these checks is not evidence that they passed.
- [ ] Install and uninstall the actual candidate in a clean Windows 11 VM without a preinstalled .NET runtime; verify Start menu launch, elevation, Apps uninstall and reboot behaviour. Hosted Windows Server checks do not replace this.
- [ ] Exercise installation failure/rollback, including a running app and locked files, in a disposable VM. Structural validation alone does not prove rollback.
- [ ] Validate migration from portable v2.0.0: record mappings, remove its background service using the downloaded app, install the MSI, restore mappings and enable automatic mode. Automatic adoption of an unmanaged service is intentionally blocked.
- [ ] On the supported XPS 14 DA14260, verify Energy saver on/off, rapid unplug/replug, sleep on AC then unplug/wake, and reboot startup without opening the app. Earlier v2 checks did not cover these transitions.
- [ ] Re-run the normal AC/DC, manual override, disable/re-enable and BIOS write/read-back checks against this candidate's coordinated operations. Earlier v2.0.0 evidence is not equivalent to testing the modified build.
- [ ] Review the candidate's default, unsupported-hardware, BIOS-password, corrupt-configuration and provider-unavailable UI states. No BIOS password handling or general multi-model certification is provided.
- [ ] Choose the project's distribution/source licence and a private vulnerability-reporting route. Third-party notices are included; no project-wide licence has been selected by this change.

Code signing remains outstanding. The owner authorised an unsigned candidate; publishing it as unsigned should remain an explicit release decision. SHA-256 checksums detect changed bytes relative to a trusted checksum, but do not establish publisher identity.

## Runtime and service maintenance

The MSI is self-contained: .NET is installed beside the app under Program Files and no runtime is downloaded during installation. Bundled runtimes are **not patched by updating the machine's shared .NET runtime**. Rebuild and release an updated MSI when .NET security fixes apply. Keep the SDK, WiX tools, Windows, Dell BIOS and Intel drivers maintained.

MSI service recovery uses two restart attempts, 30 seconds apart, then stops retrying until the failure count resets after one day. The service is automatic-start, not delayed-start. Configuration is off by default, so installing the candidate on a machine without Dell's provider does not request BIOS changes.

Uninstall removes the app/service/shortcut/event source, keeps automatic settings for reinstall, and leaves the current firmware settings as they are. Disable automatic mode before uninstall if a later reinstall should stay disabled. A full settings reset can be performed by an administrator after uninstall by deleting only `HKLM\SOFTWARE\XpsManager\AutoSwitch`.

## Building and releasing

```powershell
dotnet run --project tools/TelemetryChecks -c Release
./tools/Build-Installer.ps1 -Version 2.0.1
./tools/Test-InstallerPackage.ps1 -Msi installer/bin/Release/XpsManager-2.0.1-win-x64.msi
```

WiX 7's OSMF terms were explicitly accepted by the owner for this project on 2026-11-12. Builders must independently satisfy the applicable terms; `AcceptEula` is recorded in the installer project.

Run **Release candidate** manually in GitHub Actions for a branch build. It uploads the unsigned MSI, checksum, build commit/run metadata and this readiness record only after checks pass. A matching `v2.0.1` tag additionally creates a draft release; it does not publish automatically. Update the checklist with evidence before promoting the draft. No tag has been created by this preparation.
