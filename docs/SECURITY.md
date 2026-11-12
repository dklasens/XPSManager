# Security and performance review

Reviewed 2026-11-12 against the v2 source and the installed XPS 14 service. This is a source/configuration review with targeted runtime checks, not an independent penetration test or a guarantee of zero risk.

## Background activity

The service is event-driven. `AutoSwitcher` creates an initially inactive timer. A Windows power notification schedules `Change(2000, Timeout.Infinite)`: one callback two seconds after the most recent notification, with no repeating interval. After it runs, it stays inactive until another event. An early timer callback is rescheduled only for the remaining debounce time. There is no 5 ms loop, periodic BIOS poll, heartbeat, network ping, or timer-based enforcement of the thermal setting.

Actual power mode and AC/DC state are compared with the previous input, so duplicate and transient Game Mode notifications do not cause repeated BIOS checks. Only thermal settings offered by the BIOS can be written, and an already-correct value is not written again. Disabled configuration returns before BIOS access. Errors wait for another event or explicit reload; there is no error retry loop.

The visible app samples battery/CPU/GPU telemetry once a second and refreshes service status every three seconds. Both timers pause when minimised and stop when the window closes. A read already in progress can finish after minimising. BIOS settings are refreshed on opening, window activation, explicit re-read and verification after writing. The service does not instantiate the WPF app or its telemetry sampler.

An idle observation of the same service process over 109.28 seconds recorded **0 ms additional process CPU time and zero additional read, write or other process I/O operations**. Working set was **38.75 MiB**, private memory **10.16 MiB**. No TCP connections/listeners or UDP endpoints were observed. These counters have finite resolution; they do not establish exactly zero energy use, account for all work in Windows/Dell providers, or prove long-term behaviour. Local evidence: ignored `artifacts/service-idle-measurement.json`.

After installing the hardening changes, a second observation over **96.139 seconds** again recorded **0 ms additional CPU and no additional I/O operations**, with **39.96 MiB working set / 11.42 MiB private memory**. The service remained enabled with no last error, and its executable hash matched the reviewed published build. Evidence: `artifacts/hardened-idle-measurement.json`.

Thermal modes themselves intentionally affect machine performance, fan noise and power consumption: Ultra Performance can use more power and produce more heat; Quiet can reduce performance. That is separate from the program's monitoring overhead.

## Privilege boundaries

- The service runs as LocalSystem to access Dell's admin-only WMI provider. Its executable is a quoted fixed path under Program Files, not the downloaded/workspace copy.
- The installed directory and executable grant write access only to administrators and SYSTEM. Ordinary users have read/execute access. Installation refuses reparse points in the path and stages replacement files in the protected directory.
- Configuration is in the 64-bit HKLM registry view, with administrator/SYSTEM write access and ordinary-user read access. Settings are committed as one JSON value to avoid partial reads during updates.
- Service permissions explicitly allow administrators/SYSTEM to manage the service and request command 128. Interactive users may query/interrogate it, but cannot start, stop, reconfigure or request a custom reload.
- No HTTP server, socket listener, named-pipe command server, remote-management API, download/update client, or cloud telemetry is implemented. Service inputs are Windows power events, the fixed reload command and protected configuration. The service's only BIOS write path is `ThermalManagement` with validated values.
- Native imports in both app and Core are restricted to System32. This avoids picking up a planted DLL beside a downloaded elevated executable or in its working directory/PATH. Intel's ControlLib.dll was present in System32 with a valid Microsoft Windows Hardware Compatibility Publisher signature; live graphics reads continued to work after hardening.

The review found and fixed two gaps: Windows' default service ACL permitted interactive users to send custom control commands, and native imports previously used the default DLL search path. It also removed unnecessary status polling while the UI is minimised.

## Verification

- A non-elevated token could query service status, but access requests for change-config, start, stop and custom reload all failed with access denied. Writable opens of the HKLM configuration and installed executable were denied. `tools/Check-ServiceAccess.ps1` repeats these checks without sending a service command or writing data.
- Build and pure checks passed. Live telemetry confirmed the restricted native loader still finds the graphics driver. The hardened service updated successfully and remained running.
- NuGet's direct/transitive vulnerable-package check reported no known vulnerable packages in the solution on the review date. That is advisory coverage, not proof that dependencies are vulnerability-free. The installed shared .NET runtime, Windows and Dell/Intel drivers also need normal security updates.
- Earlier live checks verified invalid mappings are skipped, manual override persists, disabling stops automatic writes, crash recovery works, and removal/reinstallation/update works. The deferred Energy saver, rapid replug, sleep/wake and reboot checks remain listed in [V2-VALIDATION.md](V2-VALIDATION.md).

The app remains unsigned and elevated, and the service has powerful privileges. Use a trusted build, keep its dependencies updated, and treat code changes as privileged software changes. This review found no remaining critical issue within the inspected paths; it is not a blanket assurance that future versions, dependencies or firmware cannot have defects.

Reference contracts: [Microsoft service access rights](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights), [restricted native DLL search paths](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.defaultdllimportsearchpathsattribute), and [DLL security guidance](https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-security).
