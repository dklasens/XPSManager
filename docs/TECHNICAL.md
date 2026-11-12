# XPS Manager: technical notes

Windows app and optional background service for changing Dell XPS BIOS power settings, using Dell's BIOS WMI interface (`root\dcim\sysman\biosattributes`). Tested on an XPS 14 DA14260 with BIOS 1.11.0.

Settings it manages:
- **Thermal mode**: Optimized, Cool, Quiet or Ultra Performance
- **Battery charging**: Adaptive, Standard, ExpressCharge, Primarily AC use, or Custom with start/stop thresholds

## Live readings

The readout band at the top shows what's in effect and what the machine is doing, updated every second while the window is open (sampling pauses while minimised):

- **Battery** percentage and time left. Until it has a full minute of readings it shows Windows' estimate, then its own estimate from a one-minute rolling average of battery draw. While charging it shows a rough time to full.
- **In effect:** BIOS thermal and charge modes (a pending change shows in blue underneath), the Windows power mode, and the built-in display's refresh rate and brightness. With dynamic refresh rate on, it shows the selected rate ("Dynamic 120 Hz"); Windows drops to 60 Hz when nothing on screen is moving, and doesn't expose the instantaneous rate.
- **Live strip:** battery watts, CPU package watts, average P-core clock and GPU clock. The P-core or GPU label turns amber and reads "LIMITED" when the CPU or GPU is being held back by a power, thermal or platform limit.

The **Activity** tab (Ctrl+2; Ctrl+1 returns to Settings) breaks this down:

| Section | Readings | Source |
|---|---|---|
| Power | Battery draw, CPU package, cores, graphics, memory, everything else | Battery IOCTL; Intel RAPL through Windows' `Energy Meter` counters |
| Battery | Time left, energy used since the window opened, remaining/full Wh, estimated health, charge cycles | Battery IOCTL, `GetSystemPowerStatus` |
| Processor | Average effective clock for P-cores and E-cores, load, performance limit | `Processor Information` counters; core classes from `GetSystemCpuSetInformation` |
| Graphics | Clock (hover for requested and maximum), busiest engine, media engine clock, throttle reason | Intel graphics control library (`ControlLib.dll`); `GPU Engine` counters |

"Everything else" is battery draw minus CPU package and memory: the display, storage, Wi-Fi and conversion losses. It's only shown on battery. Windows groups the low-power E-cores with the regular E-cores, so the app shows two core groups.

**Not available:** CPU and GPU temperatures, fan speed and battery temperature. This laptop doesn't report them to Windows, even with admin rights. The limit readings are the substitute: they show when heat or power is holding performance back.

All of these readings are read-only; nothing in them changes a setting. The checks in `tools/TelemetryChecks` cover battery decoding, the time-left estimator and GPU decoding: `dotnet run --project tools/TelemetryChecks`. Add `-- --live` to print one live sample, or `-- --render` to render the Activity view with fixture data to `artifacts/activity-panel.png`.

## Automatic thermal policy (v2)

`AutoSwitchPolicy` is pure mapping/validation, and `SwitchDebounce` uses a supplied monotonic clock. `AutoSwitcher` registers effective-power-mode V2 notifications and performs one trailing-edge check after two quiet seconds. It re-reads overlay and Energy saver state, compares the actual `(mode, AC/DC)` pair to the last check, and ignores transient Game Mode notifications. Startup, resume and command 128 force a fresh evaluation. No periodic BIOS polling runs in the service.

`Program.Main` dispatches `--service` to `XpsManagerService`, without constructing WPF. The service registers AC/DC, Energy saver and console display notifications and handles resume plus session unlock as a safety net. Unregistering callbacks occurs outside the switcher's lock to avoid teardown deadlock. Only `PowerSettings.SetThermal` is used by the service; it validates the advertised values and verifies the write.

