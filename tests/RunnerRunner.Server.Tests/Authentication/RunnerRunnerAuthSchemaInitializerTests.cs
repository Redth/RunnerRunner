using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RunnerRunner.Server.Authentication;
using RunnerRunner.Server.Data.Auth;

namespace RunnerRunner.Server.Tests.Authentication;

public class RunnerRunnerAuthSchemaInitializerTests
{
    [Fact]
    public async Task EnsureCreatedAsyncCreatesAuthTablesWhenDatabaseAlreadyHasOtherTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await CreateExistingTableAsync(connection);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RunnerRunnerAuthDbContext>(options => options.UseSqlite(connection));
        services
            .AddIdentity<RunnerRunnerUser, IdentityRole>()
            .AddEntityFrameworkStores<RunnerRunnerAuthDbContext>()
            .AddDefaultTokenProviders();

        await using var provider = services.BuildServiceProvider();

        await RunnerRunnerAuthSchemaInitializer.EnsureCreatedAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerRunnerAuthDbContext>();
        var roleNames = await db.Roles.Select(role => role.Name).ToListAsync();

        Assert.Contains(RunnerRunnerRoles.Administrator, roleNames);
        Assert.Contains(RunnerRunnerRoles.Viewer, roleNames);
    }

    private static async Task CreateExistingTableAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ExistingDocumentTable (
                Id TEXT PRIMARY KEY
            );
            """;
        await command.ExecuteNonQueryAsync();
    }
}
