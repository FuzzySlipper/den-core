using System.Data.Common;
using DenCore.Data;
using Microsoft.Data.Sqlite;

namespace DenCore.Tests.Data;

public class DbExceptionTranslatorTests
{
    [Fact]
    public void Translate_MapsSqliteForeignKeyConstraint()
    {
        var exception = new SqliteException("foreign key failed", 19, 787);

        Assert.Equal(DbExceptionKind.ForeignKeyViolation, DbExceptionTranslator.Translate(exception));
        Assert.True(DbExceptionTranslator.IsReferentialIntegrityViolation(exception));
        Assert.True(DbExceptionTranslator.IsConstraintViolation(exception));
    }

    [Theory]
    [InlineData("23503", DbExceptionKind.ForeignKeyViolation)]
    [InlineData("23505", DbExceptionKind.UniqueViolation)]
    [InlineData("40001", DbExceptionKind.SerializationFailure)]
    [InlineData("08006", DbExceptionKind.ProviderReachability)]
    public void Translate_MapsPostgresSqlState(string sqlState, DbExceptionKind expected)
    {
        var exception = new FakePostgresException(sqlState);

        Assert.Equal(expected, DbExceptionTranslator.Translate(exception));
    }

    private sealed class FakePostgresException(string sqlState) : DbException
    {
        public override string SqlState { get; } = sqlState;
    }
}