Configuration uses the 64-bit HKLM view at `SOFTWARE\XpsManager\AutoSwitch`. `Configuration` is an atomic JSON snapshot of Enabled and the map; the plan's `Enabled` and `Map.*` values are mirrored for inspection and accepted as migration defaults when no snapshot exists. LastAppliedMode/At, LastTrigger and LastError are separate status values. Edit using the app or CLI so the snapshot and mirrors remain consistent. The installer explicitly protects the key and installation directory with administrator/SYSTEM write permissions.

The same framework-dependent single-file exe serves the UI and service. Development build outputs are rejected by the installer because they need adjacent DLLs. Installation rejects reparse points, uses quoted SCM paths, stages a fresh file before replacement, and removes only its own installed file (never recursively deletes a directory). A new app version offers an installed-service update. Removal from the installed running UI is rejected with instructions to use the downloaded copy.

See [V2-VALIDATION.md](V2-VALIDATION.md) for live evidence and release gates. Windows notification contracts: [effective mode registration](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powerregisterforeffectivepowermodenotifications) and [power event registration](https://learn.microsoft.com/en-us/windows/win32/power/registering-for-power-events).

Battery serial uses the `BatterySerialNumber` IOCTL information level, falling back to a leading decimal `Win32_Battery.DeviceID` serial formatted as hex. PPID is read once from an OEM string in SMBIOS type 22 through `MSSmBios_RawSMBiosTables`. The parser requires a single Dell battery record and one 20-character PPID, optionally followed by an Axx revision, matching [Dell's documented format](https://www.dell.com/support/kbdoc/en-us/000125347/how-to-obtain-a-dell-pcs-motherboard-ppid-piece-part-identification-without-going-inside-the-pc-and-removing-parts). Missing or ambiguous sources return unavailable.

## Project layout

| Project | What it is |
|---|---|
| `src/XpsManager.Core` | WMI wrapper (`BiosClient`) and power-settings logic (`PowerSettings`): validates input, orders the writes, and reads each change back to confirm it |
| `src/XpsManager.App` | WPF app (Cobalt theme), service host and installer; asks for admin at launch |
| `src/XpsManager.Cli` | `xpsm` command-line tool for scripting and testing |

## Single exe

```powershell
dotnet publish src/XpsManager.App -p:PublishProfile=SingleFile
.\dist\XpsManager.exe   # shows a UAC prompt
```

This builds one roughly 3 MB `XpsManager.exe` with the fonts and icon embedded. It needs the .NET 10 Desktop Runtime. For a machine without the runtime, add `-p:SelfContained=true -p:EnableCompressionInSingleFile=true`. That file is much larger and slower to open.

## Design

The UI follows the Hallmark skill's **Cobalt** theme. The palette, font and spacing values are in [`Themes/Tokens.xaml`](../src/XpsManager.App/Themes/Tokens.xaml) and the control styles are in [`Themes/Controls.xaml`](../src/XpsManager.App/Themes/Controls.xaml). The fonts are Space Grotesk, Inter and JetBrains Mono, all under the SIL Open Font License; the licences are next to the font files. The icon is generated by [`tools/make-icon.ps1`](../tools/make-icon.ps1).

## Running

The BIOS interface is admin-only.

```powershell
dotnet build
# App: launch the exe directly (it shows a UAC prompt). `dotnet run` can't start it elevated.
.\src\XpsManager.App\bin\Debug\net10.0-windows\XpsManager.exe
# CLI: run from an elevated terminal
xpsm status
xpsm thermal Quiet
xpsm charge Custom 50 80
xpsm charge Adaptive
```

## Notes

- **Speed:** listing all attributes takes about 2 s, because the provider reads all of them from firmware even when the query is filtered. Fetching one attribute by its key takes about 25 ms. The first run discovers the keys and caches them in `%LOCALAPPDATA%\XpsManager\attribute-keys.json`. After that, a read takes about 110 ms and a write plus read-back takes about 100â€“250 ms. Each fetched attribute is checked by name, so a stale cache triggers a new discovery rather than returning wrong data.
- **Password:** writes assume no BIOS admin password is set.
- **Custom thresholds:** they can only be changed while the charge mode is `Custom`, and stop must be at least 5% above start.

