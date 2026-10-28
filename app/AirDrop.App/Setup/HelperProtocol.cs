using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AirDrop.App.Localization;

namespace AirDrop.App.Setup;

/// <summary>Everything the elevated helper (<c>NitroDrop.exe --setup-helper</c>) can do. Nothing else runs elevated.</summary>
public enum HelperAction
{
    BitLockerStatus,
    RebootToFirmware,
    TestSigningOn,
    InstallDriver,
    UninstallDriver,
}

public sealed class HelperRequest
{
    public HelperAction Action { get; set; }
    public bool DryRun { get; set; }
    public string Drive { get; set; } = "C:";
    /// <summary>RebootToFirmware / TestSigningOn: also suspend BitLocker for one reboot (one UAC prompt for both).</summary>
    public bool SuspendBitLocker { get; set; }
    /// <summary>InstallDriver: folder with inf, sys, cat and firmware (user's cache; copied to a protected folder first).</summary>
    public string? PackageDir { get; set; }
    public string? HardwareId { get; set; }
    /// <summary>UninstallDriver: our per-install certificate.</summary>
    public string? CertThumbprint { get; set; }
    public bool DisableTestSigning { get; set; }
    public bool ResumeBitLocker { get; set; }
    /// <summary>UI language of the wizard, so the helper's messages come back in the same language.</summary>
    public string? Language { get; set; } = Loc.Language;
}

public sealed class RecoveryKey
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>48-digit numerical password. Kept in memory / the pipe only; never written to the log.</summary>
    [JsonPropertyName("password")] public string? Password { get; set; }
}

/// <summary>Win32_EncryptableVolume summary for the system drive.</summary>
public sealed class BitLockerInfo
{
    [JsonPropertyName("present")] public bool Present { get; set; }
    /// <summary>0 = protection off, 1 = on, 2 = unknown (locked).</summary>
    [JsonPropertyName("protectionStatus")] public int ProtectionStatus { get; set; }
    /// <summary>0 fully decrypted, 1 fully encrypted, 2 encrypting, 3 decrypting, 4/5 paused.</summary>
    [JsonPropertyName("conversionStatus")] public int ConversionStatus { get; set; }
    [JsonPropertyName("percent")] public double Percent { get; set; }
    [JsonPropertyName("recovery")] public List<RecoveryKey> Recovery { get; set; } = new();

    [JsonIgnore] public bool IsProtected => Present && ProtectionStatus == 1;
    /// <summary>Encrypted but protectors disabled ("suspended") — still needs the key backed up.</summary>
    [JsonIgnore] public bool IsSuspended => Present && ProtectionStatus == 0 && ConversionStatus is 1 or 2 or 4;
    [JsonIgnore] public bool IsEncrypted => Present && ConversionStatus != 0;

    public BitLockerQuickState ToQuick() =>
        !Present || !IsEncrypted ? BitLockerQuickState.NotEncrypted
        : IsProtected ? BitLockerQuickState.On
        : IsSuspended ? BitLockerQuickState.Suspended
        : BitLockerQuickState.Off;

