using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tedd.TcpTunnel.Benchmarks;

internal static class ThroughputSuite
{
    private static readonly string BenchmarkKey = EncryptionOptions.GenerateKey();
    private const int TransferBufferSize = 1024 * 1024;

    private static readonly Scenario[] Scenarios =
    [
        new("None", Codec.None, "uncompressed framing"),
        new("LZ4 fastest", Codec.Lz4, "level=Fastest", CompressionLevel.Fastest),
        new("LZ4 HC", Codec.Lz4, "level=SmallestSize", CompressionLevel.SmallestSize),
        new("Brotli q1", Codec.Brotli, "quality=1, window=22, history=false", BrotliQuality: 1, BrotliWindow: 22, RepeatFile: true),
        new("Brotli q4", Codec.Brotli, "quality=4, window=22, history=false", BrotliQuality: 4, BrotliWindow: 22, RepeatFile: true),
        new("Brotli q9", Codec.Brotli, "quality=9, window=22, history=false", BrotliQuality: 9, BrotliWindow: 22, RepeatFile: true),
        new("Brotli q4 + history", Codec.Brotli, "quality=4, window=22, history=true", BrotliQuality: 4, BrotliWindow: 22, History: true, RepeatFile: true),
        new("Brotli q9 + history", Codec.Brotli, "quality=9, window=22, history=true", BrotliQuality: 9, BrotliWindow: 22, History: true, RepeatFile: true),
        new("Zstandard -5", Codec.Zstandard, "level=-5", ZstandardLevel: -5),
        new("Zstandard 3", Codec.Zstandard, "level=3", ZstandardLevel: 3),
        new("Zstandard 9", Codec.Zstandard, "level=9", ZstandardLevel: 9),
        new("Deflate fastest", Codec.Deflate, "level=Fastest", CompressionLevel.Fastest),
        new("Deflate optimal", Codec.Deflate, "level=Optimal", CompressionLevel.Optimal),
        new("GZip fastest", Codec.GZip, "level=Fastest", CompressionLevel.Fastest),
        new("GZip optimal", Codec.GZip, "level=Optimal", CompressionLevel.Optimal),
        new("ZLib fastest", Codec.ZLib, "level=Fastest", CompressionLevel.Fastest),
        new("ZLib optimal", Codec.ZLib, "level=Optimal", CompressionLevel.Optimal),
        new("ChaCha20-Poly1305", Codec.None, "encryption=ChaCha20Poly1305", Encryption: EncryptionAlgorithm.ChaCha20Poly1305),
        new("AES-256-GCM", Codec.None, "encryption=AesGcm", Encryption: EncryptionAlgorithm.AesGcm),
        new("AES-256-CCM", Codec.None, "encryption=AesCcm", Encryption: EncryptionAlgorithm.AesCcm),
        new("LZ4 + ChaCha20-Poly1305", Codec.Lz4, "level=Fastest, encryption=ChaCha20Poly1305", Encryption: EncryptionAlgorithm.ChaCha20Poly1305),
        new("LZ4 + AES-256-GCM", Codec.Lz4, "level=Fastest, encryption=AesGcm", Encryption: EncryptionAlgorithm.AesGcm),
        new("LZ4 + AES-256-CCM", Codec.Lz4, "level=Fastest, encryption=AesCcm", Encryption: EncryptionAlgorithm.AesCcm),
        new("Brotli history + ChaCha20", Codec.Brotli, "quality=4, window=22, history=true, encryption=ChaCha20Poly1305", BrotliWindow: 22, History: true, RepeatFile: true, Encryption: EncryptionAlgorithm.ChaCha20Poly1305),
        new("Brotli history + AES-GCM", Codec.Brotli, "quality=4, window=22, history=true, encryption=AesGcm", BrotliWindow: 22, History: true, RepeatFile: true, Encryption: EncryptionAlgorithm.AesGcm),
        new("Brotli history + AES-CCM", Codec.Brotli, "quality=4, window=22, history=true, encryption=AesCcm", BrotliWindow: 22, History: true, RepeatFile: true, Encryption: EncryptionAlgorithm.AesCcm)
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        var options = SuiteOptions.Parse(args);
        if (options.Help)
        {
            Console.WriteLine(SuiteOptions.HelpText);
            return 0;
        }

        var files = DiscoverFiles(options.Files);
        var samples = files.Select(CreateSample).ToArray();
        var machine = new MachineInformation(
            RuntimeInformation.OSDescription.Trim(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown",
            Environment.ProcessorCount,
            RuntimeInformation.FrameworkDescription);
        var targetBytes = checked(options.TargetMiB * 1024L * 1024L);
        var results = new List<ScenarioResult>(Scenarios.Length);

        Console.WriteLine($"Input files: {string.Join(", ", samples.Select(sample => $"{sample.Name} ({ToMiB(sample.Bytes):0.0} MiB)"))}");
        Console.WriteLine($"Target: {options.TargetMiB} MiB per transfer, {options.Warmups} warm-up(s), {options.Iterations} measured iteration(s).");
        foreach (var scenario in Scenarios)
        {
            if (!EncryptionOptions.IsSupported(scenario.Encryption)) { Console.WriteLine($"Skipping unsupported {scenario.Encryption}."); continue; }
            var plan = BuildPlan(files, targetBytes, scenario.RepeatFile);
            Console.Write($"{scenario.Name,-24} ");
            var result = await MeasureScenarioAsync(scenario, plan, options).ConfigureAwait(false);
            results.Add(result);
            Console.WriteLine($"best {result.BestMiBPerSecond,9:0.0} MiB/s · median {result.MedianMiBPerSecond,9:0.0} MiB/s");
        }

        var report = new BenchmarkReport(DateTimeOffset.UtcNow, machine, options, samples, results);
        await WriteOutputsAsync(report).ConfigureAwait(false);
        Console.WriteLine($"Markdown: {Path.GetFullPath(options.MarkdownOutput)}");
        Console.WriteLine($"Web data: {Path.GetFullPath(options.JsonOutput)}");
        Console.WriteLine($"Graph: {Path.GetFullPath(options.SvgOutput)}");
        return 0;
    }

    private static FileInfo[] DiscoverFiles(IReadOnlyList<string> requested)
    {
        if (requested.Count > 0)
        {
            var supplied = requested.Select(path => new FileInfo(Path.GetFullPath(path))).ToArray();
            var missing = supplied.FirstOrDefault(file => !file.Exists);
            if (missing is not null) throw new FileNotFoundException("Benchmark input file was not found.", missing.FullName);
            return supplied.DistinctBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Specify at least one --file outside Windows.");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var preferred = new[] { "mshtml.dll", "Windows.UI.Xaml.dll", "shell32.dll" }
            .Select(name => new FileInfo(Path.Combine(system, name)))
            .Where(file => file.Exists)
            .ToList();
        if (preferred.Count < 3)
        {
            preferred.AddRange(new DirectoryInfo(system).EnumerateFiles("*.dll", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.Length)
                .Where(file => preferred.All(existing => !string.Equals(existing.FullName, file.FullName, StringComparison.OrdinalIgnoreCase)))
                .Take(3 - preferred.Count));
        }
        if (preferred.Count == 0) throw new FileNotFoundException("No DLL benchmark inputs were found in the Windows system directory.");
        return preferred.ToArray();
    }

    private static FileSample CreateSample(FileInfo file)
    {
        using var input = file.OpenRead();
        return new(file.Name, file.Length, Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
    }

    private static TransferPlan BuildPlan(IReadOnlyList<FileInfo> files, long targetBytes, bool repeated)
    {
        IReadOnlyList<FileInfo> sourceFiles = repeated ? [files.OrderByDescending(file => file.Length).First()] : files;
        var sequence = new List<FileInfo>();
        long bytes = 0;
        for (var index = 0; bytes < targetBytes; index++)
        {
            var file = sourceFiles[index % sourceFiles.Count];
            sequence.Add(file);
            bytes += file.Length;
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[TransferBufferSize];
        foreach (var file in sequence)
        {
            using var input = file.OpenRead();
            int count;
            while ((count = input.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, count));
        }
        return new(sequence, bytes, hash.GetHashAndReset(), repeated ? $"Repeated {sourceFiles[0].Name}" : "Mixed DLL set");
    }

    private static async Task<ScenarioResult> MeasureScenarioAsync(Scenario scenario, TransferPlan plan, SuiteOptions options)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var errors = new ConcurrentQueue<string>();
        void Log(TunnelEvent entry)
        {
            if (entry.Level == "error") errors.Enqueue($"{entry.Forward}: {entry.Message} {entry.Exception?.Message}".Trim());
        }

        var sink = new TcpListener(IPAddress.Loopback, 0);
        sink.Start(4);
        var sinkPort = ((IPEndPoint)sink.LocalEndpoint).Port;
        Task? serverTask = null;
        Task? clientTask = null;
        try
        {
            var server = new Listener(CreateOptions("benchmark-server", TunnelMode.Server, sinkPort, scenario, options.Execution), Log);
            serverTask = server.Start(stop.Token);
            var serverEndpoint = await server.Ready.WaitAsync(stop.Token).ConfigureAwait(false);
            var client = new Listener(CreateOptions("benchmark-client", TunnelMode.Client, serverEndpoint.Port, scenario, options.Execution), Log);
            clientTask = client.Start(stop.Token);
            var clientEndpoint = await client.Ready.WaitAsync(stop.Token).ConfigureAwait(false);

            for (var warmup = 0; warmup < options.Warmups; warmup++)
                await MeasureOnceAsync(sink, clientEndpoint, plan, stop.Token).ConfigureAwait(false);
            var rates = new double[options.Iterations];
            for (var iteration = 0; iteration < rates.Length; iteration++)
                rates[iteration] = await MeasureOnceAsync(sink, clientEndpoint, plan, stop.Token).ConfigureAwait(false);
            Array.Sort(rates);
            return new(
                scenario.Name,
                scenario.Codec.ToString(),
                scenario.Encryption.ToString(),
                scenario.Settings,
                plan.Description,
                plan.Bytes,
                rates,
                rates[^1],
                Median(rates));
        }
        catch (Exception ex)
        {
            var detail = errors.IsEmpty ? string.Empty : $" Tunnel errors: {string.Join(" | ", errors)}";
            throw new InvalidOperationException($"Scenario '{scenario.Name}' failed.{detail}", ex);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            sink.Stop();
            var tasks = new[] { clientTask, serverTask }.Where(task => task is not null).Cast<Task>().ToArray();
            if (tasks.Length > 0)
            {
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
    }

    private static ForwardOptions CreateOptions(string name, TunnelMode mode, int remotePort, Scenario scenario, ExecutionMode execution) => new()
    {
        Name = name,
        Mode = mode,
        ListenAddress = IPAddress.Loopback.ToString(),
        ListenPort = 0,
        RemoteHost = IPAddress.Loopback.ToString(),
        RemotePort = remotePort,
        Compression = scenario.Codec,
        Encryption = scenario.Encryption == EncryptionAlgorithm.None ? new() : new()
        {
            Algorithm = scenario.Encryption,
            Key = mode == TunnelMode.Client ? BenchmarkKey : null,
            Keys = mode == TunnelMode.Server ? new() { ["default"] = BenchmarkKey } : null
        },
        CompressionLevel = scenario.CompressionLevel,
        BrotliQuality = scenario.BrotliQuality,
        BrotliWindow = scenario.BrotliWindow,
        ZstandardLevel = scenario.ZstandardLevel,
        CompressionHistory = scenario.History,
        BufferSize = TransferBufferSize,
        BatchMilliseconds = 0,
        HeartbeatMilliseconds = 0,
        HandshakeTimeoutMilliseconds = 30000,
        MaxConnections = 4,
        Execution = execution,
        Retry = new()
        {
            Attempts = 10,
            ConnectTimeoutMilliseconds = 5000,
            InitialDelayMilliseconds = 10,
            MaxDelayMilliseconds = 100,
            Jitter = false
        },
        Socket = new() { NoDelay = true, KeepAlive = true }
    };

    private static async Task<double> MeasureOnceAsync(TcpListener sink, IPEndPoint clientEndpoint, TransferPlan plan, CancellationToken token)
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiveTask = ReceiveAsync(sink, plan.Bytes, plan.Sha256, accepted, token);
        using var source = new TcpClient { NoDelay = true };
        await source.ConnectAsync(clientEndpoint.Address, clientEndpoint.Port, token).ConfigureAwait(false);
        await accepted.Task.WaitAsync(token).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var stream = source.GetStream();
        foreach (var file in plan.Files)
        {
            await using var input = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                TransferBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(stream, TransferBufferSize, token).ConfigureAwait(false);
        }
        await stream.FlushAsync(token).ConfigureAwait(false);
        source.Client.Shutdown(SocketShutdown.Send);
        var received = await receiveTask.ConfigureAwait(false);
        stopwatch.Stop();
        if (received != plan.Bytes) throw new InvalidDataException($"Expected {plan.Bytes} bytes; received {received}.");
        return ToMiB(plan.Bytes) / stopwatch.Elapsed.TotalSeconds;
    }

    private static async Task<long> ReceiveAsync(
        TcpListener sink,
        long expected,
        byte[] expectedHash,
        TaskCompletionSource accepted,
        CancellationToken token)
    {
        using var destination = await sink.AcceptTcpClientAsync(token).ConfigureAwait(false);
        destination.NoDelay = true;
        accepted.TrySetResult();
        var buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var stream = destination.GetStream();
            while (received < expected)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (count == 0) break;
                hash.AppendData(buffer.AsSpan(0, count));
                received += count;
                if (received > expected) throw new InvalidDataException("Destination received more bytes than expected.");
            }
            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(expectedHash)) throw new InvalidDataException("Destination payload hash mismatch.");
            if (await stream.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0) throw new InvalidDataException("Unexpected trailing data.");
            return received;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task WriteOutputsAsync(BenchmarkReport report)
    {
        var markdownPath = Path.GetFullPath(report.Options.MarkdownOutput);
        var jsonPath = Path.GetFullPath(report.Options.JsonOutput);
        var svgPath = Path.GetFullPath(report.Options.SvgOutput);
        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(svgPath)!);
        await File.WriteAllTextAsync(markdownPath, CreateMarkdown(report), new UTF8Encoding(false)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(new
        {
            generatedAtUtc = report.GeneratedAt,
            machine = report.Machine,
            methodology = new
            {
                transport = "Application → Client → Server → destination sink over IPv4 loopback",
                execution = report.Options.Execution.ToString(),
                bufferBytes = TransferBufferSize,
                batchMilliseconds = 0,
                warmups = report.Options.Warmups,
                iterations = report.Options.Iterations,
                targetMiB = report.Options.TargetMiB,
                reportedValue = "Median application-data throughput; peak retained to show the highest observed run"
            },
            files = report.Files.Select(file => new { name = file.Name, bytes = file.Bytes, sha256 = file.Sha256 }),
            results = report.Results.Select(result => new
            {
                name = result.Name,
                codec = result.Codec,
                encryption = result.Encryption,
                settings = result.Settings,
                dataSet = result.DataSet,
                transferredBytes = result.TransferredBytes,
                bestMiBPerSecond = Math.Round(result.BestMiBPerSecond, 1),
                medianMiBPerSecond = Math.Round(result.MedianMiBPerSecond, 1),
                measurementsMiBPerSecond = result.MeasurementsMiBPerSecond.Select(value => Math.Round(value, 1))
            })
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json + Environment.NewLine, new UTF8Encoding(false)).ConfigureAwait(false);
        await File.WriteAllTextAsync(svgPath, CreateSvg(report), new UTF8Encoding(false)).ConfigureAwait(false);
    }

    private static string CreateMarkdown(BenchmarkReport report)
    {
        var text = new StringBuilder();
        var tick = (char)96;
        var fence = new string(tick, 3);
        var none = report.Results.Single(result => result.Codec == Codec.None.ToString() && result.Encryption == EncryptionAlgorithm.None.ToString());
        var zstandardFast = report.Results.Single(result => result.Name == "Zstandard -5");
        text.AppendLine("# End-to-end throughput benchmarks").AppendLine();
        text.AppendLine($"Measured {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC on {report.Machine.Processor} ({report.Machine.Architecture}, {report.Machine.LogicalProcessors} logical processors) using {report.Machine.Framework}.");
        text.AppendLine();
        text.AppendLine("These loopback results measure the complete Application → Client → Server → destination path. They isolate framing, copying, compression, authenticated encryption and destination hashing cost; they do not predict throughput across a particular network.");
        text.AppendLine();
        text.AppendLine("## Interpretation").AppendLine();
        text.AppendLine("| Question | Recorded evidence | Interpretation |");
        text.AppendLine("| --- | --- | --- |");
        text.AppendLine($"| How does Zstandard -5 compare with uncompressed? | Median {Format(zstandardFast.MedianMiBPerSecond)} vs {Format(none.MedianMiBPerSecond)} MiB/s ({Difference(zstandardFast.MedianMiBPerSecond, none.MedianMiBPerSecond)}) | This run's median determines the displayed rank; it does not establish a universal ordering. |");
        text.AppendLine($"| What does peak add? | Peak {Format(zstandardFast.BestMiBPerSecond)} vs {Format(none.BestMiBPerSecond)} MiB/s ({Difference(zstandardFast.BestMiBPerSecond, none.BestMiBPerSecond)}) | Peak is one observation per profile and shows a transient ceiling rather than expected throughput. |");
        text.AppendLine("| Why can compression help without a bandwidth cap? | Loopback has no external link cap. | Bytes still incur framing, managed/native copies, TCP-buffer work, and scheduling. A fast codec can reduce that work enough to offset its CPU cost on compressible input. |");
        text.AppendLine("| Which value should be compared? | Median is the primary result; peak is retained separately. | Median is less sensitive to scheduler and cache outliers. Neither value predicts a real network without representative data and conditions. |");
        text.AppendLine();
        text.AppendLine("## Graph").AppendLine();
        text.AppendLine("![Grouped median and peak throughput for every compression and encryption profile](website/benchmarks.svg)");
        text.AppendLine();
        text.AppendLine("## Results").AppendLine();
        text.AppendLine("| Rank | Profile | Settings | Input | Median MiB/s | Peak MiB/s |");
        text.AppendLine("| ---: | --- | --- | --- | ---: | ---: |");
        var ranked = report.Results.OrderByDescending(result => result.MedianMiBPerSecond).ToArray();
        for (var index = 0; index < ranked.Length; index++)
        {
            var result = ranked[index];
            text.AppendLine($"| {index + 1} | {result.Name} | {result.Settings} | {result.DataSet} | {Format(result.MedianMiBPerSecond)} | {Format(result.BestMiBPerSecond)} |");
        }

        text.AppendLine().AppendLine("## Input files").AppendLine();
        text.AppendLine("| File | Size (MiB) | SHA-256 |");
        text.AppendLine("| --- | ---: | --- |");
        foreach (var file in report.Files)
            text.AppendLine($"| {file.Name} | {Format(ToMiB(file.Bytes))} | {tick}{file.Sha256}{tick} |");

        text.AppendLine().AppendLine("## Methodology").AppendLine();
        text.AppendLine("| Parameter | Value |");
        text.AppendLine("| --- | --- |");
        text.AppendLine($"| Platform | {report.Machine.OperatingSystem} |");
        text.AppendLine($"| Execution | {report.Options.Execution}; 1 MiB tunnel and file-copy buffers; zero application batching delay |");
        text.AppendLine($"| Runs | {report.Options.Warmups} warm-up(s), then {report.Options.Iterations} measured transfer(s) of at least {report.Options.TargetMiB} MiB per profile |");
        text.AppendLine("| Reported speed | Median application-data rate is primary; peak is the highest observed run |");
        text.AppendLine("| Brotli input | Largest selected DLL repeated on one connection; history-enabled and disabled profiles receive the identical sequence |");
        text.AppendLine("| Other input | Selected DLLs cycled in order; warm-ups populate the OS page cache |");
        text.AppendLine("| Validation | Sink verifies exact byte count, SHA-256 digest and half-close; encrypted profiles use TTN3 with the stated cipher after compression |");
        text.AppendLine("| Limitation | Tunnel-wire byte count is not recorded, so codec throughput differences cannot be decomposed into compression ratio and processing cost |");

        text.AppendLine().AppendLine("## Reproduce").AppendLine();
        text.AppendLine($"Run from the repository root on Windows with the SDK pinned by {tick}global.json{tick}:").AppendLine();
        text.AppendLine(fence + "powershell");
        text.AppendLine($"dotnet run --project src/Tedd.TcpTunnel.Benchmarks -c Release -- --throughput --target-mib {report.Options.TargetMiB} --warmups {report.Options.Warmups} --iterations {report.Options.Iterations} --output benchmarks.md --json-output website/benchmarks.json --svg-output website/benchmarks.svg");
        text.AppendLine(fence);
        text.AppendLine();
        text.AppendLine($"Use repeated {tick}--file PATH{tick} arguments to supply a different corpus. On non-Windows systems, at least one {tick}--file{tick} is required.");
        return text.ToString();
    }

    private static string CreateSvg(BenchmarkReport report)
    {
        const int width = 1200;
        const int left = 230;
        const int right = 95;
        const int top = 118;
        const int rowHeight = 34;
        const int bottom = 64;
        var ranked = report.Results.OrderByDescending(result => result.MedianMiBPerSecond).ToArray();
        var height = top + ranked.Length * rowHeight + bottom;
        var chartWidth = width - left - right;
        var axisMaximum = Math.Max(25, Math.Ceiling(ranked.Max(result => result.BestMiBPerSecond) / 25) * 25);
        var text = new StringBuilder();
        text.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-labelledby=\"title description\">");
        text.AppendLine("<title id=\"title\">Median and peak end-to-end TcpTunnel throughput</title>");
        text.AppendLine("<desc id=\"description\">Grouped horizontal bars compare median and peak application-data throughput in mebibytes per second for all measured compression and encryption profiles.</desc>");
        text.AppendLine("<rect width=\"100%\" height=\"100%\" rx=\"12\" fill=\"#ffffff\"/>");
        text.AppendLine("<style>text{font-family:Arial,sans-serif;fill:#13233b}.title{font-size:24px;font-weight:700}.subtitle,.tick{font-size:12px;fill:#667085}.label{font-size:13px;font-weight:600}.legend{font-size:12px;font-weight:600}.grid{stroke:#d9e1ec;stroke-width:1}.axis{stroke:#9aa8ba;stroke-width:1}</style>");
        text.AppendLine("<text class=\"title\" x=\"28\" y=\"38\">End-to-end throughput by profile</text>");
        text.AppendLine("<text class=\"subtitle\" x=\"28\" y=\"61\">Median is the primary comparison; peak is the highest observed run.</text>");
        text.AppendLine("<rect x=\"760\" y=\"30\" width=\"18\" height=\"9\" rx=\"3\" fill=\"#255de8\"/><text class=\"legend\" x=\"786\" y=\"39\">Median</text>");
        text.AppendLine("<rect x=\"865\" y=\"30\" width=\"18\" height=\"9\" rx=\"3\" fill=\"#9bb6f2\"/><text class=\"legend\" x=\"891\" y=\"39\">Peak</text>");

        for (var index = 0; index <= 5; index++)
        {
            var value = axisMaximum * index / 5;
            var x = left + chartWidth * index / 5d;
            text.AppendLine($"<line class=\"grid\" x1=\"{Svg(x)}\" y1=\"{top - 13}\" x2=\"{Svg(x)}\" y2=\"{height - bottom + 5}\"/>");
            text.AppendLine($"<text class=\"tick\" x=\"{Svg(x)}\" y=\"{height - 24}\" text-anchor=\"middle\">{value:0} MiB/s</text>");
        }

        for (var index = 0; index < ranked.Length; index++)
        {
            var result = ranked[index];
            var y = top + index * rowHeight;
            var medianWidth = chartWidth * result.MedianMiBPerSecond / axisMaximum;
            var peakWidth = chartWidth * result.BestMiBPerSecond / axisMaximum;
            text.AppendLine($"<text class=\"label\" x=\"{left - 14}\" y=\"{y + 14}\" text-anchor=\"end\">{Xml(result.Name)}</text>");
            text.AppendLine($"<rect x=\"{left}\" y=\"{y + 3}\" width=\"{Svg(medianWidth)}\" height=\"9\" rx=\"3\" fill=\"#255de8\"/>");
            text.AppendLine($"<rect x=\"{left}\" y=\"{y + 16}\" width=\"{Svg(peakWidth)}\" height=\"9\" rx=\"3\" fill=\"#9bb6f2\"/>");
        }

        text.AppendLine($"<line class=\"axis\" x1=\"{left}\" y1=\"{height - bottom + 5}\" x2=\"{width - right}\" y2=\"{height - bottom + 5}\"/>");
        text.AppendLine("</svg>");
        return text.ToString();
    }

    private static double Median(IReadOnlyList<double> sorted) => sorted.Count % 2 == 0
        ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2
        : sorted[sorted.Count / 2];

    private static double ToMiB(long bytes) => bytes / (1024d * 1024d);
    private static string Format(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Difference(double value, double baseline) =>
        ((value / baseline - 1) * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
    private static string Svg(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Xml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private sealed record Scenario(
        string Name,
        Codec Codec,
        string Settings,
        CompressionLevel CompressionLevel = CompressionLevel.Fastest,
        int BrotliQuality = 4,
        int BrotliWindow = 20,
        int ZstandardLevel = 3,
        bool History = false,
        bool RepeatFile = false,
        EncryptionAlgorithm Encryption = EncryptionAlgorithm.None);

    private sealed record TransferPlan(IReadOnlyList<FileInfo> Files, long Bytes, byte[] Sha256, string Description);
    private sealed record FileSample(string Name, long Bytes, string Sha256);
    private sealed record MachineInformation(string OperatingSystem, string Architecture, string Processor, int LogicalProcessors, string Framework);
    private sealed record ScenarioResult(
        string Name,
        string Codec,
        string Encryption,
        string Settings,
        string DataSet,
        long TransferredBytes,
        IReadOnlyList<double> MeasurementsMiBPerSecond,
        double BestMiBPerSecond,
        double MedianMiBPerSecond);
    private sealed record BenchmarkReport(
        DateTimeOffset GeneratedAt,
        MachineInformation Machine,
        SuiteOptions Options,
        IReadOnlyList<FileSample> Files,
        IReadOnlyList<ScenarioResult> Results);

    private sealed record SuiteOptions(
        int TargetMiB,
        int Warmups,
        int Iterations,
        int TimeoutSeconds,
        ExecutionMode Execution,
        string MarkdownOutput,
        string JsonOutput,
        string SvgOutput,
        IReadOnlyList<string> Files,
        bool Help)
    {
        public static SuiteOptions Parse(string[] args)
        {
            var targetMiB = 64;
            var warmups = 1;
            var iterations = 7;
            var timeoutSeconds = 600;
            var execution = ExecutionMode.Async;
            var markdown = "benchmarks.md";
            var json = "website/benchmarks.json";
            var svg = "website/benchmarks.svg";
            var files = new List<string>();
            var help = false;

            string Value(ref int index)
            {
                if (++index >= args.Length) throw new ArgumentException($"Missing value for {args[index - 1]}.");
                return args[index];
            }

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--target-mib": targetMiB = Positive(Value(ref index), "target-mib"); break;
                    case "--warmups": warmups = NonNegative(Value(ref index), "warmups"); break;
                    case "--iterations": iterations = Positive(Value(ref index), "iterations"); break;
                    case "--timeout-seconds": timeoutSeconds = Positive(Value(ref index), "timeout-seconds"); break;
                    case "--execution":
                        if (!Enum.TryParse<ExecutionMode>(Value(ref index), true, out execution) || !Enum.IsDefined(execution))
                            throw new ArgumentException("Execution must be Async or Dedicated.");
                        break;
                    case "--output": markdown = Value(ref index); break;
                    case "--json-output": json = Value(ref index); break;
                    case "--svg-output": svg = Value(ref index); break;
                    case "--file": files.Add(Value(ref index)); break;
                    case "--help" or "-h": help = true; break;
                    default: throw new ArgumentException($"Unknown throughput option: {args[index]}");
                }
            }
            return new(targetMiB, warmups, iterations, timeoutSeconds, execution, markdown, json, svg, files, help);
        }

        private static int Positive(string value, string name) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"{name} must be a positive integer.");

        private static int NonNegative(string value, string name) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
                ? parsed
                : throw new ArgumentException($"{name} must be a non-negative integer.");

        public const string HelpText = """
End-to-end TcpTunnel throughput suite

dotnet run --project src/Tedd.TcpTunnel.Benchmarks -c Release -- --throughput

Options:
  --target-mib N       Minimum mebibytes transferred per profile (default: 64)
  --warmups N          Unmeasured transfers before each profile (default: 1)
  --iterations N       Measured transfers per profile (default: 7)
  --timeout-seconds N  Per-profile timeout (default: 600)
  --execution MODE     Async or Dedicated (default: Async)
  --file PATH          Input file; repeat for a multi-file corpus
  --output PATH        Markdown report (default: benchmarks.md)
  --json-output PATH   Website data (default: website/benchmarks.json)
  --svg-output PATH    SVG graph (default: website/benchmarks.svg)
""";
    }
}
