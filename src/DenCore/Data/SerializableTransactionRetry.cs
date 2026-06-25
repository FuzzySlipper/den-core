using System.Data.Common;

namespace DenCore.Data;

public static class SerializableTransactionRetry
{
    private const int DefaultMaxAttempts = 3;

    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, int maxAttempts = DefaultMaxAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (DbException ex) when (attempt < maxAttempts && DbExceptionTranslator.IsSerializationFailure(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }
    }
}
