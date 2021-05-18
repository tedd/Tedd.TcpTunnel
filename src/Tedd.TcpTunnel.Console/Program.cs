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
        /// <param name="isClient">Will compress outgoing stream, set to false on server side.</param>
        static async Task<int> Main(int listenPort, string remoteHost, int remotePort, bool isClient, string listenAddress = null)
        {
            var listener = new Listener(listenAddress, listenPort, remoteHost, remotePort);
            using var cancellationTokenSource = new CancellationTokenSource();
            await listener.Start(cancellationTokenSource.Token);
            return 0;
        }
    }
}
