# Plan: automatic thermal switching and Dell Optimizer parity

Written 2026-11-10. This is the original design plan; v2 was implemented on 2026-11-12. See [the validation record](docs/V2-VALIDATION.md) for completed checks, implementation discoveries and deferred tests, and [the security review](docs/SECURITY.md) for the shipped protections. Assumptions below describe the original plan, not proof of current behaviour.

## Goal

Make XPS Manager a full replacement for Dell Optimizer on the XPS 14 (DA14260), so Dell Optimizer can be uninstalled.

The main missing feature is automatic thermal switching: XPS Manager should follow the Windows power mode and set the BIOS thermal mode to match. In practice that means Quiet on battery and Ultra Performance on AC, because Windows already keeps a separate power mode for each power source.

## Background

### How the app works today

- One elevated WPF app (`src/XpsManager.App`), opened, used and closed. There is no background process, tray icon or service.
- Every BIOS read and write goes through Dell's WMI interface (`root\dcim\sysman\biosattributes`), which is admin-only. See `src/XpsManager.Core/BiosClient.cs`.
- Thermal mode is the BIOS attribute `ThermalManagement` with values `Optimized`, `Cool`, `Quiet` and `Performance` (shown as "Ultra Performance"). Writes take effect immediately, with no reboot. See `src/XpsManager.Core/PowerSettings.cs`.
- The app already reads the Windows power mode and the AC/battery state for telemetry: `SystemInfo.WindowsPowerMode` and `SystemInfo.Power` in `src/XpsManager.Core/SystemInfo.cs`.
- It ships as a single framework-dependent `XpsManager.exe` (publish profile `SingleFile`), built and released by `.github/workflows/release.yml` when a `v*` tag is pushed.
- There is also a CLI (`src/XpsManager.Cli`, output `xpsm.exe`) with `status`, `thermal <mode>` and `charge <mode>` commands.

### How Dell Optimizer implements each feature

These findings come from inspecting the Dell Optimizer 6.3.3 install (`C:\Program Files\Dell\DellOptimizer`) on 2026-11-10. They are based on strings in its assemblies, not on decompiled code.

| Optimizer feature | How Optimizer does it | What that means for XPS Manager |
|---|---|---|
| Sync thermal mode with Windows power mode | In software, in both directions, driven by the Windows "effective power mode" notification | Replicable with the same notification |
| Dynamic Charge | Learns usage, then switches the BIOS charge mode and shows a toast. Not supported when the charge mode is Custom | The BIOS `Adaptive` mode is the firmware equivalent |
| Dolby Vision Bright / Dark / Vivid | Remote-controls the Dolby Vision Access Store app over a Dolby-licensed channel | Not replicable |
| Battery details | Health, charge rate, time to full, manufacturer, serial, PPID | Serial and PPID are missing from XPS Manager |

### Parity status

| Feature | Status | Action in this plan |
|---|---|---|
| Thermal modes | Parity | None |
| Battery percentage, health, rate, estimate | Parity or better | None |
| Power-mode sync | Missing | Build it (workstreams A to E) |
| Battery serial and PPID | Missing | Add serial; add PPID if a source can be found (workstream F) |
| Dynamic Charge | Different mechanism | Not building; see "Out of scope" |
| Dolby Vision modes | Missing | Not building; see "Out of scope" |

### Machine state observed on 2026-11-10

- Dell Optimizer 6.3.3 is installed, with its "synchronize thermal management mode with Windows power mode" box ticked.
- The thermal mode was Ultra Performance.
- The Windows power mode was "Best performance" for both plugged in and on battery (`ActiveOverlayAcPowerScheme` and `ActiveOverlayDcPowerScheme` were both `ded574b5-…`). The owner expected "Best power efficiency" on battery. Optimizer's reverse sync (thermal mode drives power mode) is the likely cause.
- Dolby Vision Access and Dolby Access Store apps are installed.
- Dell's own services (for example `DellTechHub`) run as LocalSystem.

## Architecture decision

**Decision: a Windows service running as LocalSystem, hosted in the existing `XpsManager.exe`.**

### Options considered

