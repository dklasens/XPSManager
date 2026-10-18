# XPS Manager

A small Windows app for the **Dell XPS 14 (DA14260)** that lets you change the laptop's power and battery settings from Windows, and see what the laptop is doing right now. You don't need to restart into the BIOS or install Dell's larger utilities.

Open it, change a setting, close it.

## What it does

**Change settings**
- **Thermal mode:** Optimized, Cool, Quiet or Ultra Performance. This sets how hard the laptop is allowed to run and how loud the fans may get.
- **Battery charging:** Adaptive, Standard, Express, Primarily AC, or Custom. Custom sets your own start and stop percentages; for example, stopping at 80% helps the battery last longer.

**See what's happening**, updated every second:
- Battery level and how long it will last at the current rate of use
- How much power the battery, the processor and the graphics are using
- Processor and graphics speeds, and whether they're being held back by heat or power limits
- The Windows power mode, the screen's refresh rate and brightness
- Battery health and charge cycles

The **Activity** tab has the full breakdown. The **Settings** tab is what opens by default.

## How it works

Dell builds a settings interface into the laptop's firmware (the BIOS) that Windows programs can talk to. XPS Manager uses it to read your current settings and write the ones you choose. After each change it reads the setting back to make sure it took effect. These are the same settings you'd find by restarting into the BIOS setup screen.

The live readings come from sources already in Windows: the battery, Windows performance counters, and Intel's graphics driver. Reading them never changes anything.

Because it changes BIOS settings, the app needs administrator access. Windows asks once when it opens.

## Download

Get **XpsManager.exe** (or the .zip) from the [latest release](../../releases/latest).

- Needs Windows 11 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
- The app isn't code-signed, so Windows may show a SmartScreen warning the first time. Choose **More info**, then **Run anyway**.

## Good to know

- It's built and tested on the XPS 14 DA14260 (BIOS 1.11.0). Other Dell laptops may work if their BIOS offers the same settings, but they haven't been tested.
- It assumes no BIOS administrator password is set.
- Temperatures and fan speed aren't shown: this laptop doesn't report them to Windows.

## For developers

Build, project layout, and how each reading is measured: see [docs/TECHNICAL.md](docs/TECHNICAL.md).

```powershell
dotnet build
dotnet run --project tools/TelemetryChecks   # checks
dotnet publish src/XpsManager.App -p:PublishProfile=SingleFile   # dist\XpsManager.exe
```

Releases are built by GitHub Actions: pushing a tag such as `v1.1` builds, checks and publishes the exe and .zip.
