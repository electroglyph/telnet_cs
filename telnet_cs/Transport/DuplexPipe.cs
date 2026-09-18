namespace telnet_cs.Transport;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.IO;

/// <summary>
/// In-memory linked pair of <see cref="IByteStream"/> ends for hermetic
/// tests: bytes written on one end arrive on the other with real blocking,
/// no sockets and no ports. Internal until the public
/// hermetic-transport review; tests reach it via the
/// <c>InternalsVisibleTo("telnet_cs.Tests")</c> grant.
/// </summary>
internal static class DuplexPipe
{
    /// <summary>
    /// Creates two linked ends. Bytes written on <c>A</c> are readable
    /// on <c>B</c> and vice versa. Each end logs what it wrote in
    /// <see cref="DuplexEnd.WrittenBytes"/> for assertion.
    /// </summary>
    /// <returns>The linked <c>(A, B)</c> ends.</returns>
    public static (DuplexEnd A, DuplexEnd B) Create()
    {
        var endA = new DuplexEnd();
        var endB = new DuplexEnd();
        endA.Peer = endB;
        endB.Peer = endA;
        return (endA, endB);
    }
}
