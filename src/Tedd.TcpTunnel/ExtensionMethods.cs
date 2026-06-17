using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tedd.TcpTunnel
{
    public static class ExtensionMethods
    {
        /// <summary>
        /// Copies the contents of the source stream to the destination stream and flushes after each read.
        /// Complexity:
        /// Time: O(N) where N is the number of bytes in the stream.
        /// Space: O(1) auxiliary space (uses an ArrayPool buffer instead of allocating a new array).
        /// </summary>
        public static async Task CopyToAsyncWithFlush(this Stream source, Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (bufferSize < 1)
                bufferSize = 81920;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                var bytesRead = -1;
                while (bytesRead != 0 && !cancellationToken.IsCancellationRequested)
                {
                    bytesRead = await source.ReadAsync(buffer, 0, bufferSize, cancellationToken);
                    if (bytesRead == 0)
                        continue;

                    await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
