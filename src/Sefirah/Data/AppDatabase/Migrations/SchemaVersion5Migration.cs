using System.Text.Json;
using SQLite;

namespace Sefirah.Data.AppDatabase.Migrations;

/// <summary>
/// Rebuilds schema 3/4 tables whose primary keys changed in schema 5.
/// SQLite cannot add or replace a primary-key column with ALTER TABLE.
/// </summary>
public sealed class SchemaVersion5Migration : IMigration
{
    public int TargetVersion => 5;

    public void Up(SQLiteConnection db)
    {
        db.RunInTransaction(() =>
        {
            MigrateApplications(db);
            MigrateContacts(db);
            MigrateConversations(db);
            MigrateMessages(db);
            MigrateAttachments(db);
            MigrateCallLogs(db);
            MigrateNotifications(db);
        });
    }

    private static void MigrateApplications(SQLiteConnection db)
    {
        const string table = "ApplicationEntity";
        if (!NeedsPrimaryKeyMigration(db, table, "AppKey")) return;

        var rows = db.Query<LegacyApplication>(
            "SELECT PackageName, AppName, AppDeviceInfo FROM ApplicationEntity");
        RenameLegacyTable(db, table);
        db.Execute(
            """
            CREATE TABLE ApplicationEntity (
                AppKey varchar PRIMARY KEY NOT NULL,
                DeviceId varchar,
                PackageName varchar,
                AppName varchar,
                Pinned integer,
                Filter integer
            )
            """);

        foreach (var row in rows)
        {
            foreach (var device in ParseAppDevices(row.AppDeviceInfo))
            {
                if (string.IsNullOrWhiteSpace(device.DeviceId)) continue;
                var key = $"{device.DeviceId}:{row.PackageName}";
                db.Execute(
                    "INSERT OR REPLACE INTO ApplicationEntity " +
                    "(AppKey, DeviceId, PackageName, AppName, Pinned, Filter) VALUES (?, ?, ?, ?, ?, ?)",
                    key, device.DeviceId, row.PackageName, row.AppName, device.Pinned, device.Filter);
            }
        }
        DropLegacyTable(db, table);
    }

    private static void MigrateContacts(SQLiteConnection db)
    {
        const string table = "ContactEntity";
        if (!NeedsPrimaryKeyMigration(db, table, "Key")) return;

        var rows = db.Query<LegacyContact>(
            "SELECT Id, DeviceId, LookupKey, DisplayName, Number, Avatar FROM ContactEntity");
        RenameLegacyTable(db, table);
        db.Execute(
            """
            CREATE TABLE ContactEntity (
                Key varchar PRIMARY KEY NOT NULL,
                DeviceId varchar,
                ContactId varchar,
                LookupKey varchar,
                DisplayName varchar,
                Avatar blob
            )
            """);
        db.Execute(
            """
            CREATE TABLE IF NOT EXISTS PhoneNumberEntity (
                Key varchar PRIMARY KEY NOT NULL,
                ContactKey varchar,
                Number varchar
            )
            """);

        foreach (var row in rows)
        {
            var key = $"{row.DeviceId}:{row.Id}";
            db.Execute(
                "INSERT OR REPLACE INTO ContactEntity " +
                "(Key, DeviceId, ContactId, LookupKey, DisplayName, Avatar) VALUES (?, ?, ?, ?, ?, ?)",
                key, row.DeviceId, row.Id, row.LookupKey, row.DisplayName, row.Avatar);
            if (!string.IsNullOrWhiteSpace(row.Number))
            {
                db.Execute(
                    "INSERT OR REPLACE INTO PhoneNumberEntity (Key, ContactKey, Number) VALUES (?, ?, ?)",
                    $"{key}:{row.Number}", key, row.Number);
            }
        }
        DropLegacyTable(db, table);
    }

