using Sefirah.Data.AppDatabase.Migrations;
using Sefirah.Data.AppDatabase.Models;
using SQLite;

namespace Sefirah.Data.AppDatabase;

public class DatabaseContext
{
    private const int CurrentSchemaVersion = 5;

    private static readonly IMigration[] Migrations = 
    [
        new SchemaVersion2Migration(),
        new SchemaVersion5Migration(),
    ];

    public SQLiteConnection Database { get; private set; }

    public DatabaseContext(ILogger<DatabaseContext> logger)
    {
        try
        {
            logger.Info("Initializing database context");
            Database = TryCreateDatabase(logger);
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to initialize database context", ex);
            throw;
        }
    }

    private static SQLiteConnection TryCreateDatabase(ILogger logger)
    {
        var databasePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, Constants.LocalSettings.DatabaseFileName);
        var db = new SQLiteConnection(databasePath)
        {
            BusyTimeout = TimeSpan.FromSeconds(5),
        };

        var hasSchemaVersionTable = db.GetTableInfo(nameof(SchemaVersionEntity)).Count > 0;
        int? storedSchemaVersion = hasSchemaVersionTable
            ? db.Table<SchemaVersionEntity>().OrderByDescending(v => v.Version).FirstOrDefault()?.Version
            : null;

        // If schema version doesn't match, run migrations when they exist; otherwise destructive fallback
        if (storedSchemaVersion != CurrentSchemaVersion)
        {
            BackupDatabaseFiles(databasePath, logger);

            if (storedSchemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Database schema {storedSchemaVersion} is newer than supported schema {CurrentSchemaVersion}. " +
                    "Refusing to modify it to preserve pairing data.");
            }

            var migrationsToRun = Migrations
                .Where(m => storedSchemaVersion.HasValue &&
                            m.TargetVersion > storedSchemaVersion.Value &&
                            m.TargetVersion <= CurrentSchemaVersion)
                .OrderBy(m => m.TargetVersion)
                .ToArray();
            if (migrationsToRun.Length > 0)
            {
                RunMigrations(db, migrationsToRun, logger);
            }

            // sqlite-net's CreateTable performs additive column migration for existing tables.
            // Missing historical migration classes must never cause a destructive fallback.
            CreateAllTables(db);
            SetSchemaVersion(db, CurrentSchemaVersion);
            logger.Info("Database schema updated successfully");
        }
        else
        {
            CreateAllTables(db);
        }

        return db;
    }

    private static void SetSchemaVersion(SQLiteConnection db, int version)
    {
        db.InsertOrReplace(new SchemaVersionEntity { Version = version });
    }

    private static void RunMigrations(SQLiteConnection db, IMigration[] migrationsToRun, ILogger logger)
    {
        foreach (var migration in migrationsToRun)
        {
            try
            {
                logger.Info($"Running migration to version {migration.TargetVersion}");
                migration.Up(db);
                SetSchemaVersion(db, migration.TargetVersion);
            }
            catch (Exception ex)
            {
                logger.Error($"Migration to version {migration.TargetVersion} failed. Pairing data was not deleted.", ex);
                throw;
            }
        }
    }

    private static void BackupDatabaseFiles(string databasePath, ILogger logger)
    {
        if (!File.Exists(databasePath)) return;

        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            ".agents",
            "Sefirah",
            "database-backups",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupRoot);

        foreach (var sourcePath in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (!File.Exists(sourcePath)) continue;
            File.Copy(sourcePath, Path.Combine(backupRoot, Path.GetFileName(sourcePath)), overwrite: false);
        }
        logger.Info($"Database backup created at {backupRoot}");
    }

    private static void CreateAllTables(SQLiteConnection db)
    {
        db.CreateTable<SchemaVersionEntity>();
        db.CreateTable<LocalDeviceEntity>();
        db.CreateTable<PairedDeviceEntity>();
        db.CreateTable<ApplicationEntity>();
        db.CreateTable<ContactEntity>();
        db.CreateTable<PhoneNumberEntity>();
        db.CreateTable<ConversationEntity>();
        db.CreateTable<MessageEntity>();
        db.CreateTable<AttachmentEntity>();
        db.CreateTable<CallLogEntity>();
        db.CreateTable<NotificationEntity>();
    }

    public static void DropAllTables(SQLiteConnection db)
    {
        db.DropTable<LocalDeviceEntity>();
        db.DropTable<PairedDeviceEntity>();
        db.DropTable<ApplicationEntity>();
        db.DropTable<PhoneNumberEntity>();
        db.DropTable<ContactEntity>();
        db.DropTable<ConversationEntity>();
        db.DropTable<MessageEntity>();
        db.DropTable<AttachmentEntity>();
        db.DropTable<CallLogEntity>();
        db.DropTable<NotificationEntity>();
        db.DropTable<SchemaVersionEntity>();
    }
}
