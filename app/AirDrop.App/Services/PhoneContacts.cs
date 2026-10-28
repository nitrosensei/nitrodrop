using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AirDrop.Core.Contacts;
using Nitro.Continuity.Runtime;

namespace AirDrop.App.Services;

// No WPF types here: linked into AirDrop.Tests.

public sealed record PhoneContact(string Name, byte[]? Photo);

/// <summary>Caller / sender name + photo for the "Telefon" tab (numbers from MAP/ANCS/PhoneLine).</summary>
public interface IPhoneContacts
{
    PhoneContact? ByNumber(string? number);
    /// <summary>Exact (case-insensitive) display-name match, for ANCS titles that are already names.</summary>
    PhoneContact? ByName(string? name);
    /// <summary>Imports PBAP vCard text; returns how many contacts were added or updated.</summary>
    int ImportVCards(string vcardText);
}

/// <summary>Backed by AirDrop.Core's <see cref="ContactBook"/>: numbers are matched through the same hash forms as AirDrop.</summary>
public sealed class ContactBookPhoneContacts(ContactBook book) : IPhoneContacts
{
    public PhoneContact? ByNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number) || !number.Any(char.IsAsciiDigit)) return null;
        var c = book.Match(ContactHashing.PhoneHashes(number, book.Region));
        return c is null ? null : new PhoneContact(c.DisplayName, c.Photo);
    }

    public PhoneContact? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var c = book.Contacts.FirstOrDefault(x => string.Equals(x.DisplayName, name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        return c is null ? null : new PhoneContact(c.DisplayName, c.Photo);
    }

    public int ImportVCards(string vcardText) => book.ImportText(vcardText);
}

/// <summary>Fixed contacts for --demo / --snapshot / tests.</summary>
public sealed class DemoPhoneContacts : IPhoneContacts
{
    private readonly List<(string Name, string Number, byte[]? Photo)> _list =
    [
        ("Annem", "+905321112233", null),
        ("Zeynep Kaya", "+905554443322", null),
        ("Ahmet Yılmaz", "+905069998877", null),   // l10n-allow: sample contact name
        ("Mert Demir", "+905427776655", null),
    ];

    public PhoneContact? ByNumber(string? number)
    {
        var key = PhoneNumbers.Key(number);
        if (key.Length == 0) return null;
        var hit = _list.FirstOrDefault(c => PhoneNumbers.Key(c.Number) == key);
        return hit.Name is null ? null : new PhoneContact(hit.Name, hit.Photo);
    }

    public PhoneContact? ByName(string? name)
    {
        var hit = _list.FirstOrDefault(c => string.Equals(c.Name, name?.Trim(), StringComparison.CurrentCultureIgnoreCase));
        return hit.Name is null ? null : new PhoneContact(hit.Name, hit.Photo);
    }

    public int ImportVCards(string vcardText) => VCard.Parse(vcardText).Count;
}

/// <summary>"Telefon" preferences (%LOCALAPPDATA%\airdrop-win\phone-settings.json). Kept apart from AppSettings on purpose.</summary>
public sealed class PhoneSettings
{
    public bool NotificationToasts { get; set; } = true;
    public bool CallToasts { get; set; } = true;
    /// <summary>Show message text in toasts (2FA codes land on the PC — plan §5 privacy note).</summary>
    public bool ShowPreviews { get; set; } = true;
    public bool PlaySounds { get; set; } = true;

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airdrop-win", "phone-settings.json");

    public static PhoneSettings Load(string? path = null)
    {
        try
        {
            var p = path ?? DefaultPath;
            return File.Exists(p) ? JsonSerializer.Deserialize<PhoneSettings>(File.ReadAllText(p)) ?? new() : new();
        }
        catch (Exception) { return new(); }
    }

    public void Save(string? path = null)
    {
        try
        {
            var p = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}
