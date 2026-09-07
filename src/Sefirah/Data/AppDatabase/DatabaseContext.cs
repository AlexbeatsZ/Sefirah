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
        Console.WriteLine("[DEBUG] DatabaseContext: constructor starting...");
        try
        {
            Console.WriteLine("[DEBUG] DatabaseContext: calling logger.Info...");
            logger.Info("Initializing database context");
            Console.WriteLine("[DEBUG] DatabaseContext: calling TryCreateDatabase...");
            Database = TryCreateDatabase(logger);
            Console.WriteLine("[DEBUG] DatabaseContext: database created successfully.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] DatabaseContext initialization failed: {ex}");
            logger.Error($"Failed to initialize database context", ex);
            throw;
        }
    }

    private static SQLiteConnection TryCreateDatabase(ILogger logger)
    {
        Console.WriteLine("[DEBUG] TryCreateDatabase: getting LocalFolder.Path...");
        var localFolderPath = ApplicationData.Current.LocalFolder.Path;
        Console.WriteLine($"[DEBUG] TryCreateDatabase: LocalFolder.Path = {localFolderPath}");
        Directory.CreateDirectory(localFolderPath);
        var databasePath = Path.Combine(localFolderPath, Constants.LocalSettings.DatabaseFileName);
        Console.WriteLine($"[DEBUG] TryCreateDatabase: databasePath = {databasePath}");
        Console.WriteLine("[DEBUG] TryCreateDatabase: opening SQLiteConnection...");
        var db = new SQLiteConnection(databasePath)
        {
            BusyTimeout = TimeSpan.FromSeconds(5),
        };
        Console.WriteLine("[DEBUG] TryCreateDatabase: SQLiteConnection opened. Checking SchemaVersionEntity table info...");

        var hasSchemaVersionTable = db.GetTableInfo(nameof(SchemaVersionEntity)).Count > 0;
        Console.WriteLine($"[DEBUG] TryCreateDatabase: hasSchemaVersionTable = {hasSchemaVersionTable}");
        int? storedSchemaVersion = hasSchemaVersionTable
            ? db.Table<SchemaVersionEntity>().OrderByDescending(v => v.Version).FirstOrDefault()?.Version
            : null;
        Console.WriteLine($"[DEBUG] TryCreateDatabase: storedSchemaVersion = {storedSchemaVersion}");

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
        Console.WriteLine("[DEBUG] CreateAllTables: SchemaVersionEntity...");
        db.CreateTable<SchemaVersionEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: LocalDeviceEntity...");
        db.CreateTable<LocalDeviceEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: PairedDeviceEntity...");
        db.CreateTable<PairedDeviceEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: ApplicationEntity...");
        db.CreateTable<ApplicationEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: ContactEntity...");
        db.CreateTable<ContactEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: PhoneNumberEntity...");
        db.CreateTable<PhoneNumberEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: ConversationEntity...");
        db.CreateTable<ConversationEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: MessageEntity...");
        db.CreateTable<MessageEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: AttachmentEntity...");
        db.CreateTable<AttachmentEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: CallLogEntity...");
        db.CreateTable<CallLogEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: NotificationEntity...");
        db.CreateTable<NotificationEntity>();
        Console.WriteLine("[DEBUG] CreateAllTables: All tables created.");
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
