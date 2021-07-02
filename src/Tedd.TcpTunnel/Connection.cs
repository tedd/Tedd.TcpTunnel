using K4os.Compression.LZ4.Streams;
using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Tedd.TcpTunnel
{
    internal class Connection
    {
        private readonly TcpTunnelSettings _settings;
        private readonly Socket _socket1;
        private readonly Socket _socket2;
        private readonly int _bufferSize = 4096 * 10;

        public Connection(TcpTunnelSettings settings, Socket socket1, Socket socket2)
        {
            _settings = settings;
            _socket1 = socket1;
            _socket2 = socket2;
        }

        internal void Start()
        {
            try
            {
                using var cancellationTokenSource = new CancellationTokenSource();
                Task.WaitAll(
                    ProcessStreamAsync(_socket1, _socket2, cancellationTokenSource, _settings.IsClient),
                    ProcessStreamAsync(_socket2, _socket1, cancellationTokenSource, !_settings.IsClient)
                );
            }
            finally
            {
                try
                {
                    _socket1.Close(1000);
                    _socket1.Dispose();
                }
                catch (Exception exception)
                {
                    Listener.Error($"Error closing socket 1: {exception.Message}");
                }
                try
                {
                    _socket2.Close(1000);
                    _socket2.Dispose();
                }
                catch (Exception exception)
                {
                    Listener.Error($"Error closing socket 2: {exception.Message}");
                }

            }
        }

        private async Task ProcessStreamAsync(Socket socket1, Socket socket2, CancellationTokenSource cancellationTokenSource, bool isClient)
        {
            if (isClient)
            {
                var s1 = socket1;
                socket1 = socket2;
                socket2 = s1;
            }
            using var ns1 = new NetworkStream(socket1, false);
            using var ns2 = new NetworkStream(socket2, false);
            //using var ms2 = new MemoryStream();
            using var ms2 = new BufferedStream();
            //using var cs1 = new GZipStream(ns1, CompressionLevel.Optimal, true);
            //using var cs2 = new GZipStream(ms2, CompressionMode.Decompress, true);
            using var cs1 = LZ4Stream.Encode(ns1,null,true);
            using var cs2 = LZ4Stream.Decode(ms2, null, true);

            var t1 = ns2.CopyToAsyncWithFlush(cs1, 0, cancellationTokenSource.Token); //   COMPRESS: Copy from socket2->ns2 to socket1 (write to cs1->ns1->socket1)
            var t2 = ns1.CopyToAsyncWithFlush(ms2, 0, cancellationTokenSource.Token); // DECOMPRESS: Copy from socket1->ns1 to ms2 (write to ms2->cs2)
            var t3 = cs2.CopyToAsyncWithFlush(ns2, 0, cancellationTokenSource.Token); // DECOMPRESS: Copy from cs2 to socket2 (write to ns2->socket2)
            
            ns1.WriteAsync()
            // No compression
            //var t1 = ns2.CopyToAsyncWithFlush(ns1, 0, cancellationTokenSource.Token);
            //var t2 = ns1.CopyToAsyncWithFlush(ns2, 0, cancellationTokenSource.Token);

            Task.WaitAll(
                t1,
                t2,
                t3
            );
        }



    }
}