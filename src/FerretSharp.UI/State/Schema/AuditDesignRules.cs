using FerretSharp.Core.Audit;

namespace FerretSharp.UI.State;

/// <summary>
/// When the audit box of the table designer takes part (WP-26, settings in 3.50.2/3.54.0): one rule for the designer and the
/// audit line of the "Columns" view.
/// </summary>
public static class AuditDesignRules
{
    /// <summary>
    /// Where the audit box starts: on when the designer was opened for the audit table; on for a table with an audit table
    /// unless the setting says otherwise (<see cref="Core.Settings.AppSettings.SyncAuditInDesigner"/>); off for a table
    /// without one, which is the normal case, and always when audit tables may not be created (<see cref="CanAudit"/>).
    /// Not decided (null) while it is unknown whether there is an audit table – reading it failed: the box starts once read.
    /// </summary>
    /// <param name="hasAuditTable">Null: unknown, reading the audit objects failed.</param>
    public static bool? AuditAtStart(bool openedForAudit, bool? hasAuditTable, bool syncByDefault, bool createAllowed) =>
        hasAuditTable is { } has ? CanAudit(has, createAllowed) && (openedForAudit || (has && syncByDefault)) : null;

    /// <summary>
    /// Whether the audit box can take part: always for a table with an audit table (brought up to date), for one without
    /// – or a new table – only when audit tables may be created (<see cref="Core.Settings.AppSettings.CreateAuditTables"/>).
    /// Never while it is unknown whether there is one: reading it failed, and taken as "none" the plan would create an
    /// existing audit table (ORA-00955) after the table's own steps had run.
    /// </summary>
    /// <param name="hasAuditTable">Null: unknown, reading the audit objects failed.</param>
    public static bool CanAudit(bool? hasAuditTable, bool createAllowed) => hasAuditTable is { } has && (has || createAllowed);

    /// <summary>
    /// Whether the table has an audit table, for the rules above: never for a new table, and taken as none while there is
    /// no template (the plan names the broken one); unknown (null) while its audit objects are read, after reading them
    /// failed and when a template turned up that they were not read by – taken as "none", the plan created an existing
    /// audit table, and a box ticked while reading became the user's choice (code review 2026-10-28).
    /// </summary>
    /// <param name="templateMissing">There is no usable template now (the file is read again when it changes).</param>
    /// <param name="read">What was read of the audit objects (<see cref="AuditRead.State"/>).</param>
    public static bool? HasAuditTable(bool newTable, bool templateMissing, AuditState? read) =>
        newTable || templateMissing ? false : read is null ? null : read.AuditTable is not null;
}
