using System.Collections.Generic;
using System.Threading.Tasks;

namespace AirDrop.App.Services;

public sealed record ContactMatch(string Name, byte[]? Photo);

/// <summary>
/// Maps Apple-validated contact hashes to the user's own contacts (macOS-style avatars).
/// Only matches imported contacts; never tries to reverse unknown hashes.
/// </summary>
public interface IContactResolver
{
    int Count { get; }
    ContactMatch? Match(IReadOnlyList<string>? hashes);
    Task<int> ImportAsync(string vcfPath);
    void Clear();
}

public sealed class NullContactResolver : IContactResolver
{
    public int Count => 0;
    public ContactMatch? Match(IReadOnlyList<string>? hashes) => null;
    public Task<int> ImportAsync(string vcfPath) => Task.FromResult(0);
    public void Clear() { }
}
