using FerretSharp.Core.Audit;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>When the audit box of the table designer takes part and where it starts (<see cref="AuditDesignRules"/>).</summary>
public sealed class AuditDesignRulesTests
{
    private static readonly TableDetails Kunde = new(new TableSummary("SHOP", "KUNDE", TableKind.Table), [], ["ID"], [], false);

    /// <summary>
    /// The audit box at start: the setting turns it off for a table with an audit table, not when opened for the audit;
    /// without creating audit tables it stays off for a table without one, also when opened for the audit.
    /// </summary>
    [Theory]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, true, true, true, true)]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, true, false, false, true)]
    public void The_audit_box_starts_by_audit_table_settings_and_how_the_designer_was_opened(bool openedForAudit, bool hasAuditTable, bool syncByDefault,
        bool createAllowed, bool expected) =>
        Assert.Equal(expected, AuditDesignRules.AuditAtStart(openedForAudit, hasAuditTable, syncByDefault, createAllowed));

    /// <summary>Without creating audit tables the box is only there for a table that has one.</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    public void The_audit_box_takes_part_unless_it_would_create_an_audit_table_that_may_not_be_created(bool hasAuditTable, bool createAllowed, bool expected) =>
        Assert.Equal(expected, AuditDesignRules.CanAudit(hasAuditTable, createAllowed));

    /// <summary>Code review: while the audit objects were read again, the box was free as for a table without audit table.</summary>
    [Fact]
    public void Whether_there_is_an_audit_table_is_unknown_until_read()
    {
        Assert.Null(AuditDesignRules.HasAuditTable(newTable: false, templateMissing: false, read: null));
        Assert.False(AuditDesignRules.HasAuditTable(newTable: false, templateMissing: false, AuditState.None));
        Assert.True(AuditDesignRules.HasAuditTable(newTable: false, templateMissing: false, new AuditState(Kunde, null)));
        Assert.False(AuditDesignRules.HasAuditTable(newTable: true, templateMissing: false, read: null));
    }

    /// <summary>
    /// Code review 2026-10-28: a template broken when the designer opened and readable later – the state taken as "none"
    /// made the plan create the existing audit table. Without a template the box takes part (the plan names the broken
    /// template); one that turns up unread leaves it unknown.
    /// </summary>
    [Fact]
    public void A_template_that_turns_up_later_leaves_the_audit_table_unknown()
    {
        Assert.False(AuditDesignRules.HasAuditTable(newTable: false, templateMissing: true, read: null));
        Assert.Null(AuditDesignRules.HasAuditTable(newTable: false, templateMissing: false, read: null));
        Assert.True(AuditDesignRules.CanAudit(AuditDesignRules.HasAuditTable(false, templateMissing: true, null), createAllowed: true));
    }
}
