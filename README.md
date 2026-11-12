# XPS Manager v2

A small Windows app for the **Dell XPS 14 (DA14260)** that lets you change the laptop's power and battery settings from Windows, and see what the laptop is doing right now. You don't need to restart into the BIOS or install Dell's larger utilities.

Open it, change a setting, close it. In v2, optional automatic thermal switching keeps working in the background.

## New in v2

- Follow Windows power mode automatically, with editable thermal mappings and manual overrides.
- Keep switching after closing the app through an optional Windows service with automatic startup and crash recovery.
- View battery serial and firmware-reported PPID in Activity.
- Manage service installation, updates and removal from Settings, with protected configuration and restricted service permissions.

## What it does

**Change settings**
- **Thermal mode:** Optimized, Cool, Quiet or Ultra Performance. This sets how hard the laptop is allowed to run and how loud the fans may get.
- **Automatic thermal switching:** follow Windows power mode, with editable mappings and a background Windows service.
- **Battery charging:** Adaptive, Standard, Express, Primarily AC, or Custom. Custom sets your own start and stop percentages; for example, stopping at 80% helps the battery last longer.

**See what's happening**, updated every second:
- Battery level and how long it will last at the current rate of use
- How much power the battery, the processor and the graphics are using
- Processor and graphics speeds, and whether they're being held back by heat or power limits
- The Windows power mode, the screen's refresh rate and brightness
- Battery health, charge cycles, serial number and firmware-reported PPID (when available)

The **Activity** tab has the full breakdown. The **Settings** tab is what opens by default.

## How it works

Dell builds a settings interface into the laptop's firmware (the BIOS) that Windows programs can talk to. XPS Manager uses it to read your current settings and write the ones you choose. After each change it reads the setting back to make sure it took effect. These are the same settings you'd find by restarting into the BIOS setup screen.

The live readings come from sources already in Windows: the battery, Windows performance counters, and Intel's graphics driver. Reading them never changes anything.

Because it changes BIOS settings, the app needs administrator access. Windows asks once when it opens.

## Download

Get **XpsManager.exe** (or the .zip with documentation) from the [latest release](https://github.com/dklasens/XPSManager/releases/latest).

- Needs Windows 11 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
- The app isn't code-signed, so Windows may show a SmartScreen warning the first time. Choose **More info**, then **Run anyway**.

## Getting started

1. Download and open **XpsManager.exe**. Approve the Windows administrator prompt.
2. Use the thermal and charging controls, then choose **Write to BIOS** for manual changes.
3. For automatic switching, turn off Dell Optimizer's thermal / Windows power-mode sync, then enable **Settings → Automatic → Follow Windows power mode**.
4. Set Windows to **Best power efficiency** on battery and **Best performance** while plugged in if you want Quiet on battery and Ultra Performance on AC.

Enabling automatic mode handles service installation and startup. **No login task, startup shortcut or open app window is needed.** The service is configured for delayed automatic startup with Windows, including before logon. Leaving automatic mode off installs no service.

## Automatic mode

In **Settings → Automatic**, enable **Follow Windows power mode**. The published exe installs a protected copy at `C:\Program Files\XPS Manager\XpsManager.exe` and registers `XpsManagerService`, running as LocalSystem with delayed automatic startup and crash recovery. It works with the app closed and before logon. The app still asks for administrator access.

| Windows mode | Default thermal mode |
|---|---|
| Energy saver / Best power efficiency | Quiet |
| Balanced / legacy Better performance | Optimized |
| Best performance | Ultra Performance |
| Custom / unavailable | No change |

Edit the three mapping rows and choose **Save automatic settings**. Energy saver shares the efficiency row; Better performance shares Balanced. **No change** leaves the BIOS alone. Windows keeps separate power modes for battery and AC: choose Best power efficiency on battery and Best performance while plugged in to get Quiet / Ultra Performance switching.

Changes are one-way: XPS Manager never changes the Windows power mode. Notifications settle for two seconds before the service reads, compares and writes the thermal setting. A manual thermal choice lasts until the next real mode/source change, resume, service restart or saved configuration; transient game-mode notifications do not cancel it. There is no periodic BIOS write timer.

**Turn off Dell Optimizer's thermal / Windows power-mode synchronization before enabling this feature.** Its reverse sync can conflict with XPS Manager. Dolby Vision remains in Dolby Vision Access. Firmware Adaptive charging is available; Dell's learned Dynamic Charge is not reproduced.

Turning the switch off leaves the service installed and idle. **Remove background service** stops and removes the service, installed executable, event source and configuration. Use your downloaded copy for removal, rather than opening the exe inside Program Files. When a newer downloaded exe is opened, **Update installed service** appears in Automatic.

Settings and last-result status live under `HKLM\SOFTWARE\XpsManager\AutoSwitch`, writable only by administrators and SYSTEM. Application events use the **XPS Manager** source. If installation or a BIOS operation fails, the Automatic status explains it; save again to retry.

Elevated CLI: `xpsm auto status`, `xpsm auto on`, `xpsm auto off`. Install first through the published app. For deployment, the published app also accepts `--install-service`, `--update-service` and `--remove-service`; exit code 1 indicates failure, with details in `%LOCALAPPDATA%\XpsManager\service-operation-error.txt`.

## Performance and security

The service responds to Windows power notifications; it does not continuously poll the BIOS. A notification schedules one check after a two-second settling period. It only writes when a supported thermal value differs from the current setting. There is no network telemetry or remote-control interface.

While the app is visible, live readings refresh every second and service status every three seconds. Both pause when minimised and stop when the app closes. The service runs independently. Measured idle service use, access-control checks and limitations are documented in the [security review](docs/SECURITY.md); this is not a guarantee of zero overhead or zero security risk.

## Hardware support and validation

- It's built and tested on the XPS 14 DA14260 (BIOS 1.11.0). Other Dell laptops may work if their BIOS offers the same settings, but they haven't been tested.
- It assumes no BIOS administrator password is set.
- Temperatures and fan speed aren't shown: this laptop doesn't report them to Windows.

Verified on the target laptop: AC/battery switching, a Windows power-mode change, manual override, disable/re-enable, invalid mappings, service crash recovery, removal/reinstallation/update, and battery identity reads. **Energy saver on/off, rapid replug, sleep/wake, reboot startup and the newer-version update offer still need their remaining hardware checks.** See the [v2 validation record](docs/V2-VALIDATION.md).

## For developers

Security boundaries, polling intervals and measured idle resource use: [docs/SECURITY.md](docs/SECURITY.md).

Build, project layout, and how each reading is measured: see [docs/TECHNICAL.md](docs/TECHNICAL.md).

```powershell
dotnet build
dotnet run --project tools/TelemetryChecks   # checks
dotnet publish src/XpsManager.App -p:PublishProfile=SingleFile   # dist\XpsManager.exe
```

Releases are built by GitHub Actions: pushing a tag such as `v2.0.0` runs the hardware-independent checks, publishes the single exe, packages the documentation, and attaches SHA-256 checksums to the release. The CLI is available from source with `dotnet build src/XpsManager.Cli`; it is not a separate release download.
