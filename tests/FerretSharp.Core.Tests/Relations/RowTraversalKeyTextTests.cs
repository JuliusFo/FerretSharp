using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;

namespace FerretSharp.Core.Tests.Relations;

/// <summary>
/// Review of WP-85: a TIMESTAMP(8/9) key is a <see cref="PreciseTimestamp"/>, the foreign key holding it a DateTime – the key
/// text (matching rows across columns, node ids of the record diagram) is the same for both within seven digits.
/// </summary>
public sealed class RowTraversalKeyTextTests
{
    [Fact]
    public void A_precise_key_has_the_text_of_the_same_date_time_and_all_digits_beyond()
    {
        var time = new DateTime(2026, 10, 1, 13, 45, 7).AddTicks(1234500);

        Assert.Equal(RowTraversal.KeyText([time], trim: false), RowTraversal.KeyText([PreciseTimestamp.Of(time)], trim: false));
        Assert.Equal("2026-10-01 13:45:07.123456789",
            RowTraversal.KeyText([new PreciseTimestamp(new DateTime(2026, 10, 1, 13, 45, 7), 123456789)], trim: false));
    }
}
