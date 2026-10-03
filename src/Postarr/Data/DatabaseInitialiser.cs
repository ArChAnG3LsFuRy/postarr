using Microsoft.EntityFrameworkCore;

namespace Postarr.Data;

/// <summary>
/// Handles database startup for both new installs and upgrades from older versions.
///
/// Strategy:
/// 1. If the DB doesn't exist → run all migrations normally (creates fresh schema).
/// 2. If the DB exists but has no __EFMigrationsHistory table (created by old EnsureCreated) →
///    fake-insert the InitialCreate record so EF thinks that migration already ran,
///    then run any newer migrations on top. This preserves all existing data.
/// 3. If the DB already has migrations → just run any pending ones (normal upgrade path).
///
/// New columns added in future migrations use "ALTER TABLE ... ADD COLUMN" with a DEFAULT
/// value, which SQLite handles perfectly — existing rows get the default, no data loss.
/// </summary>
public static class DatabaseInitialiser
{
    private const string InitialMigration = "20240101000000_InitialCreate";

    public static async Task InitialiseAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PostarrDbContext>();

        // Ensure the database file exists (creates it if not)
        await db.Database.EnsureCreatedAsync();

        // Check if the migrations history table exists
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        bool hasMigrationsTable;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
            hasMigrationsTable = (long)(await cmd.ExecuteScalarAsync() ?? 0L) > 0;
        }

        if (!hasMigrationsTable)
        {
            // Existing DB created by EnsureCreated — no migrations history.
            // Create the history table and register InitialCreate as already applied
            // so EF doesn't try to re-create tables that already exist.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                        "MigrationId"   TEXT NOT NULL,
                        "ProductVersion" TEXT NOT NULL,
                        CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                    );
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20240101000000_InitialCreate', '8.0.10');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            Console.WriteLine("[Postarr] Existing database detected — registered initial migration, applying any new ones.");
        }

        // Add columns introduced after this database was first created.
        //
        // NB: the schema actually comes from EnsureCreatedAsync above (built from the current
        // model), NOT from the migration files — those carry no [Migration]/[DbContext] attributes,
        // so EF discovers none of them and MigrateAsync is a no-op. That's fine for a NEW database
        // (EnsureCreated already includes every column) but an EXISTING one predates any newly
        // added model property and would fail at runtime with "no such column".
        //
        // So: add missing columns directly. Checking pragma_table_info first makes this idempotent,
        // which is what lets the same code be correct on both paths — new DBs skip every column,
        // upgraded DBs get exactly the ones they lack. Add a line here for each new column.
        await EnsureColumnAsync(conn, "LibraryItems", "PosterDismissed",     "INTEGER", "0");
        await EnsureColumnAsync(conn, "LibraryItems", "BackgroundDismissed", "INTEGER", "0");
        await EnsureColumnAsync(conn, "Collections",  "PosterDismissed",     "INTEGER", "0");
        await EnsureColumnAsync(conn, "LibraryItems", "RatingsCheckedUtc",   "TEXT",    "'0001-01-01 00:00:00'");
        // Every row that predates Jellyfin support came from Plex, so 'Plex' is the correct backfill.
        await EnsureColumnAsync(conn, "LibraryItems", "ServerType",          "TEXT",    "'Plex'");
        await EnsureColumnAsync(conn, "Collections",  "ServerType",          "TEXT",    "'Plex'");
        // Extra badge data (Metacritic, NEW, runtime, source, versions, languages). Nullable = "unknown".
        await EnsureColumnAsync(conn, "LibraryItems", "MetacriticScore",       "INTEGER", null);
        await EnsureColumnAsync(conn, "LibraryItems", "AddedAtUtc",            "TEXT",    null);
        await EnsureColumnAsync(conn, "LibraryItems", "LatestSeasonAddedUtc",  "TEXT",    null);
        await EnsureColumnAsync(conn, "LibraryItems", "RuntimeMinutes",        "INTEGER", null);
        await EnsureColumnAsync(conn, "LibraryItems", "VersionCount",          "INTEGER", null);
        await EnsureColumnAsync(conn, "LibraryItems", "VideoSource",           "TEXT",    null);
        await EnsureColumnAsync(conn, "LibraryItems", "AudioLanguageCount",    "INTEGER", null);
        await EnsureColumnAsync(conn, "LibraryItems", "SubtitleLanguageCount", "INTEGER", null);
        await EnsureColumnAsync(conn, "LibraryItems", "NewBadgeOnPoster",      "INTEGER", "0");
        await EnsureColumnAsync(conn, "LibraryItems", "ServerRatingFields",    "INTEGER", "0");
        await EnsureColumnAsync(conn, "LibraryItems", "LetterboxdRating",      "REAL",    null);
        await EnsureColumnAsync(conn, "LibraryItems", "TraktRating",           "INTEGER", null);
        await EnsureColumnAsync(conn, "LibraryItems", "ImdbTop250Rank",        "INTEGER", null);
        await EnsureColumnAsync(conn, "LibraryItems", "Genres",                "TEXT",    null);
        await EnsureColumnAsync(conn, "LibraryItems", "TmdbCollectionId",      "TEXT",    null);
        await EnsureColumnAsync(conn, "LibraryItems", "TmdbCollectionName",    "TEXT",    null);
        await EnsureColumnAsync(conn, "LibraryItems", "Countries",             "TEXT",    null);
        await EnsureColumnAsync(conn, "LibraryItems", "Actors",                "TEXT",    null);
        await EnsureColumnAsync(conn, "LibraryItems", "Directors",             "TEXT",    null);
        await EnsureColumnAsync(conn, "LibraryItems", "TmdbDetailsVersion",    "INTEGER", "0");

        // Tables introduced after the database was first created (EnsureCreated only builds them on a new DB).
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ManagedCollections" (
                "Id"                 INTEGER NOT NULL CONSTRAINT "PK_ManagedCollections" PRIMARY KEY AUTOINCREMENT,
                "ServerType"         TEXT    NOT NULL DEFAULT 'Plex',
                "SectionKey"         TEXT    NOT NULL DEFAULT '',
                "MediaType"          INTEGER NOT NULL DEFAULT 0,
                "SetKey"             TEXT    NOT NULL DEFAULT '',
                "CollectionKey"      TEXT    NOT NULL DEFAULT '',
                "Title"              TEXT    NOT NULL DEFAULT '',
                "ServerCollectionId" TEXT    NOT NULL DEFAULT '',
                "TmdbCollectionId"   TEXT    NULL,
                "ItemCount"          INTEGER NOT NULL DEFAULT 0,
                "LastSyncedUtc"      TEXT    NOT NULL DEFAULT '0001-01-01 00:00:00'
            );
            """);
        await EnsureColumnAsync(conn, "ManagedCollections", "AutoPosterUrl", "TEXT", null);
        await EnsureColumnAsync(conn, "ManagedCollections", "OrderHash",     "TEXT", null);

        await conn.CloseAsync();

        // Now run any pending migrations (safe: skips already-applied ones)
        await db.Database.MigrateAsync();

        Console.WriteLine("[Postarr] Database initialisation complete.");
    }

    private static async Task ExecAsync(System.Data.Common.DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Adds a column only if the table doesn't already have it. Safe to call every start.
    /// A null <paramref name="defaultValue"/> adds a nullable column (existing rows get NULL = unknown).
    /// </summary>
    private static async Task EnsureColumnAsync(
        System.Data.Common.DbConnection conn, string table, string column, string type, string? defaultValue)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt64(await check.ExecuteScalarAsync() ?? 0L) > 0) return;
        }
        using (var add = conn.CreateCommand())
        {
            // NOT NULL + DEFAULT keeps existing rows valid (SQLite backfills them).
            add.CommandText = defaultValue == null
                ? $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type} NULL"
                : $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type} NOT NULL DEFAULT {defaultValue}";
            await add.ExecuteNonQueryAsync();
        }
        Console.WriteLine($"[Postarr] Added missing column {table}.{column}");
    }
}
