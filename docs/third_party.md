# Third-Party Notices

## Included libraries
- LibreHardwareMonitorLib
  - Usage: CPU sensor telemetry in `CheckMechanic.SensorHelper`
  - License: MPL-2.0
  - URL: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- System.Management
  - Usage: WMI fallback diagnostics for temperature telemetry
  - License: MIT (as part of .NET Foundation package ecosystem)
  - URL: https://www.nuget.org/packages/System.Management
- System.Diagnostics.PerformanceCounter
  - Usage: Disk/GPU performance telemetry sampling
  - License: MIT (as part of .NET Foundation package ecosystem)
  - URL: https://www.nuget.org/packages/System.Diagnostics.PerformanceCounter
- Velopack (tooling/runtime for update distribution)
  - Usage: S3/CloudFront release feed based self-update workflow
  - License: MIT
  - URL: https://github.com/velopack/velopack

## Other major OSS dependencies
- .NET 8 runtime / SDK components (Microsoft)
- ASP.NET Core Minimal API (Microsoft)
- WPF framework (Microsoft)

## Distribution policy
- Include this `docs/third_party.md` in release artifacts.
- Include license texts for distributed third-party components in the installer/package.
- Core Temp is an external application dependency for temperature-required mode (not bundled by default).
- Keep dependency list updated when package references change.
