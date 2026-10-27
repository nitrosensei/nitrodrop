using System;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using AirDrop.App.Services;

namespace AirDrop.App.ViewModels;

public enum PeerState { Idle, Waiting, Sending, Sent, Declined, Failed }

public sealed class PeerViewModel : Observable
{
    public const double Size = 84;        // avatar diameter
    public const double ItemWidth = 132;  // avatar + name label column

    private static readonly CultureInfo Tr = new("tr-TR");

    private double _x, _y, _progress;
    private PeerState _state;
    private string _displayName;
    private string _initials;
    private ImageSource? _avatar;
    private bool _isTrusted, _isContact;
    private string? _speedText;

    public PeerViewModel(PeerInfo info)
    {
        Info = info;
        _displayName = OwnerOf(info.Name);
        _initials = MakeInitials(_displayName);
    }

    public PeerInfo Info { get; private set; }
    public string Id => Info.Id;
    public string Name => Info.Name;
    public DeviceKind Kind => Info.Kind;
    public string StableKey => Info.StableKey;

    /// <summary>Contact name / custom alias / owner part of the device name.</summary>
    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public string Initials { get => _initials; private set => Set(ref _initials, value); }

    public ImageSource? Avatar
    {
        get => _avatar;
        private set { if (Set(ref _avatar, value)) Raise(nameof(HasAvatar)); }
    }

    public bool HasAvatar => Avatar is not null;
    public bool IsTrusted { get => _isTrusted; private set => Set(ref _isTrusted, value); }
    public bool IsContact { get => _isContact; private set => Set(ref _isContact, value); }

    public string DeviceLabel => DeviceModels.Describe(Kind, Info.ModelName);

    public string Glyph => Kind switch
    {
        DeviceKind.iPhone => "",
        DeviceKind.iPad => "",
        DeviceKind.Mac => "",
        DeviceKind.Windows => "",
        _ => "",
    };

    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public PeerState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            if (!IsBusy) _speedText = null;
            Raise(nameof(StatusText));
            Raise(nameof(IsBusy));
        }
    }

    public bool IsBusy => State is PeerState.Waiting or PeerState.Sending;

    public string StatusText => State switch
    {
        PeerState.Waiting => "Bekleniyor…",
        PeerState.Sending => _speedText ?? "Gönderiliyor…",
        PeerState.Sent => "Gönderildi",
        PeerState.Declined => "Reddedildi",
        PeerState.Failed => "Başarısız",
        _ => DeviceLabel,
    };

    public void ReportProgress(TransferProgress p)
    {
        State = PeerState.Sending;
        Progress = p.Fraction;
        _speedText = Format.Speed(p);
        Raise(nameof(StatusText));
    }

    /// <summary>Precedence: user alias/photo → matched contact → device name + initials.</summary>
    public void ApplyIdentity(DevicePrefs? prefs, ContactMatch? contact)
    {
        IsContact = contact is not null;
        IsTrusted = prefs?.Trusted ?? false;
        DisplayName = prefs?.DisplayName ?? contact?.Name ?? OwnerOf(Name);
        Initials = MakeInitials(DisplayName);
        Avatar = ImageUtil.FromFile(prefs?.PhotoPath) ?? ImageUtil.FromBytes(contact?.Photo);
    }

    public void Update(PeerInfo info)
    {
        Info = info;
        Raise(nameof(DeviceLabel));
        Raise(nameof(StatusText));
    }

    /// <summary>Turkish possessive: "Emre'nin iPhone'u" → "Emre".</summary>
    internal static string OwnerOf(string name)
    {
        var owner = name.Split('\'', '’')[0].Trim();
        return owner.Length > 0 && owner.Length < name.Length ? owner : name;
    }

    internal static string MakeInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1)
            return (parts[0].Length >= 2 ? parts[0][..2] : parts[0]).ToUpper(Tr);
        return string.Concat(new[] { parts[0], parts[^1] }.Select(p => char.ToUpper(p[0], Tr)));
    }
}

internal static class Format
{
    private static readonly CultureInfo Tr = new("tr-TR");

    public static string Size(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#", Tr) + " KB";
        if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#", Tr) + " MB";
        return (b / 1073741824.0).ToString("0.##", Tr) + " GB";
    }

    public static string Speed(TransferProgress p)
    {
        var s = (p.BytesPerSecond / 1048576.0).ToString("0.0", Tr) + " MB/sn";
        if (p.Remaining is { } r)
            s += r.TotalSeconds < 60 ? $" · {Math.Ceiling(r.TotalSeconds)} sn" : $" · {Math.Ceiling(r.TotalMinutes)} dk";
        return s;
    }
}
