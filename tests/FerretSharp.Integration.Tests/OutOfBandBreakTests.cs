using System.Diagnostics;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Backlog "Abbrechen über VPN": a cancelled grid load over the customer VPN lost its session (ORA-12570 0.2 s after the
/// break), an earlier long query could not be cancelled. ODP.NET sends a break as TCP urgent data when the server agrees;
/// <see cref="BreakProxy"/> models a path that loses it or passes it on as data. In band, both keep the session.
/// Runs alone: it turns out-of-band breaks on in the shared container.
/// </summary>
[Collection(nameof(SerialTests))]
public sealed class OutOfBandBreakTests(OracleContainerFixture oracle)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Some seconds of work on the server without a row before the end (4 s on a developer machine).</summary>
    private const string BoundedSql =
        "SELECT COUNT(*) FROM all_objects a, (SELECT LEVEL n FROM dual CONNECT BY LEVEL <= 100) b WHERE MOD(a.object_id + b.n, 7) = 3";

    private const string EndlessSql = "SELECT COUNT(*) FROM all_objects a CROSS JOIN all_objects b CROSS JOIN all_objects c";

    /// <summary>Much longer than any cancel here takes; a hang fails instead of holding the run.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_path_that_passes_urgent_data_on_as_data_loses_the_session_on_a_break()
    {
        await using var allowed = await oracle.AllowOutOfBandBreaksAsync();
        await using var proxy = Proxy(UrgentData.PassedOnAsData);
        await using var session = await OpenAsync(proxy);

        // The stray byte breaks the stream once the server reads it again – when it is done.
        var error = await Assert.ThrowsAsync<DatabaseException>(() => CancelledAsync(session, BoundedSql, TimeSpan.FromSeconds(1)));

        Assert.True(error.IsConnectionLost, error.Display);
        Assert.Contains("into its command", error.SessionHistory);
        var next = await Assert.ThrowsAsync<DatabaseException>(() => session.PingAsync(Ct));
        Assert.True(next.IsConnectionLost, next.Display);
    }

    [Fact]
    public async Task A_path_that_loses_urgent_data_cancels_only_when_the_server_is_done()
    {
        await using var allowed = await oracle.AllowOutOfBandBreaksAsync();
        await using var proxy = Proxy(UrgentData.Lost);
        await using var session = await OpenAsync(proxy);
        var watch = Stopwatch.StartNew();
        await session.ExecuteReaderAsync(BoundedSql, [], (reader, ct) => reader.ReadAsync(ct), Ct).WaitAsync(Limit, Ct);
        var work = watch.Elapsed;

        watch.Restart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancelledAsync(session, BoundedSql, work / 4));

        Assert.True(watch.Elapsed > work / 2, $"cancelled after {watch.Elapsed} of {work}");
        Assert.True(await session.PingAsync(Ct) > TimeSpan.Zero);
    }

    /// <summary>
    /// <c>DISABLE_OOB</c> from a sqlnet.ora of the connection's own (<c>Tns_Admin</c>) – the same setting as
    /// <c>--disable-oob</c> (<see cref="OracleSession.SendBreaksInBand"/>), which ODP.NET takes only once per process.
    /// </summary>
    [Fact]
    public async Task In_band_a_cancel_ends_with_ORA_01013_and_keeps_the_session_on_either_path()
    {
        await using var allowed = await oracle.AllowOutOfBandBreaksAsync();
        using var folder = new TestFolder();
        await File.WriteAllTextAsync(folder.Combine("sqlnet.ora"), "DISABLE_OOB=on\n", Ct);

        foreach (var urgent in new[] { UrgentData.PassedOnAsData, UrgentData.Lost })
        {
            await using var proxy = Proxy(urgent);
            await using var session = await OpenAsync(proxy, folder.Path);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancelledAsync(session, EndlessSql, TimeSpan.FromSeconds(1)));

            Assert.True(await session.PingAsync(Ct) > TimeSpan.Zero, urgent.ToString());
        }
    }

    private BreakProxy Proxy(UrgentData urgent)
    {
        var address = (HostPortAddress)oracle.RequireProfile().Profile.Address;
        return new BreakProxy(address.Host, address.Port, urgent);
    }

    private async Task<OracleSession> OpenAsync(BreakProxy proxy, string? tnsAdmin = null)
    {
        var (profile, password) = oracle.RequireProfile();
        var throughProxy = profile with { Address = (HostPortAddress)profile.Address with { Host = "127.0.0.1", Port = proxy.Port } };
        var builder = new OracleConnectionStringBuilder(OracleConnectionStringFactory.Create(throughProxy, password));
        if (tnsAdmin is not null)
        {
            builder["Tns_Admin"] = tnsAdmin;
        }

        return await OracleSession.OpenAsync(builder.ConnectionString, new SessionContext("FerretSharp", "Break test"), Ct);
    }

    private static async Task CancelledAsync(OracleSession session, string sql, TimeSpan after)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(after);
        await session.ExecuteReaderAsync(sql, [], (reader, ct) => reader.ReadAsync(ct), cancel.Token).WaitAsync(Limit, Ct);
    }
}
