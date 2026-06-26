using DenCore.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenCore.Tests;

/// <summary>
/// Creates an isolated Postgres schema for each test.
/// </summary>
public sealed class TestDb : IAsyncLifetime
{
    private DatabaseInitializer? _initializer;
    public DbConnectionFactory Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _initializer = new DatabaseInitializer(
            $"den-core-test-{Guid.NewGuid():N}",
            NullLogger<DatabaseInitializer>.Instance);
        await _initializer.InitializeAsync();
        Db = new DbConnectionFactory(_initializer.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_initializer is not null)
            await _initializer.DisposeAsync();
    }
}
