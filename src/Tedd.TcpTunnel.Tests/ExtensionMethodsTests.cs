using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Tedd.TcpTunnel.Tests
{
    public class ExtensionMethodsTests
    {
        [Theory]
        [InlineData(10, 5)]     // Buffer smaller than data
        [InlineData(10, 10)]    // Buffer equal to data
        [InlineData(10, 100)]   // Buffer larger than data
        [InlineData(10000, 81920)] // Large data, default buffer
        [InlineData(10, 0)]     // BufferSize 0 triggers default behavior (81920)
        [InlineData(10, -1)]    // Negative BufferSize triggers default behavior (81920)
        public async Task CopyToAsyncWithFlush_ValidInputs_CopiesDataCorrectly(int dataSize, int bufferSize)
        {
            // Arrange
            var sourceData = new byte[dataSize];
            new Random().NextBytes(sourceData);

            using var sourceStream = new MemoryStream(sourceData);
            using var destinationStream = new MemoryStream();

            // Act
            await sourceStream.CopyToAsyncWithFlush(destinationStream, bufferSize, CancellationToken.None);

            // Assert
            Assert.Equal(sourceData, destinationStream.ToArray());
        }

        [Fact]
        public async Task CopyToAsyncWithFlush_EmptySource_DoesNothing()
        {
            // Arrange
            using var sourceStream = new MemoryStream();
            using var destinationStream = new MemoryStream();

            // Act
            await sourceStream.CopyToAsyncWithFlush(destinationStream, 1024, CancellationToken.None);

            // Assert
            Assert.Empty(destinationStream.ToArray());
        }

        [Fact]
        public async Task CopyToAsyncWithFlush_CancelledToken_StopsCopyingGracefully()
        {
            // Arrange
            var sourceData = new byte[100];
            new Random().NextBytes(sourceData);
            using var sourceStream = new MemoryStream(sourceData);
            using var destinationStream = new MemoryStream();
            using var cts = new CancellationTokenSource();

            // Act
            cts.Cancel();
            await sourceStream.CopyToAsyncWithFlush(destinationStream, 10, cts.Token);

            // Assert
            Assert.Empty(destinationStream.ToArray());
        }

        [Fact]
        public async Task CopyToAsyncWithFlush_NullSourceStream_ThrowsNullReferenceException()
        {
            // Arrange
            Stream sourceStream = null;
            using var destinationStream = new MemoryStream();

            // Act & Assert
            await Assert.ThrowsAsync<NullReferenceException>(async () =>
                await sourceStream.CopyToAsyncWithFlush(destinationStream, 1024, CancellationToken.None));
        }

        [Fact]
        public async Task CopyToAsyncWithFlush_NullDestinationStream_ThrowsNullReferenceException()
        {
            // Arrange
            using var sourceStream = new MemoryStream(new byte[10]);
            Stream destinationStream = null;

            // Act & Assert
            await Assert.ThrowsAsync<NullReferenceException>(async () =>
                await sourceStream.CopyToAsyncWithFlush(destinationStream, 1024, CancellationToken.None));
        }
    }
}
