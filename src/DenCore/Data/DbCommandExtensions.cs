using System.Data.Common;

namespace DenCore.Data;

public static class DbCommandExtensions
{
    public static DbParameter AddParameterWithValue(this DbCommand command, string parameterName, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