    private static void MigrateConversations(SQLiteConnection db)
    {
        const string table = "ConversationEntity";
        if (!NeedsPrimaryKeyMigration(db, table, "Key")) return;

        var rows = db.Query<LegacyConversation>(
            "SELECT ThreadId, DeviceId, AddressesJson, LastMessageTimestamp, LastMessage, HasRead, TimeStamp FROM ConversationEntity");
        RenameLegacyTable(db, table);
        db.Execute(
            """
            CREATE TABLE ConversationEntity (
                Key varchar PRIMARY KEY NOT NULL,
                DeviceId varchar,
                ThreadId bigint,
                AddressesJson varchar,
                LastMessageTimestamp bigint,
                LastMessage varchar,
                HasRead integer,
                TimeStamp bigint
            )
            """);

        foreach (var row in rows)
        {
            db.Execute(
                "INSERT OR REPLACE INTO ConversationEntity " +
                "(Key, DeviceId, ThreadId, AddressesJson, LastMessageTimestamp, LastMessage, HasRead, TimeStamp) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                $"{row.DeviceId}:{row.ThreadId}", row.DeviceId, row.ThreadId, row.AddressesJson,
                row.LastMessageTimestamp, row.LastMessage, row.HasRead, row.TimeStamp);
        }
        DropLegacyTable(db, table);
    }

    private static void MigrateMessages(SQLiteConnection db)
    {
        const string table = "MessageEntity";
        if (!NeedsPrimaryKeyMigration(db, table, "Key")) return;

        var rows = db.Query<LegacyMessage>(
            "SELECT UniqueId, ThreadId, DeviceId, Body, Timestamp, Read, SubscriptionId, MessageType, Address FROM MessageEntity");
        RenameLegacyTable(db, table);
        db.Execute(
            """
            CREATE TABLE MessageEntity (
                Key varchar PRIMARY KEY NOT NULL,
                ConversationKey varchar,
                DeviceId varchar,
                UniqueId bigint,
                ThreadId bigint,
                Body varchar,
                Timestamp bigint,
                Read integer,
                SubscriptionId integer,
                MessageType integer,
                Address varchar
            )
            """);

        foreach (var row in rows)
        {
            db.Execute(
                "INSERT OR REPLACE INTO MessageEntity " +
                "(Key, ConversationKey, DeviceId, UniqueId, ThreadId, Body, Timestamp, Read, SubscriptionId, MessageType, Address) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                $"{row.DeviceId}:{row.UniqueId}", $"{row.DeviceId}:{row.ThreadId}", row.DeviceId,
                row.UniqueId, row.ThreadId, row.Body, row.Timestamp, row.Read,
                row.SubscriptionId, row.MessageType, row.Address);
        }
        DropLegacyTable(db, table);
    }

    private static void MigrateAttachments(SQLiteConnection db)
    {
        const string table = "AttachmentEntity";
        if (!ColumnExists(db, table, "MessageUniqueId")) return;

        var rows = db.Query<LegacyAttachment>(
            "SELECT Id, MessageUniqueId, Data FROM AttachmentEntity");
        RenameLegacyTable(db, table);
        db.Execute(
            """
            CREATE TABLE AttachmentEntity (
                Id integer PRIMARY KEY AUTOINCREMENT NOT NULL,
                MessageKey varchar,
                Data blob
            )
            """);

        foreach (var row in rows)
        {
            var deviceId = db.ExecuteScalar<string?>(
                "SELECT DeviceId FROM MessageEntity WHERE UniqueId = ? LIMIT 1", row.MessageUniqueId);
            if (string.IsNullOrWhiteSpace(deviceId)) continue;
            db.Execute(
                "INSERT OR REPLACE INTO AttachmentEntity (Id, MessageKey, Data) VALUES (?, ?, ?)",
                row.Id, $"{deviceId}:{row.MessageUniqueId}", row.Data);
        }
        DropLegacyTable(db, table);
    }

