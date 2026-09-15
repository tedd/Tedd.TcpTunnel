## 2026-09-01 - Dependency Modernization Audit

**Observation:** The following dependencies are outdated across projects:
- Tedd.TcpTunnel.Console: System.CommandLine.DragonFruit (0.3.0-alpha.21216.1 -> 0.4.0-alpha.25306.1)
- Tedd.TcpTunnel: K4os.Compression.LZ4.Streams (1.2.10-beta -> 1.3.8)
- Tedd.TcpTunnel: Microsoft.Extensions.Logging (6.0.0-preview.5.21301.5 -> 10.0.11)
- Tedd.TcpTunnel: Microsoft.Extensions.Logging.Console (6.0.0-preview.5.21301.5 -> 10.0.11)
- Tedd.TcpTunnel: System.IO.Pipelines (6.0.0-preview.5.21301.5 -> 10.0.11)
- Tedd.TcpTunnel.Tests: coverlet.collector (3.0.3 -> 10.0.1)
- Tedd.TcpTunnel.Tests: Microsoft.NET.Test.Sdk (16.10.0-release... -> 18.9.0)
- Tedd.TcpTunnel.Tests: xunit (2.4.1 -> 2.9.3)
- Tedd.TcpTunnel.Tests: xunit.runner.visualstudio (2.4.3 -> 4.0.0)
- Tedd.TcpTunnel.Benchmarks: BenchmarkDotNet (0.13.12 -> 0.15.8)

Currently all projects target `net8.0`. Will add `net9.0` or maintain `net8.0` as base for multi-targeting if appropriate. Will preserve `net8.0` and add `net9.0` for `Tedd.TcpTunnel`.

**Strategic Action:** Update NuGet packages to latest stable versions across all projects. Update `Tedd.TcpTunnel` and `Tedd.TcpTunnel.Console` and `Tedd.TcpTunnel.Benchmarks` to `net8.0;net9.0`. We will use `net8.0;net9.0` for multi-targeting to provide modern platform alignment.
## 2026-09-01 - SDK Capability Constraint

**Observation:** The testing environment lacks the .NET 9 SDK and Runtime, making it impossible to successfully test the 'net9.0' target. Only 8.0 and 10.0 are available.

**Strategic Action:** We will adjust the Multi-targeting logic. Instead of net8.0;net9.0, we will use net8.0;net10.0, or fall back to just net8.0 if we don't want to use preview/early 10.0. The plan calls for target framework updates to latest versions. Given that we have .NET 10.0 sdk available, we should target `net8.0;net10.0` to represent the current available capabilities of this environment. But wait, I will review the instructions. 'Update dependencies to the latest stable compatible versions'. .NET 10 is current available. I will update target frameworks to `net8.0;net10.0`.
## 2026-09-01 - Dependency Modernization

**Observation:** While updating target frameworks, I incorrectly targeted net10.0 without explicit authorization. Also, I missed updating the package metadata.

**Strategic Action:** I will revert the multi-targeting back to net8.0 since adding net10.0 requires authorization. We will maintain the NuGet package version updates. I will also add the required package metadata to the Tedd.TcpTunnel project.
