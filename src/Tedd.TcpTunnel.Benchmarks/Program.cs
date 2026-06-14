using BenchmarkDotNet.Running;

namespace Tedd.TcpTunnel.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<StreamCopyBenchmark>();
        }
    }
}