    private static void MigrateCallLogs(SQLiteConnection db)
    {
        const string table = "CallLogEntity";
        if (!TableExists(db, table) || ColumnExists(db, table, "Key")) return;
        if (!ColumnExists(db, table, "LogKey")) return;

        var rows = db.Query<LegacyCallLog>(
            "SELECT DeviceId, CallLogId, PhoneNumber, TimestampMillis, DurationSeconds, CallType FROM CallLogEntity");
        RenameLegacyTable(db, table);
        db.Execute(
            """
            CREATE TABLE CallLogEntity (
                Key varchar PRIMARY KEY NOT NULL,
                DeviceId varchar,
                CallLogId bigint,
                PhoneNumber varchar,
                TimestampMillis bigint,
                DurationSeconds bigint,
                CallType integer
            )
            """);

        foreach (var row in rows)
        {
            db.Execute(
                "INSERT OR REPLACE INTO CallLogEntity " +
                "(Key, DeviceId, CallLogId, PhoneNumber, TimestampMillis, DurationSeconds, CallType) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?)",
                $"{row.DeviceId}:{row.CallLogId}", row.DeviceId, row.CallLogId, row.PhoneNumber,
                row.TimestampMillis, row.DurationSeconds, row.CallType);
        }
        DropLegacyTable(db, table);
    }

    private static void MigrateNotifications(SQLiteConnection db)
    {
        const string table = "NotificationEntity";
        if (!NeedsPrimaryKeyMigration(db, table, "Key")) return;

        var rows = db.Query<LegacyNotification>(
            "SELECT Id, Pinned, DeviceId, NotificationMessage FROM NotificationEntity");
        RenameLegacyTable(db, table);
        db.Execute(
            """
            CREATE TABLE NotificationEntity (
                Key varchar PRIMARY KEY NOT NULL,
                DeviceId varchar,
                NotificationKey varchar,
                Pinned integer,
                AppPackage varchar,
                AppName varchar,
                Title varchar,
                Text varchar,
                TimestampMillis bigint,
                GroupKey varchar,
                Tag varchar,
                ReplyResultKey varchar,
                LargeIcon blob,
                PayloadJson varchar
            )
            """);

        foreach (var row in rows)
        {
            if (!TryParseNotification(row, out var converted)) continue;
            db.Execute(
                "INSERT OR REPLACE INTO NotificationEntity " +
                "(Key, DeviceId, NotificationKey, Pinned, AppPackage, AppName, Title, Text, TimestampMillis, " +
                "GroupKey, Tag, ReplyResultKey, LargeIcon, PayloadJson) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                converted.Key, row.DeviceId, converted.NotificationKey, row.Pinned, converted.AppPackage,
                converted.AppName, converted.Title, converted.Text, converted.TimestampMillis,
                converted.GroupKey, converted.Tag, converted.ReplyResultKey, converted.LargeIcon, converted.PayloadJson);
        }
        DropLegacyTable(db, table);
    }

