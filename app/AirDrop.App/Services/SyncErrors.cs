using System;
using System.Collections.Generic;
using AirDrop.App.Localization;

namespace AirDrop.App.Services;

/// <summary>
/// Nitro.Sync reports pairing/connection errors as fixed Turkish strings (PairResult.Error, SyncPeerInfo.LastError).
/// The library stays language-neutral to the app: these literals are matched here and replaced with the UI language's
/// text; anything else (exception messages) is shown as is. Keep in sync with Nitro.Sync/NitroSyncService.cs.
/// This file is allowlisted by the "no hard-coded Turkish" test: the literals are protocol values, not UI text.
/// </summary>
internal static class SyncErrors
{
    /// <summary>The user (on either side) declined the code — not an error worth showing.</summary>
    public const string Declined = "Eşleştirme reddedildi";
    public const string NotFound = "Cihaz ağda bulunamadı";

    private static readonly Dictionary<string, Func<string>> Known = new(StringComparer.Ordinal)
    {
        [NotFound] = () => DevicesStrings.Pair_ErrNotFound,
        ["Cihaz kimliği uyuşmuyor"] = () => DevicesStrings.Pair_ErrIdMismatch,
        ["Geçersiz yanıt"] = () => DevicesStrings.Pair_ErrBadResponse,
        ["Anahtar doğrulaması başarısız"] = () => DevicesStrings.Pair_ErrKey,
        ["Karşı taraf reddetti"] = () => DevicesStrings.Pair_ErrRejected,
        ["Senkron çalışmıyor"] = () => DevicesStrings.Pair_ErrNoSync,
    };

    /// <summary>Localized text for a Nitro.Sync error string (unknown messages pass through).</summary>
    public static string Text(string error) => Known.TryGetValue(error, out var f) ? f() : error;
}
