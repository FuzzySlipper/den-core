using DenCore.Data;
using DenCore.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenCore.Tests.Data;

public sealed class WorkerPoolRepositoryPostgresTests
{
    [Fact]
    [Trait("Category", "PostgresProvider")]
    public async Task SweepStaleWorkers_DetectsDuplicateAssignmentsForRun()
    {
        if (!PostgresTestDb.IsConfigured)
            return;

        var testDb = new PostgresTestDb();
        await testDb.InitializeAsync();
        try
        {
            var initializer = new PostgresDatabaseInitializer(
                testDb.Db,
                NullLogger<PostgresDatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            await using (var conn = await testDb.Db.CreateConnectionAsync())
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO projects (id, name)
                    VALUES ('pg-sweep-proj', 'Postgres Sweep Project');

                    INSERT INTO worker_pool_members (worker_identity, profile_identity, worker_role, status)
                    VALUES
                        ('pg-sweep-worker-1', 'postgres-profile', 'coder', 'busy'),
                        ('pg-sweep-worker-2', 'postgres-profile', 'coder', 'busy');

                    INSERT INTO worker_assignments (
                        worker_identity, run_id, project_id, role, assigned_by,
                        state, lease_id, profile_identity, acquired_at, created_at
                    )
                    VALUES
                        ('pg-sweep-worker-1', 'pg-duplicate-run', 'pg-sweep-proj', 'coder', 'postgres-test',
                         'ack', 'pg-sweep-worker-1:pg-duplicate-run', 'postgres-profile',
                         to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'),
                         to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
                        ('pg-sweep-worker-2', 'pg-duplicate-run', 'pg-sweep-proj', 'coder', 'postgres-test',
                         'running', 'pg-sweep-worker-2:pg-duplicate-run', 'postgres-profile',
                         to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'),
                         to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'));
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var repo = new WorkerPoolRepository(testDb.Db);
            var result = await repo.SweepStaleWorkersAsync(new StaleSweepOptions
            {
                ProjectId = "pg-sweep-proj",
            });

            var duplicate = Assert.Single(result.Conditions, c =>
                c.Classification == StaleClassificationTypes.DuplicateAssignmentForRun
                && c.RunId == "pg-duplicate-run");
            Assert.Equal("pg-sweep-proj", duplicate.ProjectId);
            Assert.Contains("pg-sweep-worker-1", duplicate.StateReason);
            Assert.Contains("pg-sweep-worker-2", duplicate.StateReason);
            Assert.Contains("ack,running", duplicate.CurrentState);
        }
        finally
        {
            await testDb.DisposeAsync();
        }
    }
}
