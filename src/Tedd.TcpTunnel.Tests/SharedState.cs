using System.Collections.Generic;

namespace Tedd.TcpTunnel.Tests
{
    internal class SharedState
    {
        internal List<byte[]> BinData1;
        internal List<byte[]> BinData2;

        public SharedState()
        {
        }
    }
}