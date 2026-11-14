# XPS Manager

Take control of your Dell XPS laptop's charging, cooling and performance from one small Windows app.

Built for the **Dell XPS 14 (DA14260)** on **Windows 11, 64-bit**. Other Dell models have not been verified.

**[Download the latest release](https://github.com/dklasens/XPSManager/releases/latest)** · [What's new in v2.1.0](docs/releases/v2.1.0.md)

## What can it do?

- **Choose how your laptop runs:** Optimized, Cool, Quiet or Ultra Performance, depending on what your BIOS supports.
- **Manage battery charging:** choose a charging mode or set your own start and stop percentages.
- **Get ready for a day out:** enable ExpressCharge for 24 hours, then automatically return to Primarily AC Use.
- **Follow Windows power mode:** automatically match your cooling/performance profile to your Windows settings, even with the app closed.
- **See live readings:** battery power and health, CPU and graphics activity, clocks and more. Available readings depend on your hardware.

## Install

1. Open the [latest release](https://github.com/dklasens/XPSManager/releases/latest).
2. Download **XpsManager-2.1.0-win-x64.zip** and extract it. Use the ZIP with this name, rather than GitHub's **Source code** downloads.
3. Run the **.msi** installer inside and approve the Windows administrator prompt.
4. Open **XPS Manager** from Start.

The installer includes the required .NET runtime and background service. You do not need to install .NET separately. A standalone MSI is also available on the release page.

The app is currently **unsigned**, so Windows may show an unknown-publisher or SmartScreen warning. Download it from this repository's Releases page. The release also includes SHA-256 checksums for verifying downloaded files.

**Upgrading from a portable copy?** First note your automatic settings and use **Automatic → Remove background service** in your downloaded copy. Then install the MSI and restore your settings. The installer will not take over an existing portable service.

## Everyday use

### Settings

Choose your thermal and charging options, then select **Write to BIOS** to apply them. **Discard** cancels unsaved edits; **Re-read** reloads the laptop's current settings. The app checks each change by reading it back from the BIOS.

### A full charge for a day out

Under **Settings → Battery charging**, press **ExpressCharge for 24 hours**. This enables fast charging to 100% and shows when **Primarily AC Use** will return. Press **Return now** to end early.

You can close the app: the background service keeps the timer. If the laptop is asleep or switched off when time runs out, it restores the charging mode after Windows resumes or starts. Apply or discard any pending edits before starting the timer.

Primarily AC Use follows Dell's battery-preservation policy. Returning to it does not drain a battery that is already fully charged. Both charging modes must be supported by your BIOS.

### Automatic

Turn on **Follow Windows power mode** to use these defaults, or choose your own:

| Windows power mode | Laptop thermal mode |
|---|---|
| Best power efficiency / Energy saver | Quiet |
| Balanced | Optimized |
| Best performance | Ultra Performance |

This option is off on a fresh installation. If you use Dell Optimizer, turn off its thermal/power-mode synchronisation before enabling this feature so the two apps do not compete. Automatic thermal switching and the ExpressCharge timer work independently.

### Activity

View live battery, CPU and graphics readings. Monitoring pauses when the window is minimised. CPU temperatures and fan speeds are not currently available.

## Update or uninstall

Run a newer MSI to update; your automatic settings are kept. To remove the app, use **Windows Settings → Apps → Installed apps → XPS Manager**.

If an ExpressCharge timer is active, select **Return now** before uninstalling: removing the service also removes its ability to restore the charging mode. Uninstalling keeps saved automatic settings for a future reinstall and leaves the current BIOS settings in place.

## Compatibility and help

XPS Manager requires administrator access to Dell's BIOS settings. It has been developed and tested on an XPS 14 DA14260, BIOS 1.11.0, without a BIOS administrator password. It does not flash the BIOS or change Windows power modes.

Automated checks cover the charging timer and installer lifecycle. A live 24-hour charging cycle and some sleep/restart scenarios remain unverified; see the [validation notes](docs/RELEASE-READINESS.md). For problems, [open an issue](https://github.com/dklasens/XPSManager/issues) with your laptop model, BIOS version, app version and error message. Please omit serial numbers and other personal details.

## For developers

Use the .NET SDK specified in `global.json` on Windows:

```powershell
dotnet build -c Release
dotnet run --project tools/TelemetryChecks -c Release
./tools/Build-Installer.ps1
```

GitHub Actions builds and tests each change. A matching version tag, such as `v2.1.0`, publishes the MSI, an MSI ZIP and checksums after the checks pass.

More detail: [technical notes and CLI](docs/TECHNICAL.md) · [security notes](docs/SECURITY.md) · [validation record](docs/RELEASE-READINESS.md).
