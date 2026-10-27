using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nitro.Sync.Net;

public static class SyncProtocol
{
    public const string ServiceType = "_nitrosync._tcp.local.";
    public const int Version = 1;
    public const int DefaultPort = 8771;
    public const int DefaultIosPort = 8772;

    public const string Prefix = "/nitrosync/v1";
    public const string Info = Prefix + "/info";
    public const string PairStart = Prefix + "/pair/start";
    public const string PairFinish = Prefix + "/pair/finish";
    public const string State = Prefix + "/state";
    public const string Open = Prefix + "/open";
    public const string Launch = Prefix + "/launch";
    public const string Unpair = Prefix + "/unpair";

    /// <summary>Request body limits (bytes).</summary>
    public const int MaxSmallBody = 16 * 1024;

    /// <summary>Longest a GET /state long-poll is held by the server.</summary>
    public static readonly TimeSpan MaxLongPoll = TimeSpan.FromSeconds(25);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record InfoResponse(string DeviceId, string Name, int Version);

public sealed record PairStartRequest(string DeviceId, string Name, string CertSha256, string EcdhPublicKey, string Nonce);
public sealed record PairStartResponse(string SessionId, string DeviceId, string Name, string EcdhPublicKey, string Nonce);
public sealed record PairFinishRequest(string SessionId, string Confirm);
public sealed record PairFinishResponse(string Confirm);

public sealed record OpenRequest(string Url, string? Title = null);
public sealed record LaunchRequest(string AppId);
