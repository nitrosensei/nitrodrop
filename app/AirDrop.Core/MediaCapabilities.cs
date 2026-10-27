using System.Text.Json;
using System.Text.Json.Nodes;

namespace AirDrop.Core;

/// <summary>
/// ReceiverMediaCapabilities: JSON (UTF-8 bytes in the /Discover response plist) telling the sender which
/// media formats the receiver can open. iOS uses it to decide whether to transcode HEIC→JPEG and HEVC→H.264
/// before upload.
///
/// What is known from primary sources: OpenDrop and PrivateDrop (seemoo-lab) both send only {"Version":1}
/// and iPhones deliver JPEG/H.264 to them. Everything beyond "Version" below is reconstructed from
/// memory of macOS captures and Apple's VideoToolbox/ImageIO key names.
/// UNVERIFIED: the exact key set/values macOS sends (Vendor/com.apple, Codecs/hvc1, ContainerFormats/
/// public.heif-standard). Needs a capture from a real Mac (/Discover response) to confirm.
/// </summary>
public static class MediaCapabilities
{
    /// <summary>"Compatible formats": no HEIF/HEVC, so the sender converts (what OpenDrop/PrivateDrop send).</summary>
    public const string CompatibleJson = "{\"Version\":1}";

    /// <summary>Best-known macOS-like value advertising HEVC decode and HEIF containers. UNVERIFIED.</summary>
    public static readonly string FullJson = BuildFull();

    public static string For(bool compatibleFormats) => compatibleFormats ? CompatibleJson : FullJson;

    private static string BuildFull()
    {
        var profileSupport = new JsonObject();
        foreach (var profile in new[] { "1", "2" }) // HEVC Main, Main10
            profileSupport[profile] = new JsonObject { ["VTMaxPlaybackLevel"] = 153, ["VTMaxDecodeLevel"] = 153 };
        var root = new JsonObject
        {
            ["Version"] = 1,
            ["Vendor"] = new JsonObject
            {
                ["com.apple"] = new JsonObject
                {
                    ["LivePhotoFormatVersion"] = "1",
                    ["OSVersion"] = new JsonArray(14, 0, 0),
                },
            },
            ["Codecs"] = new JsonObject
            {
                ["hvc1"] = new JsonObject
                {
                    ["Profiles"] = new JsonObject
                    {
                        ["VTPerProfileSupport"] = profileSupport,
                        ["VTSupportedProfiles"] = new JsonArray(1, 2),
                    },
                },
            },
            ["ContainerFormats"] = new JsonObject
            {
                ["public.heif-standard"] = new JsonObject
                {
                    ["HeifSubtypes"] = new JsonArray("public.heic", "public.heif", "public.avci"),
                },
            },
        };
        return root.ToJsonString();
    }

    /// <summary>
    /// Sender side: does the receiver claim HEIF/HEVC support? Missing/unparseable → false (legacy receiver,
    /// which is how Apple senders treat OpenDrop).
    /// </summary>
    public static bool SupportsHeif(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            bool heif = r.TryGetProperty("ContainerFormats", out var cf) && cf.ValueKind == JsonValueKind.Object &&
                        cf.EnumerateObject().Any(p => p.Name.Contains("heif", StringComparison.OrdinalIgnoreCase));
            bool hevc = r.TryGetProperty("Codecs", out var c) && c.ValueKind == JsonValueKind.Object &&
                        c.EnumerateObject().Any(p => p.Name is "hvc1" or "hev1");
            return heif || hevc;
        }
        catch (JsonException) { return false; }
    }
}
