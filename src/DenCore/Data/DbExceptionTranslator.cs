using System.Data.Common;
using System.Reflection;

namespace DenCore.Data;

public static class DbExceptionTranslator
{
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintForeignKey = 787;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;
    private const int SqliteCannotOpen = 14;

    public static DbExceptionKind Translate(Exception exception)
    {
        if (TryGetSqliteCode(exception, "SqliteErrorCode", out var sqliteCode))
        {
            if (sqliteCode == SqliteCannotOpen)
                return DbExceptionKind.ProviderReachability;
            if (TryGetSqliteCode(exception, "SqliteExtendedErrorCode", out var extendedCode))
            {
                if (extendedCode == SqliteConstraintForeignKey)
                    return DbExceptionKind.ForeignKeyViolation;
                if (extendedCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey)
                    return DbExceptionKind.UniqueViolation;
            }
            if (sqliteCode == SqliteConstraint)
                return DbExceptionKind.ConstraintViolation;
        }

        var sqlState = TryGetStringProperty(exception, "SqlState");
        return sqlState switch
        {
            "23503" => DbExceptionKind.ForeignKeyViolation,
            "23505" => DbExceptionKind.UniqueViolation,
            "40001" => DbExceptionKind.SerializationFailure,
            "08000" or "08001" or "08003" or "08004" or "08006" or "08007" => DbExceptionKind.ProviderReachability,
            _ => DbExceptionKind.Unknown
        };
    }

    public static bool IsConstraintViolation(Exception exception) =>
        Translate(exception) is DbExceptionKind.ConstraintViolation
            or DbExceptionKind.ForeignKeyViolation
            or DbExceptionKind.UniqueViolation;

    public static bool IsReferentialIntegrityViolation(Exception exception) =>
        Translate(exception) is DbExceptionKind.ForeignKeyViolation or DbExceptionKind.ConstraintViolation;

    public static bool IsUniqueViolation(Exception exception) =>
        Translate(exception) is DbExceptionKind.UniqueViolation or DbExceptionKind.ConstraintViolation;

    public static bool IsSerializationFailure(Exception exception) =>
        Translate(exception) == DbExceptionKind.SerializationFailure;

    public static bool IsProviderReachability(Exception exception) =>
        exception is DbException
            && (Translate(exception) is DbExceptionKind.ProviderReachability or DbExceptionKind.Unknown);

    private static bool TryGetSqliteCode(Exception exception, string propertyName, out int code)
    {
        code = 0;
        if (!string.Equals(exception.GetType().FullName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal))
            return false;
        var property = exception.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(exception) is int value)
        {
            code = value;
            return true;
        }
        return false;
    }

    private static string? TryGetStringProperty(Exception exception, string propertyName)
    {
        var property = exception.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(exception) as string;
    }
}
