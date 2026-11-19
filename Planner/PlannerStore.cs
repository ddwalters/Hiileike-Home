using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HiileikeHome.Lore;
using Npgsql;
using NpgsqlTypes;

namespace HiileikeHome.Planner;

public sealed class PlannerStore(IWebHostEnvironment environment, IConfiguration configuration) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly PlannerDatabase? database = PlannerDatabase.Create(configuration);
    private readonly string root = Path.GetFullPath(configuration["Planner:DataDirectory"] is { Length: > 0 } path
        ? path : Path.Combine("App_Data", "planner"), environment.ContentRootPath);

    public async Task<PlannerWorkspace> ReadAsync(ClaimsPrincipal user)
    {
        var owner = Owner(user);
        return database is null ? await ReadFileAsync(Path.Combine(root, owner + ".json"))
            : await database.AccessAsync(owner, null);
    }

    public async Task<PlannerWorkspace> ChangeAsync(ClaimsPrincipal user, Action<PlannerWorkspace> change)
    {
        var owner = Owner(user);
        if (database is not null)
            return await database.AccessAsync(owner, change);

        Directory.CreateDirectory(root);
        var path = Path.Combine(root, owner + ".json");
        await using var gate = await LockFileAsync(path + ".lock");
        var workspace = await ReadFileAsync(path);
        change(workspace);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, workspace, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return workspace;
    }

    public async ValueTask DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }

    private static string Owner(ClaimsPrincipal user)
    {
        var subject = user.FindFirst(ClaimTypes.NameIdentifier) ?? user.FindFirst("sub");
        if (user.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject?.Value))
            throw new PlannerValidationException("Sign in with an account that has a stable user ID to open Planner.");

        var identity = JsonSerializer.Serialize(new[] { user.FindFirst("iss")?.Value ?? subject.Issuer, subject.Value });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static async Task<FileStream> LockFileAsync(string path)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (retry < 40)
            {
                await Task.Delay(50);
            }
        }
    }

    private static async Task<PlannerWorkspace> ReadFileAsync(string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<PlannerWorkspace>(stream, Json)
                ?? throw new JsonException("The Planner workspace is empty.");
        }
        catch (FileNotFoundException)
        {
            return new();
        }
        catch (DirectoryNotFoundException)
        {
            return new();
        }
    }

    private sealed class PlannerDatabase(string connectionString) : IAsyncDisposable
    {
        private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(connectionString);
        private readonly SemaphoreSlim migrationGate = new(1);
        private bool initialized;

        public static PlannerDatabase? Create(IConfiguration configuration)
        {
            var connection = configuration.GetConnectionString("Planner");
            if (string.IsNullOrWhiteSpace(connection))
                connection = LoreDatabase.GetConnectionString(configuration);
            return connection is null ? null : new(connection);
        }

        public async Task<PlannerWorkspace> AccessAsync(string owner, Action<PlannerWorkspace>? change)
        {
            await using var connection = await source.OpenConnectionAsync();
            await InitializeAsync(connection);
            if (change is null)
            {
                await using var read = new NpgsqlCommand(
                    "SELECT payload FROM planner.workspaces WHERE owner_id = $1", connection);
                read.Parameters.AddWithValue(owner);
                return ReadWorkspace(await read.ExecuteScalarAsync());
            }

            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO planner.workspaces (owner_id) VALUES ($1) ON CONFLICT (owner_id) DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue(owner);
            await command.ExecuteNonQueryAsync();
            command.CommandText = "SELECT payload FROM planner.workspaces WHERE owner_id = $1 FOR UPDATE";
            var workspace = ReadWorkspace(await command.ExecuteScalarAsync());
            change(workspace);
            command.CommandText = "UPDATE planner.workspaces SET payload = $2, updated_at = now() WHERE owner_id = $1";
            command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(workspace, Json));
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return workspace;
        }

        private async Task InitializeAsync(NpgsqlConnection connection)
        {
            await migrationGate.WaitAsync();
            try
            {
                if (initialized)
                    return;

                await using var transaction = await connection.BeginTransactionAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT pg_advisory_xact_lock(727930184012);
                    DO $$
                    BEGIN
                        IF to_regnamespace('planner') IS NULL THEN
                            CREATE SCHEMA planner;
                        END IF;
                    END
                    $$;
                    CREATE TABLE IF NOT EXISTS planner.schema_migrations (version integer PRIMARY KEY);
                    """, connection, transaction);
                await command.ExecuteNonQueryAsync();
                command.CommandText = "SELECT coalesce(max(version), 0) FROM planner.schema_migrations";
                var version = (int)(await command.ExecuteScalarAsync())!;
                string[] migrations = ["001_initial.sql"];
                if (version > migrations.Length)
                    throw new IOException("The Planner database schema is newer than this application.");

                foreach (var migration in migrations.Skip(version))
                {
                    using var stream = typeof(PlannerStore).Assembly.GetManifestResourceStream(
                        $"HiileikeHome.Planner.Database.{migration}")
                        ?? throw new InvalidOperationException("A Planner database migration is missing.");
                    using var reader = new StreamReader(stream);
                    command.CommandText = await reader.ReadToEndAsync();
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
                initialized = true;
            }
            finally
            {
                migrationGate.Release();
            }
        }

        private static PlannerWorkspace ReadWorkspace(object? result) => result is string json
            ? JsonSerializer.Deserialize<PlannerWorkspace>(json, Json)
                ?? throw new JsonException("The Planner workspace is empty.")
            : new();

        public async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            migrationGate.Dispose();
        }
    }
}
