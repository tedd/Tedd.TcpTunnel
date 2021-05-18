using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Tedd.TcpTunnel
{
    public class Listener
    {
        private readonly string _listenAddress;
        private readonly int _listenPort;
        private readonly string _remoteHost;
        private readonly int _remotePort;
        private readonly Random _rnd = new();
        private readonly ConcurrentDictionary<Connection, byte> _connections = new();

        public Listener(string listenAddress, int listenPort, string remoteHost, int remotePort)
        {
            _listenAddress = listenAddress;
            _listenPort = listenPort;
            _remoteHost = remoteHost;
            _remotePort = remotePort;
        }

        static internal void Debug(string line) => Console.WriteLine($"[Debug] {line}");
        static internal void Error(string line) => Console.WriteLine($"[Error] {line}");
        static internal void Info(string line) => Console.WriteLine($"[Info] {line}");

        public async Task Start(CancellationToken cancellationToken)
        {
            // Resolve listening address and port
            IPAddress ipAddress = IPAddress.Any;
            try
            {
                if (!string.IsNullOrWhiteSpace(_listenAddress))
                    ipAddress = await ResolveAddress(_listenAddress);
            }
            catch (Exception exception)
            {
                Error($"Error connecting to remote host: {exception.Message}");
            }
            Socket listener = null;
            try
            {
                // Set up listening
                var localEndPoint = new IPEndPoint(ipAddress, _listenPort);
                listener = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(localEndPoint);
                listener.Listen(10);
            }
            catch (Exception exception)
            {
                Error($"Error listening on {ipAddress}:{_listenPort} host: {exception.Message}");
            }
            Info($"Info listening on {ipAddress}:{_listenPort}...");
            // Wait for incomping connection
            while (!cancellationToken.IsCancellationRequested)
            {
                // Accept connection
                var listenSocket = await listener.AcceptAsync();
                Socket remoteSocket = null;
                try
                {
                    remoteSocket = await RemoteConnect();
                }
                catch (Exception exception)
                {
                    Error($"Error connecting to remote host: {exception.Message}");
                }

                // Create connection handler and execute in background
                var connection = new Connection(listenSocket, remoteSocket);
                Task.Run(() => connection.Start());
            }
        }

        private async Task<Socket> RemoteConnect()
        {
            var remoteIp = await ResolveAddress(_remoteHost);
            var remoteEP = new IPEndPoint(remoteIp, _remotePort);

            // Set up socket
            var remoteSocket = new Socket(remoteEP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            // Connect
            Debug($"Connecting to {remoteIp}:{_remotePort}");
            await remoteSocket.ConnectAsync(remoteEP);
            return remoteSocket;
        }

        private async Task<IPAddress> ResolveAddress(string address)
        {
            if (IPAddress.TryParse(_remoteHost, out var ip))
                return ip;

            Debug($"Resolving {address}");
            // Resolve hostname
            var ipHostInfo = await Dns.GetHostEntryAsync(address);
            // Puck a random ip from what was returned
            Debug($"Resolved {address} to {string.Join(", ", ipHostInfo.AddressList.Select(s=>s.ToString()))}");
            IPAddress result = null;
            lock (_rnd)
                result= ipHostInfo.AddressList[_rnd.Next(0, ipHostInfo.AddressList.Length)];
            Debug($"Resolved {address} to {result.ToString()}");
            return result;
        }


    }
}
