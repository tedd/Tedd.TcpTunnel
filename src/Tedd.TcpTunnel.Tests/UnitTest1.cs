using Microsoft.VisualStudio.TestPlatform.TestHost;
using System;
using System.Collections.Generic;
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

            // Generate some random data for test
            var state = new SharedState();
            state.BinData1 = new ();
            state.BinData2 = new ();
            var rnd = new Random();
            for (var i = 1; i < 1_00; i ++) {
                var bytes1 = new byte[i*1000];
                rnd.NextBytes(bytes1);
                state.BinData1.Add(bytes1);

                var bytes2 = new byte[i*1000];
                rnd.NextBytes(bytes2);
                state.BinData2.Add(bytes2);
            }

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
            //using var sr = new StreamReader(ns);
            //using var sw = new StreamWriter(ns);
            for (var i = 0; i < state.BinData1.Count; i++)
            {
                var sd = state.BinData1[i];
                var rd = state.BinData2[i];
                Debug.WriteLine($"Client sending {sd.Length}");
                await ns.WriteAsync(sd);
                //await sw.WriteLineAsync("Test1");
                await ns.FlushAsync();

                var pos = 0;
                var buffer = new byte[rd.Length];
                while (pos < sd.Length) 
                    pos +=  ns.Read(buffer, pos, buffer.Length - pos);
                Assert.Equal(rd, buffer);
            }
            ns.Close();
        }

        private async Task ReceiverServer(SharedState state, int port)
        {
            var server = new TcpListener(IPAddress.Any, port);
            server.Start();
            using var client = await server.AcceptTcpClientAsync();
            using var ns = client.GetStream();

            for (var i = 0; i < state.BinData1.Count; i++)
            {
                var sd = state.BinData2[i];
                var rd = state.BinData1[i];
                Debug.WriteLine($"Server sending {sd.Length}");
                await ns.WriteAsync(sd);
                //await sw.WriteLineAsync("Test1");
                await ns.FlushAsync();

                var pos = 0;
                var buffer = new byte[rd.Length];
                while (pos < sd.Length)
                    pos += ns.Read(buffer, pos, buffer.Length - pos);
                Assert.Equal(rd, buffer);
            }
            ns.Close();

            server.Stop();
        }
    }
}
