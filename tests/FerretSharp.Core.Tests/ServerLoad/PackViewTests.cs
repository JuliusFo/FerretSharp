using System.Text.RegularExpressions;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.ServerLoad;

/// <summary>
/// Decision of the user (WP-75, 2026-11-07, ADR 0045): FerretSharp reads nothing that needs the Diagnostics or Tuning Pack.
/// Oracle charges for any access to them, also a plain SELECT (Database Licensing Information User Manual 19c, table 1-15);
/// the server would run it without a word (<c>CONTROL_MANAGEMENT_PACK_ACCESS</c> is <c>DIAGNOSTIC+TUNING</c> by default).
/// So no code of the app names one of their views, packages or the MONITOR hint – also not in a comment, where it would be
/// one step from being used. The metric views are no pack view by name but „metric history“ is a pack feature: kept out too.
/// </summary>
public sealed partial class PackViewTests
{
    /// <summary>The views beginning with DBA_HIST_ that need no pack (same manual).</summary>
    private static readonly HashSet<string> FreeHistoryViews = new(StringComparer.OrdinalIgnoreCase)
    {
        "DBA_HIST_SNAPSHOT", "DBA_HIST_DATABASE_INSTANCE", "DBA_HIST_SNAP_ERROR", "DBA_HIST_SEG_STAT", "DBA_HIST_SEG_STAT_OBJ", "DBA_HIST_UNDOSTAT",
    };

    [GeneratedRegex(
        """
        \bG?V\$ACTIVE_SESSION_HISTORY\b | \bX\$ASH\b | \bDBA_HIST_\w+ | \bCDB_HIST_\w+ | \bDBA_ADDM_\w+ | \bDBA_ADVISOR_\w+
        | \bDBMS_(WORKLOAD_REPOSITORY|ADDM|PERF|UMF|ADVISOR|SQLTUNE|AUTO_SQLTUNE|SQL_MONITOR)\b
        | \bG?V\$SQL_(PLAN_)?MONITOR\b | \bG?V\$\w*METRIC\w* | /\*\+[^*]*\bMONITOR\b
        | \b(awr|ash|addm)\w*\.sql\b | \bsqltrpt\.sql\b
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex PackObject();

    [Fact]
    public void No_code_of_the_app_names_a_view_or_package_of_the_diagnostics_or_tuning_pack()
    {
        var src = Path.Combine(Repository.Root, "src");
        var found = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".cs" or ".razor" or ".js")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) // AG Grid, Monaco
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => PackObject().Matches(File.ReadAllText(f))
                .Where(m => !FreeHistoryViews.Contains(m.Value))
                .Select(m => $"{Path.GetRelativePath(src, f)}: {m.Value}"))
            .ToList();

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("SELECT * FROM v$active_session_history", true)]
    [InlineData("SELECT * FROM gv$sql_monitor", true)]
    [InlineData("SELECT * FROM dba_hist_sqlstat", true)]
    [InlineData("SELECT * FROM v$sysmetric_history", true)]
    [InlineData("SELECT /*+ MONITOR */ * FROM t", true)]
    [InlineData("DBMS_SQLTUNE.REPORT_SQL_MONITOR", true)]
    [InlineData("SELECT * FROM v$sqlstats", false)]
    [InlineData("SELECT * FROM v$sys_time_model", false)]
    [InlineData("SELECT * FROM dba_hist_snapshot", false)]
    public void The_check_finds_what_needs_a_pack_and_nothing_else(string text, bool needsPack) =>
        Assert.Equal(needsPack, PackObject().Matches(text).Any(m => !FreeHistoryViews.Contains(m.Value)));
}
