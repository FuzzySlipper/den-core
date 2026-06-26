using System.Data.Common;
using DenCore.Data;

namespace DenCore.Tests.Data;

public class DbExceptionTranslatorTests
{
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

    [Fact]
    public void Translate_UnknownDbException_RemainsUnknown()
    {
        var exception = new FakePostgresException("ZZ999");

        Assert.Equal(DbExceptionKind.Unknown, DbExceptionTranslator.Translate(exception));
    }

    private sealed class FakePostgresException(string sqlState) : DbException
    {
        public override string SqlState { get; } = sqlState;
    }
}
