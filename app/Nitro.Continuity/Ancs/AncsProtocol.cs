namespace Nitro.Continuity.Ancs;

// Apple Notification Center Service (ANCS) — constants from Apple's "ANCS Specification" (v1.1)
// https://developer.apple.com/library/archive/documentation/CoreBluetooth/Reference/AppleNotificationCenterServiceSpecification/Specification/Specification.html
// All multi-byte integers on the wire are little-endian.

public static class AncsUuids
{
    public static readonly Guid Service = new("7905F431-B5CE-4E99-A40F-4B1E122D00D0");
    /// <summary>Notifiable. 8-byte events (added / modified / removed).</summary>
    public static readonly Guid NotificationSource = new("9FBF120D-6301-42D9-8C58-25E699A21DBD");
    /// <summary>Writeable with response. Get Notification Attributes / Get App Attributes / Perform Notification Action.</summary>
    public static readonly Guid ControlPoint = new("69D1D8F3-45E1-49A8-9821-9BBDFDAAD9D9");
    /// <summary>Notifiable. Responses to Control Point commands, possibly split across several GATT notifications.</summary>
    public static readonly Guid DataSource = new("22EAC6E9-24D6-4BB5-BE44-B36ACE7C7BFB");
}

public enum AncsEventId : byte
{
    NotificationAdded = 0,
    NotificationModified = 1,
    NotificationRemoved = 2,
}

[Flags]
public enum AncsEventFlags : byte
{
    None = 0,
    Silent = 1 << 0,
    Important = 1 << 1,
    PreExisting = 1 << 2,
    PositiveAction = 1 << 3,
    NegativeAction = 1 << 4,
}

public enum AncsCategoryId : byte
{
    Other = 0,
    IncomingCall = 1,
    MissedCall = 2,
    Voicemail = 3,
    Social = 4,
    Schedule = 5,
    Email = 6,
    News = 7,
    HealthAndFitness = 8,
    BusinessAndFinance = 9,
    Location = 10,
    Entertainment = 11,
}

public enum AncsCommandId : byte
{
    GetNotificationAttributes = 0,
    GetAppAttributes = 1,
    PerformNotificationAction = 2,
}

public enum AncsNotificationAttributeId : byte
{
    AppIdentifier = 0,
    /// <summary>Requires a 2-byte max length in the request.</summary>
    Title = 1,
    /// <summary>Requires a 2-byte max length in the request.</summary>
    Subtitle = 2,
    /// <summary>Requires a 2-byte max length in the request.</summary>
    Message = 3,
    MessageSize = 4,
    /// <summary>"yyyyMMdd'T'HHmmSS" (UTS #35), local time of the phone.</summary>
    Date = 5,
    PositiveActionLabel = 6,
    NegativeActionLabel = 7,
}

public enum AncsAppAttributeId : byte
{
    DisplayName = 0,
}

public enum AncsActionId : byte
{
    Positive = 0,
    Negative = 1,
}

/// <summary>ATT error codes the ANCS Control Point can return (spec §2.4 "Error Codes").</summary>
public enum AncsError : byte
{
    UnknownCommand = 0xA0,
    InvalidCommand = 0xA1,
    InvalidParameter = 0xA2,
    ActionFailed = 0xA3,
}

public static class AncsAttributes
{
    /// <summary>Title, Subtitle and Message carry a 2-byte max-length parameter in the request; the rest do not.</summary>
    public static bool NeedsMaxLength(AncsNotificationAttributeId id) =>
        id is AncsNotificationAttributeId.Title or AncsNotificationAttributeId.Subtitle or AncsNotificationAttributeId.Message;

    /// <summary>A reasonable default set: everything the UI needs to render a toast and its two buttons.</summary>
    public static readonly AncsAttributeRequest[] Default =
    [
        new(AncsNotificationAttributeId.AppIdentifier),
        new(AncsNotificationAttributeId.Title, 64),
        new(AncsNotificationAttributeId.Subtitle, 64),
        new(AncsNotificationAttributeId.Message, 512),
        new(AncsNotificationAttributeId.Date),
        new(AncsNotificationAttributeId.PositiveActionLabel),
        new(AncsNotificationAttributeId.NegativeActionLabel),
    ];
}

public readonly record struct AncsAttributeRequest(AncsNotificationAttributeId Id, ushort MaxLength = 0);
