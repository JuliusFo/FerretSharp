namespace FerretSharp.Core.Connections;

/// <param name="ErrorCode">Oracle error code like <c>ORA-01017</c>, if the failure came from Oracle.</param>
public sealed record ConnectionTestResult(bool Success, TimeSpan Elapsed, string? ServerVersion, string? ErrorCode, string? Message)
{
    public static ConnectionTestResult Ok(TimeSpan elapsed, string serverVersion) => new(true, elapsed, serverVersion, null, null);

    public static ConnectionTestResult Failed(TimeSpan elapsed, string message, string? errorCode = null) =>
        new(false, elapsed, null, errorCode, message);
}

public interface IConnectionTester
{
    /// <summary>Opens a short-lived session and runs <c>SELECT 1 FROM DUAL</c>. Throws only on cancellation.</summary>
    Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, string password, CancellationToken cancellationToken);
}
