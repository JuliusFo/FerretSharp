using System.Net.Sockets;
using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Connections;

/// <summary>fix/robustness: ODP.NET's aborted socket task after an open that timed out was an [ERR] in the log.</summary>
public sealed class UnobservedErrorsTests
{
    private static SocketException Aborted => new((int)SocketError.OperationAborted);

    public static TheoryData<Exception, bool> Cases => new()
    {
        { new AggregateException(Aborted), true },
        { new AggregateException(new IOException("read failed", Aborted)), true }, // under a stream's IOException
        { new AggregateException(Aborted, new IOException("read failed", Aborted)), true },
        { new AggregateException(new SocketException((int)SocketError.ConnectionReset)), false }, // another socket error stays an error
        { new AggregateException(Aborted, new InvalidOperationException("bug")), false }, // anything else in it, too
        { new AggregateException(), false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Only_aborted_socket_operations_are_harmless(Exception exception, bool harmless)
    {
        Assert.Equal(harmless, UnobservedErrors.IsAbortedSocket(exception));
    }
}
