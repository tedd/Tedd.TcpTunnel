using System.Buffers;

namespace Tedd.TcpTunnel;

public static class ExtensionMethods
{
    /// <summary>Copies a stream with a pooled buffer, flushing after each nonempty read.</summary>
    public static async Task CopyToAsyncWithFlush(this Stream source, Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (bufferSize < 1) bufferSize = 81920;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false)) != 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
