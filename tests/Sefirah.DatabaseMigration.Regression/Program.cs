using Sefirah.Data.AppDatabase.Migrations;
using SQLite;

SQLitePCL.Batteries_V2.Init();

if (args is ["--database", var sourceDatabase])
{
    RunExternalDatabase(sourceDatabase);
    return;
}

var root = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Temp", ".agents", "Sefirah", "database-migration-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var databasePath = Path.Combine(root, "legacy-schema-3.db");

try
{
    using var db = new SQLiteConnection(databasePath);
    CreateLegacySchema(db);
    SeedLegacyData(db);

    new SchemaVersion5Migration().Up(db);

    AssertPrimaryKey(db, "ApplicationEntity", "AppKey");
    AssertPrimaryKey(db, "ContactEntity", "Key");
    AssertPrimaryKey(db, "ConversationEntity", "Key");
    AssertPrimaryKey(db, "MessageEntity", "Key");
    AssertPrimaryKey(db, "AttachmentEntity", "Id");
    AssertPrimaryKey(db, "NotificationEntity", "Key");

    AssertEqual(2, db.ExecuteScalar<int>("SELECT COUNT(*) FROM PairedDeviceEntity"), "paired devices");
    AssertEqual(2, db.ExecuteScalar<int>("SELECT COUNT(*) FROM ApplicationEntity"), "per-device applications");
    AssertEqual("phone:com.example.app", db.ExecuteScalar<string>(
        "SELECT AppKey FROM ApplicationEntity WHERE DeviceId = 'phone'"), "application key");
    AssertEqual("phone:contact-1", db.ExecuteScalar<string>(
        "SELECT Key FROM ContactEntity LIMIT 1"), "contact key");
    AssertEqual("phone:contact-1:+8613800000000", db.ExecuteScalar<string>(
        "SELECT Key FROM PhoneNumberEntity LIMIT 1"), "phone-number key");
    AssertEqual("phone:42", db.ExecuteScalar<string>(
        "SELECT Key FROM ConversationEntity LIMIT 1"), "conversation key");
    AssertEqual("phone:1001", db.ExecuteScalar<string>(
        "SELECT Key FROM MessageEntity LIMIT 1"), "message key");
    AssertEqual("phone:1001", db.ExecuteScalar<string>(
        "SELECT MessageKey FROM AttachmentEntity LIMIT 1"), "attachment message key");
    AssertEqual("phone:notification-1", db.ExecuteScalar<string>(
        "SELECT Key FROM NotificationEntity LIMIT 1"), "notification key");

    var certificate = db.ExecuteScalar<byte[]>(
        "SELECT Certificate FROM PairedDeviceEntity WHERE DeviceId = 'phone'");
    if (!certificate.SequenceEqual(new byte[] { 1, 2, 3, 4 }))
        throw new InvalidOperationException("Pairing certificate changed during migration.");

    Console.WriteLine("Legacy schema 3 migration regression: PASS");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static void CreateLegacySchema(SQLiteConnection db)
{
    db.Execute("CREATE TABLE SchemaVersionEntity (Version integer PRIMARY KEY NOT NULL)");
    db.Execute("CREATE TABLE LocalDeviceEntity (DeviceId varchar PRIMARY KEY NOT NULL, DeviceName varchar)");
    db.Execute("CREATE TABLE PairedDeviceEntity (DeviceId varchar PRIMARY KEY NOT NULL, Name varchar, Model varchar, Certificate blob, WallpaperBytes blob, LastConnected bigint, Addresses varchar, PhoneNumbers varchar)");
    db.Execute("CREATE TABLE ApplicationEntity (PackageName varchar PRIMARY KEY NOT NULL, AppName varchar, AppDeviceInfo varchar)");
    db.Execute("CREATE TABLE ContactEntity (Id varchar PRIMARY KEY NOT NULL, DeviceId varchar, LookupKey varchar, DisplayName varchar, Number varchar, Avatar blob)");
    db.Execute("CREATE TABLE ConversationEntity (ThreadId integer PRIMARY KEY NOT NULL, DeviceId varchar, AddressesJson varchar, LastMessageTimestamp integer, LastMessage varchar, HasRead integer, TimeStamp integer)");
    db.Execute("CREATE TABLE MessageEntity (UniqueId integer PRIMARY KEY NOT NULL, ThreadId integer, DeviceId varchar, Body varchar, Timestamp integer, Read integer, SubscriptionId integer, MessageType integer, Address varchar)");
    db.Execute("CREATE TABLE AttachmentEntity (Id integer PRIMARY KEY AUTOINCREMENT NOT NULL, MessageUniqueId integer, Data blob)");
    db.Execute("CREATE TABLE NotificationEntity (Id varchar PRIMARY KEY NOT NULL, Pinned integer, DeviceId varchar, NotificationMessage varchar)");
}

static void SeedLegacyData(SQLiteConnection db)
{
    db.Execute("INSERT INTO SchemaVersionEntity VALUES (3)");
    db.Execute("INSERT INTO LocalDeviceEntity VALUES ('pc', 'Desktop')");
    db.Execute("INSERT INTO PairedDeviceEntity VALUES (?, ?, ?, ?, NULL, 1, '[]', '[]')", "phone", "Phone", "Model", new byte[] { 1, 2, 3, 4 });
    db.Execute("INSERT INTO PairedDeviceEntity VALUES (?, ?, ?, ?, NULL, 2, '[]', '[]')", "tablet", "Tablet", "Model", new byte[] { 5, 6, 7, 8 });
    db.Execute("INSERT INTO ApplicationEntity VALUES (?, ?, ?)", "com.example.app", "Example", "[{\"DeviceId\":\"phone\",\"Pinned\":true,\"Filter\":2},{\"DeviceId\":\"tablet\",\"Pinned\":false,\"Filter\":1}]");
    db.Execute("INSERT INTO ContactEntity VALUES ('contact-1', 'phone', 'lookup', 'Alice', '+8613800000000', NULL)");
    db.Execute("INSERT INTO ConversationEntity VALUES (42, 'phone', '[\"+8613800000000\"]', 10, 'hello', 1, 11)");
    db.Execute("INSERT INTO MessageEntity VALUES (1001, 42, 'phone', 'hello', 10, 1, 1, 1, '+8613800000000')");
    db.Execute("INSERT INTO AttachmentEntity (MessageUniqueId, Data) VALUES (1001, ?)", new byte[] { 9, 10 });
    db.Execute("INSERT INTO NotificationEntity VALUES (?, ?, ?, ?)", "phone|notification-1", 1, "phone", "{\"NotificationKey\":\"notification-1\",\"AppPackage\":\"com.example.app\",\"AppName\":\"Example\",\"Title\":\"Title\",\"Text\":\"Text\",\"TimestampMillis\":12,\"Messages\":[],\"Actions\":[]}");
}

static void AssertPrimaryKey(SQLiteConnection db, string table, string column)
{
    var info = db.Query<TableColumnInfo>($"PRAGMA table_info(\"{table}\")");
    var actual = info.SingleOrDefault(item => item.PrimaryKeyOrder > 0)?.Name;
    AssertEqual(column, actual, $"{table} primary key");
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {label} to be '{expected}', got '{actual}'.");
}

static void RunExternalDatabase(string sourceDatabase)
{
    var root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp", ".agents", "Sefirah", "database-migration-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var workingCopy = Path.Combine(root, "external-schema-3.db");
    File.Copy(sourceDatabase, workingCopy);

    try
    {
        using var db = new SQLiteConnection(workingCopy);
        var pairingsBefore = db.Query<PairingSnapshot>(
            "SELECT DeviceId, Name, Model, Certificate FROM PairedDeviceEntity ORDER BY DeviceId");
        var conversationsBefore = db.ExecuteScalar<int>("SELECT COUNT(*) FROM ConversationEntity");
        var messagesBefore = db.ExecuteScalar<int>("SELECT COUNT(*) FROM MessageEntity");

        new SchemaVersion5Migration().Up(db);

        var pairingsAfter = db.Query<PairingSnapshot>(
            "SELECT DeviceId, Name, Model, Certificate FROM PairedDeviceEntity ORDER BY DeviceId");
        AssertEqual(pairingsBefore.Count, pairingsAfter.Count, "external paired-device count");
        for (var index = 0; index < pairingsBefore.Count; index++)
        {
            AssertEqual(pairingsBefore[index].DeviceId, pairingsAfter[index].DeviceId, "external paired-device id");
            AssertEqual(pairingsBefore[index].Name, pairingsAfter[index].Name, "external paired-device name");
            if (!pairingsBefore[index].Certificate.SequenceEqual(pairingsAfter[index].Certificate))
                throw new InvalidOperationException("External pairing certificate changed during migration.");
        }

        AssertEqual(conversationsBefore, db.ExecuteScalar<int>("SELECT COUNT(*) FROM ConversationEntity"), "external conversations");
        AssertEqual(messagesBefore, db.ExecuteScalar<int>("SELECT COUNT(*) FROM MessageEntity"), "external messages");
        AssertPrimaryKey(db, "ApplicationEntity", "AppKey");
        AssertPrimaryKey(db, "ContactEntity", "Key");
        AssertPrimaryKey(db, "ConversationEntity", "Key");
        AssertPrimaryKey(db, "MessageEntity", "Key");
        AssertPrimaryKey(db, "NotificationEntity", "Key");

        Console.WriteLine($"External schema 3 migration regression: PASS ({pairingsAfter.Count} pairings, {conversationsBefore} conversations, {messagesBefore} messages)");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

sealed class TableColumnInfo
{
    public string Name { get; set; } = string.Empty;

    [Column("pk")]
    public int PrimaryKeyOrder { get; set; }
}

sealed class PairingSnapshot
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public byte[] Certificate { get; set; } = [];
}