| | Windows service | Elevated scheduled task at logon | Scheduled task per plug event |
|---|---|---|---|
| Runs before logon and for every user | Yes | No | Partly |
| Restarts after a crash | Yes, through service recovery settings | No | Not applicable |
| Reacts to power-mode changes, not only plug and unplug | Yes | Yes | No |
| Keeps elevated code out of the desktop session | Yes | No | Mostly |
| Install effort | Needs a fixed, protected install location | Low | Low |

### Why the service

- It is the standard Windows pattern for privileged, always-on hardware policy, and it is how Dell's own components run.
- The Windows power mode is machine-wide, so a service can read it with no user logged in.
- It opens the way to a later phase where the app itself no longer needs a UAC prompt (see "Possible follow-up").

### Constraints that follow

- **Protected install location.** A LocalSystem service must not run from a user-writable folder such as Downloads or OneDrive, because anything that can replace the exe gains full system rights. The service runs from an installed copy in `C:\Program Files\XPS Manager\`.
- **Protected configuration.** The service's settings must be writable by administrators only. Use the registry key `HKLM\SOFTWARE\XpsManager\AutoSwitch`, which has that protection by default.
- **Single-file release stays.** The same exe runs as the app (no arguments) or as the service (`--service`), so the release remains one file.

## Design

### Behaviour

- **Direction:** one-way. The Windows power mode drives the thermal mode. XPS Manager never changes the Windows power mode.
- **Default mapping** (editable in the app):

  | Windows power mode | Thermal mode |
  |---|---|
  | Energy saver | Quiet |
  | Best power efficiency | Quiet |
  | Balanced | Optimized |
  | Better performance (only on older builds) | Optimized |
  | Best performance | Ultra Performance (`Performance`) |
  | Custom or unreadable | No change |

- **When the service applies the mapping:**
  - when the service starts;
  - when Windows reports a power-mode change, which includes plugging in and unplugging;
  - when Energy saver turns on or off;
  - when the machine resumes from sleep.
- **Manual override:** a thermal mode chosen by hand in the app stays in force until the next power-mode change. The service does not re-apply on a timer.
- **Write discipline:** the service reads the current thermal mode first and writes only if it differs. This avoids needless BIOS writes.
- **Debounce:** wait about 2 seconds after the last notification before acting, so a quick unplug and replug causes one write at most.
- **Validation:** the service only ever writes `ThermalManagement`, and only a value that the BIOS lists as possible. An invalid configured value is logged and skipped.

### Detecting the Windows power mode

- **Trigger:** `PowerRegisterForEffectivePowerModeNotifications` in `powrprof.dll` (version 2). It calls back on every change and once immediately on registration. It needs no window, so it works in a service.
- **Value:** on each callback, re-read the mode with the existing overlay lookup (`PowerGetEffectiveOverlayScheme`) plus the Energy saver flag, instead of trusting the callback's enum. The enum also reports transient states such as Game Mode, which should not change the thermal mode.
- **Extra triggers:** register for the AC/DC power source and Energy saver setting changes through the service's power-event support, and handle resume events. These are a safety net in case the main callback misses a transition.
- **Refactor needed:** `SystemInfo.WindowsPowerMode` currently returns a display string. Split it into an enum (`EnergySaver`, `BestPowerEfficiency`, `Balanced`, `BetterPerformance`, `BestPerformance`, `Custom`) plus a label function, so the policy can switch on the enum and telemetry keeps its labels.

### Configuration and status

All under `HKLM\SOFTWARE\XpsManager\AutoSwitch`:

| Value | Written by | Purpose |
|---|---|---|
| `Enabled` | App | Master switch |
| `Map.EnergySaver`, `Map.BestPowerEfficiency`, `Map.Balanced`, `Map.BetterPerformance`, `Map.BestPerformance` | App | Thermal mode for each power mode; empty means "no change" |
| `LastAppliedMode`, `LastAppliedAt`, `LastTrigger`, `LastError` | Service | Shown in the app's status line |

After the app changes the configuration, it tells the service to reload by sending a custom service control command (`ServiceController.ExecuteCommand`, command 128). The app is elevated, so it has the right to do this.

### Service host

- **Service name:** `XpsManagerService`. **Display name:** "XPS Manager".
- **Account:** LocalSystem. **Start type:** automatic (delayed start).
- **Recovery:** restart after 5 seconds, then after 30 seconds; reset the failure count daily.
- **Logging:** Windows Application event log, source "XPS Manager". Log each applied change, each skipped invalid value and each BIOS error. Do not log routine no-op checks.
- **Entry point:** the WPF project currently relies on the generated `Main`. Replace it with an explicit `Program.Main` that runs the service when `--service` is passed and starts the WPF app otherwise. This requires changing `App.xaml` from an application definition to a page.
- **Package:** `System.ServiceProcess.ServiceController` for `ServiceBase` and `ServiceController`.
- **BIOS key cache:** `BiosClient.DefaultKeyCachePath` resolves to the LocalSystem profile when running as a service, so the service keeps its own cache. No change is needed.

### Install, update and uninstall

All three are done by the app, which is already elevated.

- **Install** (when the toggle is turned on for the first time):
  1. Copy the running exe to `C:\Program Files\XPS Manager\XpsManager.exe`.
  2. Create the event log source.
  3. Register the service with the `--service` argument, delayed automatic start and the recovery settings above.
  4. Write the default mapping and `Enabled = 1`, then start the service.
- **Update:** on launch, compare the running exe's version with the installed copy. If the running one is newer, offer to stop the service, replace the copy and start it again.
- **Disable:** turning the toggle off sets `Enabled = 0` and leaves the service installed but idle.
- **Uninstall:** a separate "Remove background service" action stops and deletes the service, removes the event log source, the Program Files folder and the registry key.

### App UI

Add an "Automatic" section to the Settings view, below the thermal mode control. Follow the existing Hallmark/Cobalt styling in `Themes/Tokens.xaml` and `Themes/Controls.xaml`.

- A toggle: "Follow Windows power mode".
- Three mapping rows (Best power efficiency, Balanced, Best performance), each with a thermal mode selector. Energy saver follows the Best power efficiency row unless there is a reason to separate them.
- A status line, for example "On. Last set Quiet at 14:32 (unplugged)". Show the last error here if there is one.
- A warning when Dell Optimizer is installed, saying its own sync must be turned off or the two will fight. Detect Optimizer by its uninstall registry entry.
- When the service changes the thermal mode while the app is open, the "active in BIOS" marker goes stale. Re-read the BIOS when the window is activated.

## Workstreams

Do them in this order. Each one should leave the app in a working state.

### A. Verify assumptions (needs one elevated session)

None of these have been tested yet. If any fails, revisit the design before building.

1. **BIOS access as LocalSystem.** Run `xpsm status` as SYSTEM, for example through a one-off scheduled task with `/ru SYSTEM` that redirects output to a file. Expect the same output as an elevated run.
2. **BIOS write as LocalSystem.** Run `xpsm thermal Quiet` the same way, confirm, then restore the original mode.
3. **Power-mode lookup in session 0.** Confirm `PowerGetEffectiveOverlayScheme` returns the right overlay when called as SYSTEM.
4. **Notification behaviour.** With a small test harness, record what the effective-power-mode callback reports for each of the three power modes, for Energy saver, and on plug and unplug. Confirm a callback arrives for every transition.
5. **Resume behaviour.** This laptop probably uses Modern Standby, where classic resume events may not arrive. Check whether a callback or power event fires after sleep. If none does, add a session-unlock or display-on trigger.

### B. Policy logic in Core

- Add the power-mode enum and refactor `SystemInfo.WindowsPowerMode` as described above.
- Add a pure policy type: given the current power mode, the mapping and the BIOS's possible thermal values, return the thermal mode to set or "no change".
- Add the configuration reader and writer for the registry key.
- Add an `AutoSwitcher` that owns the notification registration, the debounce and the "read, compare, write" step, using `PowerSettings`.

### C. Service host

- Add `Program.Main` and the `--service` branch.
- Add the `ServiceBase` subclass that starts and stops `AutoSwitcher`, handles power events and the reload command, and writes status and event log entries.
- Test by running the service from a locally built exe copied to Program Files.

### D. Install, update and uninstall

- Add a `ServiceInstaller` helper for the steps listed under "Install, update and uninstall".
- Make each operation safe to repeat, and report failures in the app's status line.

### E. App UI

- Build the "Automatic" section, the status line, the Optimizer warning and the re-read on activation.
- Add `xpsm auto status|on|off` to the CLI if it is cheap, for scripting and troubleshooting.

### F. Battery details

- **Serial:** add it to the Activity view. `Win32_Battery.DeviceID` begins with the serial in decimal (`225` is `00E1`), followed by the manufacturer and name. Prefer the battery IOCTL's serial-number query if it works; fall back to parsing `DeviceID`.
- **PPID:** the source is unknown. Spend a short, fixed amount of time looking in SMBIOS type 22 and in the Dell `sysman` WMI namespaces. Drop it if no clean source turns up; it is cosmetic.

### G. Checks, docs and release

- Add policy tests to `tools/TelemetryChecks`, following its existing pattern. They must run on the CI runner, which has no Dell hardware, so they cover the pure mapping and debounce logic only.
- Update `README.md`: what the automatic mode does, that it installs a service, where it installs to, and how to remove it.
- Update the release notes text in `.github/workflows/release.yml` to mention the service.
- Tag a new version once the acceptance checks pass.

## Acceptance checks

Run these on the laptop with Dell Optimizer's sync turned off.

| Check | Expected result |
|---|---|
| Unplug with battery mode set to Best power efficiency | Thermal mode becomes Quiet within a few seconds |
| Plug in with AC mode set to Best performance | Thermal mode becomes Ultra Performance within a few seconds |
| Change the power mode in Settings without changing the power source | Thermal mode follows the mapping |
| Turn Energy saver on, then off | Quiet, then back to the mapped mode |
| Pick Cool by hand in the app, then wait | Cool stays until the next power-mode change |
| Unplug and replug within two seconds | At most one BIOS write, visible in the event log |
| Reboot and do not open the app | Service is running and has applied the mapping |
| Sleep on AC, unplug, wake | Thermal mode is correct shortly after waking |
| Kill the service process | Windows restarts it |
| Turn the toggle off | No further changes on plug or unplug |
| Remove the background service | Service, Program Files folder, event source and registry key are gone |
| Run a newer exe while an older copy is installed | The app offers to update the installed copy |

## Cut-over from Dell Optimizer

Do this only after the acceptance checks pass.

1. In Dell Optimizer, untick "Allow Dell Optimizer to synchronize thermal management mode with Windows power mode settings and vice versa".
2. Open Dolby Vision Access and confirm the Bright, Dark and Vivid picture modes are available there. This has not been checked.
3. Uninstall Dell Optimizer. Check afterwards that SupportAssist still works, because some Dell services are shared.
4. In Windows Settings, set the on-battery power mode back to Best power efficiency.
5. In XPS Manager, confirm the battery charge mode is what you want. Dynamic Charge stops when Optimizer is removed, and the BIOS keeps whichever mode was set last.

## Out of scope

- **Dolby Vision picture modes.** Optimizer uses a Dolby-licensed channel into the Dolby Vision Access app. That app is the supported place to set them.
- **Dynamic Charge.** The BIOS `Adaptive` mode covers the same intent in firmware, and Custom thresholds give more control than Optimizer offered.
- **Reverse sync.** Changing the Windows power mode when a thermal mode is picked. It is the likely reason the battery power mode ended up on Best performance.
- **Switching other settings automatically** (charge mode, refresh rate, processor settings). These belong to the later profiles work.

## Possible follow-up: no UAC prompt

Once the service exists, the app could send its BIOS reads and writes to the service over a named pipe and run without elevation. That would remove the UAC prompt on every launch.

It is a larger change and is not part of this plan. If it is taken on:

- restrict the pipe to interactive users and administrators;
- accept only a fixed set of commands with validated values, never "set any BIOS attribute";
- move service install and uninstall behind a one-off elevation, since the app would no longer be elevated.

## Open decisions

| Decision | Default assumed in this plan |
|---|---|
| One-way or two-way sync | One-way |
| Energy saver has its own mapping row | No; it follows Best power efficiency |
| Toggle off also uninstalls the service | No; uninstall is a separate action |
| Include the no-UAC follow-up | No |
