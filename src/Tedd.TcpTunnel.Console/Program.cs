using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tedd.TcpTunnel.Console
{
    class Program
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="listenAddress">Listen address</param>
        /// <param name="listenPort">Listen port</param>
        /// <param name="remoteHost">Remote host</param>
        /// <param name="remotePort">Remote port</param>
        static async Task<int> Main(int listenPort, string remoteHost, int remotePort, string listenAddress = null)
        {
            var listener = new Listener(listenAddress, listenPort, remoteHost, remotePort);
            using var cancellationTokenSource = new CancellationTokenSource();
            await listener.Start(cancellationTokenSource.Token);
            return 0;
        }
    }
}
