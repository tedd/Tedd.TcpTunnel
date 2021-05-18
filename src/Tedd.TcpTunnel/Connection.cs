using System;
using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Tedd.TcpTunnel
{
    internal class Connection
    {
        private readonly Socket _socket1;
        private readonly Socket _socket2;
        private readonly int _bufferSize = 4096 * 10;
        private bool IsOpen = false;

        public Connection(Socket socket1, Socket socket2)
        {
            _socket1 = socket1;
            _socket2 = socket2;
        }

        internal void Start()
        {
            try
            {
                IsOpen = true;
                using var cancellationTokenSource = new CancellationTokenSource();
                Task.WaitAll(
                    ProcessLinesAsync(_socket1, _socket2, cancellationTokenSource),
                    ProcessLinesAsync(_socket2, _socket1, cancellationTokenSource)
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

        private async Task ProcessLinesAsync(Socket socket1, Socket socket2, CancellationTokenSource cancellationTokenSource)
        {
            var pipe = new Pipe();
            Task writing = FillPipeAsync(socket1, pipe.Writer, cancellationTokenSource);
            Task reading = ReadPipeAsync(socket2, pipe.Reader, cancellationTokenSource);

            await Task.WhenAny(reading, writing);
            cancellationTokenSource.Cancel();
            await Task.WhenAll(reading, writing);

        }

        private async Task FillPipeAsync(Socket socket, PipeWriter writer, CancellationTokenSource cancellationTokenSource)
        {
            //const int minimumBufferSize = 4096;

            while (true)
            {
                // Allocate at least 512 bytes from the PipeWriter
                Memory<byte> memory = writer.GetMemory(_bufferSize);
                try
                {
                    int bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None, cancellationTokenSource.Token);
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
            IsOpen = false;
            Listener.Debug("Socket closed.");
        }

        private async Task ReadPipeAsync(Socket socket, PipeReader reader, CancellationTokenSource cancellationTokenSource)
        {
            var tempBuffer = new byte[_bufferSize + 4986];
            //var brotliEncoder = new BrotliEncoder(5,16);
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationTokenSource.Token);

                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition? position = null;

                //do
                {
                    //BrotliEncoder.TryCompress(buffer, tempBuffer, out var length, 5, 16);
                    if (SequenceMarshal.TryGetReadOnlyMemory(buffer, out var memory))
                    {
                        await socket.SendAsync(memory, SocketFlags.None);
                        position = buffer.End;
                    }

                    if (position != null)
                    {
                        // Skip the line + the \n character (basically position)
                        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                    }
                }
                //while (position != null);

                // Tell the PipeReader how much of the buffer we have consumed
                reader.AdvanceTo(buffer.Start, buffer.End);

                // Stop reading if there's no more data coming
                if (result.IsCompleted || !IsOpen)
                {
                    break;
                }
            }

            // Mark the PipeReader as complete
            reader.Complete();
        }
    }
}