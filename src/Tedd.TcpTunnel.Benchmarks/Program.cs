using BenchmarkDotNet.Running;
namespace Tedd.TcpTunnel.Benchmarks;
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--throughput", .. var throughputArgs])
            return await ThroughputSuite.RunAsync(throughputArgs).ConfigureAwait(false);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
