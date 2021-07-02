using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tedd.TcpTunnel
{
    internal static class ExtensionMethods
    {
        public static async Task CopyToAsyncWithFlush(this Stream source, Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (bufferSize <1)
                bufferSize = 81920;

            var buffer = new byte[bufferSize];
            var bytesRead = -1;
            while (bytesRead != 0 && !cancellationToken.IsCancellationRequested)
            {
                bytesRead = await source.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                    continue;

                await destination.WriteAsync(buffer, 0, bytesRead);
                await destination.FlushAsync();
            }
        }

    }
}
