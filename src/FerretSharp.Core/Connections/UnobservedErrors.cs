using System.Net.Sockets;

namespace FerretSharp.Core.Connections;

/// <summary>What an unobserved task exception is (the app host's <c>TaskScheduler.UnobservedTaskException</c>).</summary>
public static class UnobservedErrors
{
    /// <summary>
    /// Only aborted socket operations (<see cref="SocketError.OperationAborted"/>, 995): after an open that ran into its
    /// timeout (address not reachable, 15 s), ODP.NET leaves a socket task nobody observes; its abort was an <c>[ERR]</c> in
    /// the log shortly after (E2E 3.33, also without cancelling). Harmless – a warning, not an error (fix/robustness).
    /// </summary>
    public static bool IsAbortedSocket(Exception exception)
    {
        IEnumerable<Exception> errors = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception];
        return errors.Any() && errors.All(e => Unwrapped(e) is SocketException { SocketErrorCode: SocketError.OperationAborted });
    }

    /// <summary>The socket error under wrappers (<see cref="IOException"/> of a stream, the driver's own).</summary>
    private static Exception Unwrapped(Exception exception)
    {
        var current = exception;
        while (current is not SocketException && current.InnerException is { } inner)
        {
            current = inner;
        }

        return current;
    }
}