    /// <summary>Parses the JSON printed by <see cref="SetupCommands.BitLockerQuery"/> (tolerates a lone object instead of an array).</summary>
    public static BitLockerInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json.Trim());
        var r = doc.RootElement;
        var info = new BitLockerInfo
        {
            Present = r.TryGetProperty("present", out var p) && p.ValueKind == JsonValueKind.True,
            ProtectionStatus = Int(r, "protectionStatus"),
            ConversionStatus = Int(r, "conversionStatus"),
            Percent = r.TryGetProperty("percent", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : 0,
        };
        if (r.TryGetProperty("recovery", out var rec))
        {
            IEnumerable<JsonElement> items = rec.ValueKind switch
            {
                JsonValueKind.Array => rec.EnumerateArray(),
                JsonValueKind.Object => new[] { rec },
                _ => Array.Empty<JsonElement>(),
            };
            foreach (var k in items)
            {
                var id = k.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                info.Recovery.Add(new RecoveryKey
                {
                    Id = id!,
                    Password = k.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.String ? pw.GetString() : null,
                });
            }
        }
        return info;
    }

    private static int Int(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>Text file the user saves / prints. Same layout Windows uses for "BitLocker Recovery Key *.TXT".</summary>
    public string FormatBackup(string computerName, DateTime now)
    {
        var sb = new StringBuilder();
        const string MsUrl = "https://aka.ms/myrecoverykey";
        sb.AppendLine(SetupStrings.Setup_Backup_Title);
        sb.AppendLine();
        sb.AppendLine(SetupStrings.Setup_Backup_Computer(computerName));
        sb.AppendLine(SetupStrings.Setup_Backup_Date(now.ToString("d MMMM yyyy HH:mm", Loc.Culture)));
        sb.AppendLine();
        sb.AppendLine(SetupStrings.Setup_Backup_Intro);
        sb.AppendLine();
        foreach (var k in Recovery)
        {
            sb.AppendLine(SetupStrings.Setup_Backup_KeyId(k.Id.Trim('{', '}')));
            sb.AppendLine(SetupStrings.Setup_Backup_Key(k.Password ?? SetupStrings.Setup_Backup_Unreadable(MsUrl)));
            sb.AppendLine();
        }
        sb.AppendLine(SetupStrings.Setup_Backup_StoreOutside);
        sb.AppendLine(SetupStrings.Setup_Backup_MsAccount(MsUrl));
        return sb.ToString();
    }
}

public sealed class HelperResult
{
    public bool Ok { get; set; }
    /// <summary>The user said no to the UAC prompt.</summary>
    public bool Cancelled { get; set; }
    public string Message { get; set; } = "";
    public int ExitCode { get; set; }
    public bool RebootRequired { get; set; }
    public bool DryRun { get; set; }
    public BcdOutcome? Bcd { get; set; }
    public BitLockerInfo? BitLocker { get; set; }
    public string? CertThumbprint { get; set; }
    public List<string> PublishedInfs { get; set; } = new();
    public bool DriverBound { get; set; }
    public bool BitLockerSuspended { get; set; }

    public static HelperResult Fail(string message, int code = 1) => new() { Ok = false, Message = message, ExitCode = code };
}

/// <summary>
/// Command line between the UI and the elevated helper:
/// <c>--setup-helper --pipe &lt;name&gt; --request &lt;base64url(JSON)&gt;</c>. The result comes back as JSON over the pipe.
/// </summary>
public static class HelperProtocol
{
    public const string HelperFlag = "--setup-helper";
    public const string PipeFlag = "--pipe";
    public const string RequestFlag = "--request";

    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool IsHelperInvocation(IReadOnlyList<string> args) => args.Contains(HelperFlag);

    public static string BuildArguments(string pipeName, HelperRequest request) =>
        CommandLine.Join(new[] { HelperFlag, PipeFlag, pipeName, RequestFlag, Encode(request) });

    public static bool TryParse(IReadOnlyList<string> args, out string pipeName, out HelperRequest? request)
    {
        pipeName = "";
        request = null;
        if (!IsHelperInvocation(args)) return false;
        string? Value(string flag)
        {
            for (int i = 0; i < args.Count - 1; i++)
                if (args[i] == flag) return args[i + 1];
            return null;
        }
        var pipe = Value(PipeFlag);
        var req = Value(RequestFlag);
        if (string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(req)) return false;
        if (!pipe.StartsWith("NitroDrop.Setup.", StringComparison.Ordinal) || pipe.Length > 80) return false;
        try { request = Decode(req); }
        catch (Exception) { return false; }
        pipeName = pipe;
        return request is not null;
    }

    public static string Encode(HelperRequest r) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(r, Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static HelperRequest? Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<HelperRequest>(Convert.FromBase64String(s), Json);
    }
}
