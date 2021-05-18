using System;
using System.Buffers;
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
        private readonly Socket _socket1;
        private readonly Socket _socket2;
        private readonly int _bufferSize = 4096 * 10;
        public bool IsClient = true;

        public Connection(Socket socket1, Socket socket2)
        {
            _socket1 = socket1;
            _socket2 = socket2;
        }

        internal void Start()
        {
            try
            {
                using var cancellationTokenSource = new CancellationTokenSource();
                Task.WaitAll(
                    ProcessStreamAsync(_socket1, _socket2, cancellationTokenSource, IsClient),
                    ProcessStreamAsync(_socket2, _socket1, cancellationTokenSource, !IsClient)
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
            var pipe = new Pipe();
            Task writing = FillPipeAsync(socket1, pipe.Writer, cancellationTokenSource);
            Task reading = ReadPipeAsync(socket2, pipe.Reader, cancellationTokenSource, isClient);

            await Task.WhenAny(reading, writing);
            cancellationTokenSource.Cancel();
            await Task.WhenAll(reading, writing);

        }

        private async Task FillPipeAsync(Socket socket, PipeWriter writer, CancellationTokenSource cancellationTokenSource)
        {
            var socketAddr = socket.RemoteEndPoint.ToString();
            //const int minimumBufferSize = 4096;

            while (true)
            {
                // Allocate at least 512 bytes from the PipeWriter
                Memory<byte> memory = writer.GetMemory(_bufferSize);
                try
                {
                    int bytesRead = 0;
                    try
                    {
                        bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None, cancellationTokenSource.Token);
                    }
                    catch (System.OperationCanceledException)
                    {
                        break;
                    } // CancellationToken
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    // Tell the PipeWriter how much was read from the Socket
                    writer.Advance(bytesRead);
                }
                catch (Exception ex)
                {
                    Listener.Error($"Remote socket read error: {ex}");
                    break;
                }

                // Make the data available to the PipeReader
                FlushResult result = await writer.FlushAsync();

                if (result.IsCompleted)
                {
                    break;
                }
            }
            // Tell the PipeReader that there's no more data coming
            writer.Complete();
            Listener.Debug($"Socket reader for socket {socketAddr} closed.");
        }

        private async Task ReadPipeAsync(Socket socket, PipeReader reader, CancellationTokenSource cancellationTokenSource, bool isClient)
        {
            var socketAddr = socket.RemoteEndPoint.ToString();
            var tempBuffer = new byte[_bufferSize + 4986];
            var tb = new ReadOnlyMemory<byte>(tempBuffer);
            using var brotliEncoder = new BrotliEncoder(5, 16);
            using var brotliDecoder = new BrotliDecoder();
            while (true)
            {
                ReadResult result = await reader.ReadAsync();

                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition? position = null;

                //do
                {
                    //BrotliEncoder.TryCompress(buffer, tempBuffer, out var length, 5, 16);
                    if (buffer.Length > 0 && SequenceMarshal.TryGetReadOnlyMemory(buffer, out var memory))
                    {
                        int bytesConsumed = 0;
                        int bytesWritten = 0;
                        if (isClient)
                            brotliEncoder.Compress(memory.Span, tempBuffer, out bytesConsumed, out bytesWritten, isFinalBlock: false);
                        else
                            brotliDecoder.Decompress(memory.Span, tempBuffer, out bytesConsumed, out bytesWritten);
                        await socket.SendAsync(tb.Slice(0, bytesWritten), SocketFlags.None);
                        position = buffer.End;
                    }

                    if (position != null)
                    {
                        // Skip the line + the \n character (basically position)
                        buffer = buffer.Slice(buffer.GetPosition(0, position.Value));
                        reader.AdvanceTo(buffer.Start, buffer.End);
                    }
                }
                //while (position != null);

                // Tell the PipeReader how much of the buffer we have consumed

                // Stop reading if there's no more data coming
                if (result.IsCompleted)
                {
                    // We need to send final block.
                    int bytesConsumed = 0;
                    int bytesWritten = 0;
                    if (isClient)
                        brotliEncoder.Compress(tb.Slice(0).Span, tempBuffer, out bytesConsumed, out bytesWritten, isFinalBlock: true);
                    else
                        brotliDecoder.Decompress(tb.Slice(0).Span, tempBuffer, out bytesConsumed, out bytesWritten);
                    await socket.SendAsync(tb.Slice(0, bytesWritten), SocketFlags.None);
                    break;
                }
            }

            // Mark the PipeReader as complete
            reader.Complete();
            Listener.Debug($"Socket writer for socket {socketAddr} done.");
        }
    }
}