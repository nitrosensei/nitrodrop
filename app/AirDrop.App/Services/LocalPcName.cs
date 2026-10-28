using System;

namespace AirDrop.App.Services;

/// <summary>This PC's name for display. NITRODROP_DEMO_PCNAME overrides it so public screenshots don't show the real host name.</summary>
public static class LocalPcName
{
    public static string Value =>
        Environment.GetEnvironmentVariable("NITRODROP_DEMO_PCNAME") is { Length: > 0 } demo ? demo : Environment.MachineName;
}
