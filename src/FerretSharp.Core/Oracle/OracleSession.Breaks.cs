using System.Globalization;
using FerretSharp.Core.Connections;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// Diagnostics of lost connections: the last break this session sent to the server (a cancelled command) and how that
/// command ended. A connection lost right after a break, on the first command since, points at the break – one that met
/// a command the server had already answered can leave the session out of step – rather than at the network (ORA-12570
/// on the explorer session after the record diagram, 3.47.0). A lost connection's error carries it into the log
/// (<see cref="DatabaseException.SessionHistory"/>); nothing of the statement or its values.
/// </summary>
public sealed partial class OracleSession
{
    private volatile SentBreak? _lastBreak;

    /// <summary>
    /// Sends breaks in band instead of as TCP urgent data (out of band, ODP.NET's default when the server agrees). A path that
    /// loses urgent data delays a cancel until the server is done; one that clears the urgent flag passes the byte on as data
    /// and the session is lost (ORA-12537/ORA-12570, backlog "Abbrechen über VPN"). In band, the server notices a break only
    /// when it next checks the connection. Process-wide in ODP.NET and only before the first connection opens (ORA-50099).
    /// </summary>
    public static void SendBreaksInBand() => OracleConfiguration.DisableOOB = true;

    /// <summary>Breaks go in band (<see cref="SendBreaksInBand"/>).</summary>
    public static bool BreaksInBand => OracleConfiguration.DisableOOB;

    /// <summary>Milliseconds the session had been idle when the last command started.</summary>
    private long _idleBefore;

    /// <summary>A break sent to the server: when, how far into its command, and how the command ended.</summary>
    internal sealed class SentBreak(long sentAt, long intoCommand, bool afterReturn)
    {
        private int _commandsSince;

        public long SentAt { get; } = sentAt;

        /// <summary>Milliseconds between the start of its command and the break.</summary>
        public long IntoCommand { get; } = intoCommand;

        /// <summary>The command had already returned when ODP.NET was told to cancel it (its registration was not yet gone).</summary>
        public bool AfterReturn { get; } = afterReturn;

        /// <summary>"normally", "with ORA-01013" …; null while its command runs.</summary>
        public string? Ended { get; set; }

        /// <summary>Commands started on the session since, the one that failed included.</summary>
        public int CommandsSince => Volatile.Read(ref _commandsSince);

        public void CountCommand() => Interlocked.Increment(ref _commandsSince);
    }

    /// <summary>One command's part in the diagnostics: sends the break and notes it, and how the command ended.</summary>
    private sealed class CommandTrace(OracleSession session, OracleCommand command)
    {
        private readonly long _started = Environment.TickCount64;
        private int _returned;
        private SentBreak? _sent;

        /// <summary>On a pool thread (<see cref="CancelInBackground"/>).</summary>
        public void Cancel()
        {
            var now = Environment.TickCount64;
            session._lastBreak = _sent = new SentBreak(now, now - _started, Volatile.Read(ref _returned) == 1);
            command.Cancel();
        }

        public void Returned() => Volatile.Write(ref _returned, 1);

        /// <summary>After the cancel registration is gone: no break comes any more.</summary>
        public void Ended(string how)
        {
            if (_sent is { } sent)
            {
                sent.Ended = how;
            }
        }
    }

    /// <summary>A command starts: counts it after the last break and notes how long the session had been idle.</summary>
    private CommandTrace StartTrace(OracleCommand command)
    {
        _lastBreak?.CountCommand();
        Interlocked.Exchange(ref _idleBefore, Environment.TickCount64 - Interlocked.Read(ref _lastRoundTrip));
        return new CommandTrace(this, command);
    }

    /// <summary>The session's error translation; a lost connection gets the session's history for the log.</summary>
    private Exception? Translate(Exception ex)
    {
        var translated = OracleErrors.Translate(ex);
        if (translated is { IsConnectionLost: true })
        {
            translated.SessionHistory = DescribeHistory(_lastBreak, Interlocked.Read(ref _idleBefore), Environment.TickCount64, BreaksInBand);
        }

        return translated;
    }

    /// <summary>
    /// "idle 13.2 s before this command; last break 14.0 s ago, 2 ms after its command had returned (it ended normally), 1 command since";
    /// "; breaks in band" after it with <paramref name="inBand"/> (to tell the two ways apart while trying <c>--disable-oob</c>).
    /// </summary>
    internal static string DescribeHistory(SentBreak? last, long idleBefore, long now, bool inBand = false)
    {
        var idle = $"idle {Seconds(idleBefore)} before this command";
        var mode = inBand ? "; breaks in band" : "";
        if (last is null)
        {
            return $"{idle}; no break sent since the session opened{mode}";
        }

        var when = last.AfterReturn ? "after its command had returned" : $"{Seconds(last.IntoCommand)} into its command";
        var since = last.CommandsSince == 0 ? "during this command" : $"{last.CommandsSince} command(s) since, this one included";
        return $"{idle}; last break {Seconds(now - last.SentAt)} ago, {when} (it ended {last.Ended ?? "not yet"}), {since}{mode}";
    }

    private static string Seconds(long milliseconds) => (milliseconds / 1000d).ToString("0.0 s", CultureInfo.InvariantCulture);
}
