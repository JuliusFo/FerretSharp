namespace FerretSharp.Core.Connections;

public enum TransactionMode
{
    /// <summary>No transaction: every query sees the latest committed data (v1 behavior).</summary>
    None,

    /// <summary>
    /// <c>SET TRANSACTION READ ONLY</c> (locked profiles): Oracle rejects DML (ORA-01456), and every query sees the
    /// data as of <see cref="TransactionInfo.StartedAt"/> (snapshot) until the transaction is restarted.
    /// </summary>
    ReadOnly,

    /// <summary>Explicit transaction that may write; nothing is committed until <c>Commit</c> (v2, WP-09).</summary>
    ReadWrite,
}

/// <summary>Transaction of a session, for the status line and the warning on a lost connection.</summary>
public sealed record TransactionInfo(TransactionMode Mode, DateTimeOffset? StartedAt)
{
    public static readonly TransactionInfo None = new(TransactionMode.None, null);
}
