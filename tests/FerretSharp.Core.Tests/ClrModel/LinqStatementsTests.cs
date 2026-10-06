using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class LinqStatementsTests
{
    private static CapturedCommand Command(string kind, string sql, params CapturedParameter[] parameters) => new(kind, sql, parameters);

    [Theory]
    [InlineData(LinqProtocol.Reader, "SELECT \"k\".\"NAME\" FROM \"KUNDEN\" \"k\"", LinqCommandKind.Query)]
    [InlineData(LinqProtocol.Scalar, "SELECT COUNT(*) FROM \"KUNDEN\" \"k\"", LinqCommandKind.Query)]
    [InlineData(LinqProtocol.Reader, "WITH x AS (SELECT 1 FROM DUAL) SELECT * FROM x", LinqCommandKind.Query)]
    [InlineData(LinqProtocol.NonQuery, "UPDATE \"AUFTRAG\" \"a\" SET \"a\".\"STATUS\" = N'STORNIERT'", LinqCommandKind.Write)]
    [InlineData(LinqProtocol.NonQuery, "DELETE FROM \"AUFTRAG\" \"a\" WHERE \"a\".\"KUNDE_ID\" = 5", LinqCommandKind.Write)]
    [InlineData(LinqProtocol.NonQuery, "BEGIN INSERT INTO \"KUNDEN\" VALUES (1); END;", LinqCommandKind.Unsupported)]
    [InlineData(LinqProtocol.Reader, "DELETE FROM \"AUFTRAG\"", LinqCommandKind.Unsupported)]
    [InlineData(LinqProtocol.Reader, "-- Offene Aufträge\n\nSELECT \"a\".\"ID\" FROM \"AUFTRAG\" \"a\"", LinqCommandKind.Query)] // TagWith()
    [InlineData(LinqProtocol.NonQuery, "-- Stornieren\nDELETE FROM \"AUFTRAG\" \"a\"", LinqCommandKind.Write)]
    [InlineData(LinqProtocol.Reader, "SELECT \"a\".\"ID\" FROM \"AUFTRAG\" \"a\" FOR UPDATE", LinqCommandKind.Unsupported)]
    public void Commands_are_queries_writes_or_unsupported(string kind, string sql, LinqCommandKind expected) =>
        Assert.Equal(expected, LinqStatements.KindOf(Command(kind, sql)));

    [Fact]
    public void Parameters_get_their_values_and_oracle_types_back()
    {
        var query = LinqStatements.ToQuery(Command(LinqProtocol.Reader, "  SELECT 1 FROM DUAL WHERE :a = 1  ",
            new("kundeId_0", "Int32", "Int32", "4711"),
            new("name_1", "NVarchar2", "String", "Müller"),
            new("von_2", "Date", "DateTime", "2026-10-05T14:30:00.0000000"),
            new("betrag_3", "Decimal", "Decimal", "1234.5"),
            new("guid_4", "Raw", "Byte[]", "AQID"),
            new("leer_5", "Varchar2", null, null),
            new("flag_6", "Boolean", "Boolean", "True")));

        Assert.Equal("SELECT 1 FROM DUAL WHERE :a = 1", query.Sql);
        Assert.Equal(
            [
                new QueryParameter("kundeId_0", 4711, OracleTypeHint.Number),
                new QueryParameter("name_1", "Müller", OracleTypeHint.NVarchar2),
                new QueryParameter("von_2", new DateTime(2026, 10, 5, 14, 30, 0), OracleTypeHint.Date),
                new QueryParameter("betrag_3", 1234.5m, OracleTypeHint.Number),
                new QueryParameter("leer_5", null, OracleTypeHint.Varchar2),
                new QueryParameter("flag_6", true, OracleTypeHint.Auto),
            ],
            query.Parameters.Where(p => p.Name != "guid_4"));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(query.Parameters.Single(p => p.Name == "guid_4").Value));
    }
}
