using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Tedd.TcpTunnel.Archive;
using Tedd.TcpTunnel;

namespace Tedd.TcpTunnel.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob(RuntimeMoniker.Net80)]
    public class StreamCopyBenchmark
    {
        private const int TotalBytes = 10 * 1024 * 1024; // 10MB
        private const int BufferSize = 81920;

        private MemoryStream _sourceStream;
        private MemoryStream _destinationStream;
        private CancellationTokenSource _cts;

        [GlobalSetup]
        public void Setup()
        {
            byte[] data = new byte[TotalBytes];
            for (int i = 0; i < TotalBytes; i++) data[i] = (byte)(i % 256);
            _sourceStream = new MemoryStream(data);
            _destinationStream = new MemoryStream(TotalBytes);
            _cts = new CancellationTokenSource();
        }

        [IterationSetup]
        public void IterationSetup()
        {
            _sourceStream.Position = 0;
            _destinationStream.Position = 0;
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _sourceStream?.Dispose();
            _destinationStream?.Dispose();
            _cts?.Dispose();
        }

        [Benchmark(Baseline = true)]
        public async Task LegacyCopyToAsyncWithFlush()
        {
            await LegacyExtensionMethods.CopyToAsyncWithFlush(_sourceStream, _destinationStream, BufferSize, _cts.Token);
        }

        [Benchmark]
        public async Task OptimizedCopyToAsyncWithFlush()
        {
            await ExtensionMethods.CopyToAsyncWithFlush(_sourceStream, _destinationStream, BufferSize, _cts.Token);
        }
    }
}
