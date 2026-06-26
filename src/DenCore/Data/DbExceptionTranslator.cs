using System.Data.Common;
using System.Reflection;

namespace DenCore.Data;

public static class DbExceptionTranslator
{
    public static DbExceptionKind Translate(Exception exception)
    {
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

    private static string? TryGetStringProperty(Exception exception, string propertyName)
    {
        var property = exception.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(exception) as string;
    }
}
