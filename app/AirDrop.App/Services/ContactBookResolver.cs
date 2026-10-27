using System.Collections.Generic;
using System.Threading.Tasks;
using AirDrop.Core.Contacts;

namespace AirDrop.App.Services;

/// <summary><see cref="IContactResolver"/> backed by AirDrop.Core's persisted <see cref="ContactBook"/>.</summary>
public sealed class ContactBookResolver : IContactResolver
{
    private readonly ContactBook _book;

    public ContactBookResolver(ContactBook book) => _book = book;

    /// <summary>Opens %LOCALAPPDATA%\airdrop-win\contacts (Turkey as the default phone region).</summary>
    public static ContactBookResolver OpenDefault() => new(ContactBook.OpenDefault());

    public ContactBook Book => _book;

    public int Count => _book.Count;

    public ContactMatch? Match(IReadOnlyList<string>? hashes) =>
        _book.Match(hashes) is { } c ? new ContactMatch(c.DisplayName, c.Photo) : null;

    public Task<int> ImportAsync(string vcfPath) => _book.ImportAsync(vcfPath);

    public void Clear() => _book.Clear();
}
