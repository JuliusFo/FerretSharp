using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query;

/// <summary>Review 3.31.0, A10: passwords in a statement's text went into the log and the SQL history, also on Prod.</summary>
public class SqlRedactionTests
{
    [Theory]
    [InlineData("ALTER USER app IDENTIFIED BY \"Neu#2026\"", "ALTER USER app IDENTIFIED BY ***")]
    [InlineData("create user x identified by geheim default tablespace users", "create user x identified by *** default tablespace users")]
    [InlineData("ALTER USER app IDENTIFIED BY neu REPLACE alt", "ALTER USER app IDENTIFIED BY *** REPLACE ***")]
    [InlineData("ALTER USER app IDENTIFIED BY VALUES 'S:ABCDEF'", "ALTER USER app IDENTIFIED BY VALUES ***")]
    [InlineData("CREATE DATABASE LINK l CONNECT TO u IDENTIFIED BY pw USING 'PROD'", "CREATE DATABASE LINK l CONNECT TO u IDENTIFIED BY *** USING 'PROD'")]
    [InlineData("CREATE USER x IDENTIFIED /* hm */ BY\n  pw", "CREATE USER x IDENTIFIED /* hm */ BY\n  ***")]
    public void The_password_after_identified_by_is_masked(string sql, string expected) =>
        Assert.Equal(expected, SqlRedaction.Redact(sql));

    [Theory]
    [InlineData("CREATE USER x IDENTIFIED EXTERNALLY")]
    [InlineData("SELECT 'IDENTIFIED BY x' FROM dual")]
    [InlineData("UPDATE t SET c = :p0 WHERE id = 1")]
    [InlineData("ALTER USER app IDENTIFIED BY")]
    public void Text_without_a_password_stays_as_it_is(string sql) =>
        Assert.Equal(sql, SqlRedaction.Redact(sql));
}
