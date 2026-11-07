using FerretSharp.Core.Oracle;

namespace FerretSharp.Core.Tests.Oracle;

public sealed class OracleErrorCodesTests
{
    [Theory]
    [InlineData(1013, "ORA-01013: user requested cancel of current operation", true)]
    // CI 2026-11-07: a cancel that hit recursive SQL under all_objects reports the recursive call's error on top.
    [InlineData(22303, "ORA-22303: type \"XDB\".\"XDB$STRING_LIST_T\" not found\nORA-00604: Error occurred at recursive SQL level 2.\nORA-01013: User requested cancel of current operation.", true)]
    [InlineData(942, "ORA-00942: table or view does not exist", false)]
    [InlineData(10130, "ORA-10130: something else", false)]
    public void A_cancel_counts_also_further_down_the_error_stack(int number, string message, bool cancel) =>
        Assert.Equal(cancel, OracleErrorCodes.IsCancel(number, message));
}