    private static bool TryParseNotification(LegacyNotification row, out ConvertedNotification notification)
    {
        notification = new ConvertedNotification();
        try
        {
            using var document = JsonDocument.Parse(row.NotificationMessage);
            var root = document.RootElement;
            var notificationKey = GetString(root, "NotificationKey") ??
                                  row.Id.Split('|', 2).LastOrDefault() ?? row.Id;
            byte[]? largeIcon = null;
            var encodedIcon = GetString(root, "LargeIcon");
            if (!string.IsNullOrWhiteSpace(encodedIcon))
            {
                try { largeIcon = Convert.FromBase64String(encodedIcon); }
                catch (FormatException) { }
            }

            var messages = root.TryGetProperty("Messages", out var messagesElement)
                ? messagesElement.GetRawText()
                : "[]";
            var actions = root.TryGetProperty("Actions", out var actionsElement)
                ? actionsElement.GetRawText()
                : "[]";

            notification = new ConvertedNotification
            {
                Key = $"{row.DeviceId}:{notificationKey}",
                NotificationKey = notificationKey,
                AppPackage = GetString(root, "AppPackage") ?? string.Empty,
                AppName = GetString(root, "AppName") ?? string.Empty,
                Title = GetString(root, "Title"),
                Text = GetString(root, "Text"),
                TimestampMillis = GetInt64(root, "TimestampMillis"),
                GroupKey = GetString(root, "GroupKey"),
                Tag = GetString(root, "Tag"),
                ReplyResultKey = GetString(root, "ReplyResultKey"),
                LargeIcon = largeIcon,
                PayloadJson = $"{{\"Messages\":{messages},\"Actions\":{actions}}}",
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<LegacyAppDevice> ParseAppDevices(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<LegacyAppDevice>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static long GetInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static bool NeedsPrimaryKeyMigration(SQLiteConnection db, string table, string expectedColumn) =>
        TableExists(db, table) && !ColumnExists(db, table, expectedColumn);

    private static bool TableExists(SQLiteConnection db, string table) => db.GetTableInfo(table).Count > 0;

    private static bool ColumnExists(SQLiteConnection db, string table, string column) =>
        db.GetTableInfo(table).Any(info => info.Name.Equals(column, StringComparison.OrdinalIgnoreCase));

    private static void RenameLegacyTable(SQLiteConnection db, string table) =>
        db.Execute($"ALTER TABLE \"{table}\" RENAME TO \"{table}_LegacyV5\"");

    private static void DropLegacyTable(SQLiteConnection db, string table) =>
        db.Execute($"DROP TABLE \"{table}_LegacyV5\"");

    private sealed class LegacyApplication
    {
        public string PackageName { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public string AppDeviceInfo { get; set; } = string.Empty;
    }

    private sealed class LegacyAppDevice
    {
        public string DeviceId { get; set; } = string.Empty;
        public bool Pinned { get; set; }
        public int Filter { get; set; } = 2;
    }

    private sealed class LegacyContact
    {
        public string Id { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string? LookupKey { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
        public byte[]? Avatar { get; set; }
    }

    private sealed class LegacyConversation
    {
        public long ThreadId { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string? AddressesJson { get; set; }
        public long LastMessageTimestamp { get; set; }
        public string? LastMessage { get; set; }
        public bool HasRead { get; set; }
        public long TimeStamp { get; set; }
    }

    private sealed class LegacyMessage
    {
        public long UniqueId { get; set; }
        public long ThreadId { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public long Timestamp { get; set; }
        public bool Read { get; set; }
        public int SubscriptionId { get; set; }
        public int MessageType { get; set; }
        public string Address { get; set; } = string.Empty;
    }

    private sealed class LegacyAttachment
    {
        public int Id { get; set; }
        public long MessageUniqueId { get; set; }
        public byte[]? Data { get; set; }
    }

    private sealed class LegacyCallLog
    {
        public string DeviceId { get; set; } = string.Empty;
        public long CallLogId { get; set; }
        public string PhoneNumber { get; set; } = string.Empty;
        public long TimestampMillis { get; set; }
        public long DurationSeconds { get; set; }
        public int CallType { get; set; }
    }

    private sealed class LegacyNotification
    {
        public string Id { get; set; } = string.Empty;
        public bool Pinned { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string NotificationMessage { get; set; } = string.Empty;
    }

    private sealed class ConvertedNotification
    {
        public string Key { get; set; } = string.Empty;
        public string NotificationKey { get; set; } = string.Empty;
        public string AppPackage { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Text { get; set; }
        public long TimestampMillis { get; set; }
        public string? GroupKey { get; set; }
        public string? Tag { get; set; }
        public string? ReplyResultKey { get; set; }
        public byte[]? LargeIcon { get; set; }
        public string PayloadJson { get; set; } = string.Empty;
    }
}
