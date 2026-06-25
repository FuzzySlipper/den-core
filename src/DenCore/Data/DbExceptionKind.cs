namespace DenCore.Data;

public enum DbExceptionKind
{
    Unknown,
    ConstraintViolation,
    ForeignKeyViolation,
    UniqueViolation,
    SerializationFailure,
    ProviderReachability
}
