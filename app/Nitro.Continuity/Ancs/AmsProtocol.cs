using System.Text;

namespace Nitro.Continuity.Ancs;

// Apple Media Service (AMS) — "AMS Specification"
// https://developer.apple.com/library/archive/documentation/CoreBluetooth/Reference/AppleMediaService_Reference/Specification/Specification.html
// Same bonding requirement as ANCS. Bonus feature: now-playing + play/pause/next from the PC.

public static class AmsUuids
{
    public static readonly Guid Service = new("89D3502B-0F36-433A-8EF4-C502AD55F8DC");
    /// <summary>Write/notify: commands; notifications list the currently supported commands.</summary>
    public static readonly Guid RemoteCommand = new("9B3C81D8-57B1-4A8A-B8DF-0E56F7CA51C2");
    /// <summary>Write (subscribe EntityID + attrs) / notify (updates).</summary>
    public static readonly Guid EntityUpdate = new("2F7CABCE-808D-411F-9A0C-BB92BA96C102");
    /// <summary>Write/read: full value of a truncated attribute.</summary>
    public static readonly Guid EntityAttribute = new("C6B2F38C-23AB-46D8-A6AB-A3A870BBD5D7");
}

public enum AmsRemoteCommandId : byte
{
    Play = 0, Pause = 1, TogglePlayPause = 2, NextTrack = 3, PreviousTrack = 4,
    VolumeUp = 5, VolumeDown = 6, AdvanceRepeatMode = 7, AdvanceShuffleMode = 8,
    SkipForward = 9, SkipBackward = 10, LikeTrack = 11, DislikeTrack = 12, BookmarkTrack = 13,
}

public enum AmsEntityId : byte { Player = 0, Queue = 1, Track = 2 }

public enum AmsTrackAttributeId : byte { Artist = 0, Album = 1, Title = 2, Duration = 3 }
public enum AmsPlayerAttributeId : byte { Name = 0, PlaybackInfo = 1, Volume = 2 }

[Flags]
public enum AmsEntityUpdateFlags : byte { None = 0, Truncated = 1 }

/// <summary>Entity Update notification: EntityID(1) | AttributeID(1) | EntityUpdateFlags(1) | Value(UTF-8, rest).</summary>
public readonly record struct AmsEntityUpdate(AmsEntityId Entity, byte AttributeId, AmsEntityUpdateFlags Flags, string Value)
{
    public static AmsEntityUpdate Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 3) throw new FormatException("AMS Entity Update must be at least 3 bytes.");
        return new((AmsEntityId)d[0], d[1], (AmsEntityUpdateFlags)d[2], Encoding.UTF8.GetString(d[3..]));
    }

    /// <summary>Subscribe write: EntityID | AttributeID*.</summary>
    public static byte[] Subscribe(AmsEntityId entity, params byte[] attributes) => [(byte)entity, .. attributes];
}
