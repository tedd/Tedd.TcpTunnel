using Microsoft.VisualStudio.TestPlatform.TestHost;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Tedd.TcpTunnel.Tests
{
    public class TunnelTests
    {
        [Fact]
        public void TunnelTest1()
        {
            Debug.WriteLine("Setting up client 2000 -> 2001");
            var listener1 = new Listener(new TcpTunnelSettings()
            {
                ListenAddress = "",
                ListenPort = 2000,
                RemoteHost = "127.0.0.1",
                RemotePort = 2001,
                IsClient = true
            });
            using var cancellationTokenSource1 = new CancellationTokenSource();
            var task1 = listener1.Start(cancellationTokenSource1.Token);

            Debug.WriteLine("Setting up server 2001 -> 2002");
            var listener2 = new Listener(new TcpTunnelSettings()
            {
                ListenAddress = "",
                ListenPort = 2001,
                RemoteHost = "127.0.0.1",
                RemotePort = 2002,
                IsClient = false
            });
            using var cancellationTokenSource2 = new CancellationTokenSource();
            var task2 = listener2.Start(cancellationTokenSource2.Token);

            var state = new SharedState();
            Debug.WriteLine("Setting up testserver on 2002");
            var task3 = ReceiverServer(state, 2002);
            Debug.WriteLine("Setting up testclient to 2000");
            var task4 = SendClient(state, 2000);

            Task.WaitAll(new[] { task1, task2, task3, task4 }, 10_000);
        }

        private async Task SendClient(SharedState state, int port)
        {

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var ns = client.GetStream();
            using var sr = new StreamReader(ns);
            using var sw = new StreamWriter(ns);
            Debug.WriteLine("Client sending: Test1");
            await sw.WriteLineAsync("Test1");
            await sw.FlushAsync();
            sw.Close();
        }

        private async Task ReceiverServer(SharedState state, int port)
        {
            var server = new TcpListener(IPAddress.Any, port);
            server.Start();
            using var client = await server.AcceptTcpClientAsync();
            using var ns = client.GetStream();
            using var sr = new StreamReader(ns);
            using var sw = new StreamWriter(ns);
            while (client.Connected)
            {
                var line = await sr.ReadLineAsync();
                if (line == null)
                    break;
                Debug.WriteLine("Server received: " + line);
            }
            server.Stop();
        }
    }
}
