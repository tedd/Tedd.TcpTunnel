## 2024-10-01 - Framework and Dependency Modernization

**Observation:** The codebase is exclusively targeting `net8.0`. Dependencies are severely outdated (e.g., K4os.Compression.LZ4.Streams 1.2.10-beta, Microsoft.Extensions.Logging 6.0.0-preview). Test framework packages are outdated (coverlet.collector 3.0.3, xunit 2.4.1). Testing project fails due to a known hanging issue which requires tests to timeout, and the test host on Ubuntu is lacking `net9.0` SDK.

**Strategic Action:** Upgraded dependencies in Tedd.TcpTunnel (K4os.Compression.LZ4.Streams -> 1.3.8, Microsoft.Extensions.* -> 10.0.12, System.IO.Pipelines -> 10.0.12). Upgraded System.CommandLine.DragonFruit -> 0.4.0-alpha.25306.1 in the console app. Upgraded test packages in the test project. Expanded TargetFrameworks to `net8.0;net10.0` across all projects (`net9.0` omitted due to missing SDK in environment). Test timeouts are a known issue. Will complete package metadata validation.
