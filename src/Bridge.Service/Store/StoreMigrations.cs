namespace Bridge.Service.Store;

/// <summary>
/// One schema upgrade from <c>user_version = Version - 1</c> to <c>Version</c>. Version 1 is <c>schema.sql</c> itself; every
/// later change (new columns, indexes, tables) is a migration with the next version number and is never edited after
/// it has shipped (docs/ARCHITECTURE.md, "Persistence": new columns only via a migration to user_version 2).
/// </summary>
/// <param name="Version">The <c>user_version</c> after this migration.</param>
/// <param name="Description">Logged when the migration runs.</param>
/// <param name="Sql">The statements. They run inside one IMMEDIATE transaction together with the <c>user_version</c> update, so
/// they must not contain <c>PRAGMA journal_mode</c> or <c>VACUUM</c>.</param>
public sealed record StoreMigration(int Version, string Description, string Sql);

/// <summary>The ordered list of schema migrations applied by <see cref="SqliteBridgeStore.InitializeAsync"/>.</summary>
public static class StoreMigrations
{
    /// <summary>The version <c>schema.sql</c> creates.</summary>
    public const int BaseVersion = 1;

    /// <summary>
    /// Migrations after <see cref="BaseVersion"/>, ascending and contiguous (docs/ARCHITECTURE.md, "Persistence"). Version 2
    /// adds the AirPlay receiver's <c>airplay_controllers</c> table (SPEC-receiver.md 8.4); its <c>settings</c> keys need no
    /// schema change. The next change appends <c>new StoreMigration(3, "...", "...")</c> here.
    /// </summary>
    public static IReadOnlyList<StoreMigration> All { get; } =
    [
        new StoreMigration(
            2,
            "airplay_controllers table (SPEC-receiver.md 8.4)",
            "CREATE TABLE IF NOT EXISTS airplay_controllers (\n" +
            "    controller_id TEXT PRIMARY KEY,   -- the sender's HAP pairing identifier\n" +
            "    public_key    BLOB NOT NULL,      -- its Ed25519 LTPK, 32 bytes\n" +
            "    created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))\n" +
            ");"),
    ];

    /// <summary>The highest version <paramref name="migrations"/> lead to.</summary>
    public static int LatestVersion(IReadOnlyList<StoreMigration> migrations) =>
        migrations.Count == 0 ? BaseVersion : migrations[^1].Version;

    /// <summary>Throws <see cref="ArgumentException"/> unless the list starts at version 2 and is contiguous.</summary>
    public static void Validate(IReadOnlyList<StoreMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        var expected = BaseVersion + 1;
        foreach (var migration in migrations)
        {
            if (migration.Version != expected)
            {
                throw new ArgumentException($"Migration versions must be contiguous from {BaseVersion + 1}; expected {expected}, got {migration.Version}.", nameof(migrations));
            }

            if (string.IsNullOrWhiteSpace(migration.Sql))
            {
                throw new ArgumentException($"Migration {migration.Version} has no SQL.", nameof(migrations));
            }

            expected++;
        }
    }
}
