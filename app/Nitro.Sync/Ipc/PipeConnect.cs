using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Nitro.Sync.Ipc;

/// <summary>
/// Connects to the app's sync pipe and checks who owns it. PipeOptions.CurrentUserOnly rejects a pipe created by an
/// elevated NitroDrop (Windows makes BUILTIN\Administrators the owner), so the owner check is done here instead:
/// the current user or Administrators is accepted (an admin can already do anything on the machine); any other owner
/// (another user squatting the name) is refused.
/// </summary>
public static class PipeConnect
{
    public static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, int timeoutMs, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unix = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await unix.ConnectAsync(timeoutMs, ct); return unix; }
            catch { await unix.DisposeAsync(); throw; }
        }

        var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await c.ConnectAsync(timeoutMs, ct);
            var owner = c.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!IsTrustedOwner(owner))
                throw new UnauthorizedAccessException("The sync pipe is owned by another user.");
            return c;
        }
        catch { await c.DisposeAsync(); throw; }
    }

    internal static bool IsTrustedOwner(SecurityIdentifier? owner)
    {
        if (owner is null || !OperatingSystem.IsWindows()) return false;
        return owner == WindowsIdentity.GetCurrent().User || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
    }
}
