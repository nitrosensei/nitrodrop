using System.Runtime.InteropServices;
using System.Text;
using AirDrop.App.Services;

namespace AirDrop.Tests;

/// <summary>
/// Explorer "Send with NitroDrop" (tr: "NitroDrop ile gönder"): --send / --send-list parsing, send batching, the HKCU verb + SendTo builder.
/// Nothing here touches the real registry or SendTo folder (fakes); list files go to %TEMP% and are deleted.
/// </summary>
public class ExplorerMenuTests
{
    // ---------------- --send / --send-list ----------------

    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void Send_collects_paths_until_next_flag()
    {
        var r = LaunchArgs.ParseSend(new[] { "--send", @"C:\a.txt", @"C:\Klasör b", "--background", @"C:\c.txt" },
                                     Existing(@"C:\a.txt", @"C:\Klasör b", @"C:\c.txt"));
        Assert.True(r.Requested);
        Assert.Equal(new[] { @"C:\a.txt", @"C:\Klasör b" }, r.Paths);
    }

    [Fact]
    public void Send_drops_missing_and_duplicate_paths_keeping_order()
    {
        var r = LaunchArgs.ParseSend(new[] { "--send", @"C:\b", @"C:\gone", @"C:\a", @"c:\B" }, Existing(@"C:\a", @"C:\b"));
        Assert.Equal(new[] { @"C:\b", @"C:\a" }, r.Paths);
    }

    [Fact]
    public void No_send_flag_is_not_a_request()
    {
        var r = LaunchArgs.ParseSend(new[] { "--snapshot", @"C:\out", "--demo" }, _ => true);
        Assert.False(r.Requested);
        Assert.Empty(r.Paths);
        Assert.Same(SendRequest.None, r);
    }

    [Fact]
    public void Send_without_existing_paths_is_still_a_request()
    {
        var r = LaunchArgs.ParseSend(new[] { "--send", @"C:\gone" }, _ => false);
        Assert.True(r.Requested);
        Assert.Empty(r.Paths);
    }

    [Fact]
    public void Send_list_uses_the_reader_and_merges_with_send()
    {
        string? asked = null;
        var r = LaunchArgs.ParseSend(new[] { "--send-list", @"C:\t\list.txt", "--send", @"C:\x", @"C:\a" },
            Existing(@"C:\a", @"C:\b", @"C:\x"),
            f => { asked = f; return new[] { @"C:\a", @"C:\b", @"C:\missing" }; });
        Assert.Equal(@"C:\t\list.txt", asked);
        Assert.Equal(new[] { @"C:\a", @"C:\b", @"C:\x" }, r.Paths);
    }

    [Fact]
    public void Send_list_value_is_not_taken_as_a_send_path_and_reader_errors_are_swallowed()
    {
        var r = LaunchArgs.ParseSend(new[] { "--send", @"C:\a", "--send-list", @"C:\a" }, _ => true,
                                     _ => throw new IOException("locked"));
        Assert.True(r.Requested);
        Assert.Equal(new[] { @"C:\a" }, r.Paths);

        var missingValue = LaunchArgs.ParseSend(new[] { "--send-list" }, _ => true, _ => throw new InvalidOperationException());
        Assert.True(missingValue.Requested);
        Assert.Empty(missingValue.Paths);
    }

    // ---------------- list file ----------------

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "airdrop-win-tests", "sendlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void List_file_round_trip_keeps_unicode_and_spaces()
    {
        var dir = TempDir();
        var paths = new[] { @"C:\Users\Emre Can\Masaüstü\çiğdem şükür.jpg", @"D:\İŞ\ğ ü ş ı ö ç", @"\\server\share\a b.txt" };
        var file = SendListFile.Write(paths, dir);
        Assert.StartsWith(SendListFile.Prefix, Path.GetFileName(file));
        Assert.EndsWith(SendListFile.Extension, file);
        var bytes = File.ReadAllBytes(file);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        Assert.Equal(string.Concat(paths.Select(p => p + "\r\n")), Encoding.UTF8.GetString(bytes));
        Assert.Equal(paths, SendListFile.Read(file));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void List_file_reader_accepts_bom_lf_and_blank_lines()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "x.txt");
        File.WriteAllText(file, "\r\nC:\\a\n\n  C:\\b b  \r\n", new UTF8Encoding(true));
        Assert.Equal(new[] { @"C:\a", @"C:\b b" }, SendListFile.Read(file));
        Assert.Empty(SendListFile.Read(Path.Combine(dir, "none.txt")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Only_our_temp_list_files_are_deleted()
    {
        var other = TempDir();
        var foreign = Path.Combine(other, SendListFile.Prefix + "1.txt");   // right name, wrong folder
        File.WriteAllText(foreign, @"C:\a");
        Assert.Equal(new[] { @"C:\a" }, SendListFile.ReadAndDelete(foreign));
        Assert.True(File.Exists(foreign));
        Assert.True(SendListFile.IsOurTempFile(foreign, other));
        Assert.False(SendListFile.IsOurTempFile(Path.Combine(other, "notes.txt"), other));
        Assert.False(SendListFile.IsOurTempFile(Path.Combine(other, SendListFile.Prefix + "1.exe"), other));
        Directory.Delete(other, true);
    }

    [Fact]
    public void Send_list_end_to_end_reads_and_deletes_the_temp_file()
    {
        var dir = TempDir();
        var a = Path.Combine(dir, "bir iki.txt");
        var b = Path.Combine(dir, "üç");
        File.WriteAllText(a, "x");
        Directory.CreateDirectory(b);
        var list = SendListFile.Write(new[] { a, b, Path.Combine(dir, "yok.txt") });   // default: %TEMP%
        Assert.True(SendListFile.IsOurTempFile(list));

        var r = LaunchArgs.ParseSend(new[] { "--send-list", list });
        Assert.Equal(new[] { a, b }, r.Paths);
        Assert.False(File.Exists(list));
        Directory.Delete(dir, true);
    }

    // ---------------- quoting ----------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static string[] Argv(string cmdLine)
    {
        var p = CommandLineToArgvW(cmdLine, out int argc);
        try { return Enumerable.Range(0, argc).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(p); }
    }

    [Theory]
    [InlineData(@"C:\a.txt")]
    [InlineData(@"C:\Program Files\NitroDrop\NitroDrop.exe")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\dir with space\")]
    [InlineData(@"\\server\share\a b")]
    [InlineData("say \"hi\"")]
    [InlineData(@"tail\\")]
    [InlineData("")]
    [InlineData(@"C:\Kullanıcılar\Şule\Masaüstü\ğ.png")]
    public void Quote_round_trips_through_CommandLineToArgvW(string arg)
    {
        var argv = Argv("x.exe " + LaunchArgs.Quote(arg) + " next");
        Assert.Equal(new[] { "x.exe", arg, "next" }, argv);
    }

    // ---------------- HKCU verb + SendTo ----------------

    private sealed class FakeRegistry : IUserRegistry
    {
        public readonly Dictionary<string, Dictionary<string, string>> Keys = new(StringComparer.OrdinalIgnoreCase);
        public int Writes;

        public string? GetString(string subKey, string? name) =>
            Keys.TryGetValue(subKey, out var v) && v.TryGetValue(name ?? "", out var s) ? s : null;

        public void SetString(string subKey, string? name, string value)
        {
            Writes++;
            if (!Keys.TryGetValue(subKey, out var v)) Keys[subKey] = v = new(StringComparer.OrdinalIgnoreCase);
            v[name ?? ""] = value;
        }

        public void DeleteTree(string subKey)
        {
            Writes++;
            foreach (var k in Keys.Keys.Where(k => k.Equals(subKey, StringComparison.OrdinalIgnoreCase) ||
                                                   k.StartsWith(subKey + @"\", StringComparison.OrdinalIgnoreCase)).ToList())
                Keys.Remove(k);
        }
    }

    private sealed class FakeShortcuts : IShortcutStore
    {
        public readonly Dictionary<string, (string Target, string Args, string Description)> Links = new(StringComparer.OrdinalIgnoreCase);
        public string SendToFolder => @"C:\Users\emre\AppData\Roaming\Microsoft\Windows\SendTo";
        public bool Exists(string path) => Links.ContainsKey(path);
        public void Create(string path, string target, string arguments, string description) => Links[path] = (target, arguments, description);
        public void Delete(string path) => Links.Remove(path);
    }

    private const string Exe = @"C:\NitroDrop portable\NitroDrop.exe";

    [Fact]
    public void Command_quotes_exe_and_placeholder()
    {
        Assert.Equal("\"C:\\NitroDrop portable\\NitroDrop.exe\" --send \"%1\"", ExplorerMenu.BuildCommand(Exe));
        Assert.Equal("\"C:\\ND\\NitroDrop.exe\" --send \"%1\"", ExplorerMenu.BuildCommand(@"C:\ND\NitroDrop.exe"));
        Assert.Throws<ArgumentException>(() => ExplorerMenu.BuildCommand("C:\\a\"b.exe"));
        Assert.Throws<ArgumentException>(() => ExplorerMenu.BuildCommand(" "));
    }

    [Theory]
    [InlineData(@"C:\Users\Emre Can\Masaüstü\çiğdem şükür.jpg")]
    [InlineData(@"D:\a.txt")]
    [InlineData(@"\\nas\paylaşım\klasör adı")]
    public void Command_after_Explorer_substitution_parses_to_exe_send_path(string selected)
    {
        var cmd = ExplorerMenu.BuildCommand(Exe).Replace("%1", selected);
        Assert.Equal(new[] { Exe, "--send", selected }, Argv(cmd));
        var parsed = LaunchArgs.ParseSend(Argv(cmd).Skip(1).ToArray(), Existing(selected));
        Assert.Equal(new[] { selected }, parsed.Paths);
    }

    [Fact]
    public void Exe_is_recovered_from_the_command()
    {
        Assert.Equal(Exe, ExplorerMenu.ExeFromCommand(ExplorerMenu.BuildCommand(Exe)));
        Assert.Equal(@"C:\x\NitroDrop.exe", ExplorerMenu.ExeFromCommand(@"C:\x\NitroDrop.exe --send ""%1"""));
        Assert.Null(ExplorerMenu.ExeFromCommand(null));
        Assert.Null(ExplorerMenu.ExeFromCommand("\"\" x"));
    }

    [Fact]
    public void Register_writes_both_verbs_and_the_sendto_shortcut()
    {
        var reg = new FakeRegistry();
        var lnk = new FakeShortcuts();
        ExplorerMenu.Register(reg, lnk, Exe);

        Assert.Equal(new[] { @"Software\Classes\*\shell\NitroDrop", @"Software\Classes\Directory\shell\NitroDrop" }, ExplorerMenu.ShellKeys);
        foreach (var key in ExplorerMenu.ShellKeys)
        {
            Assert.Equal("NitroDrop ile gönder", reg.GetString(key, "MUIVerb"));
            Assert.Equal(Exe + ",0", reg.GetString(key, "Icon"));
            Assert.Equal("Player", reg.GetString(key, "MultiSelectModel"));
            Assert.Equal(ExplorerMenu.BuildCommand(Exe), reg.GetString(key + @"\command", null));
        }
        var link = Assert.Single(lnk.Links);
        Assert.Equal(Path.Combine(lnk.SendToFolder, "NitroDrop.lnk"), link.Key);
        Assert.Equal((Exe, "--send", UiStrings.SendToShortcutDescription), link.Value);
        Assert.Equal(ExplorerMenuState.On, ExplorerMenu.GetState(reg, Exe));
        Assert.Equal(ExplorerMenuState.OtherExe, ExplorerMenu.GetState(reg, @"D:\other\NitroDrop.exe"));

        ExplorerMenu.Unregister(reg, lnk);
        Assert.Empty(reg.Keys);
        Assert.Empty(lnk.Links);
        Assert.Equal(ExplorerMenuState.Off, ExplorerMenu.GetState(reg, Exe));
        ExplorerMenu.Unregister(reg, lnk);   // idempotent
    }

    [Fact]
    public void Portable_service_toggles_and_repairs_a_moved_folder_only()
    {
        var reg = new FakeRegistry();
        var lnk = new FakeShortcuts();
        var oldExe = @"C:\Downloads\NitroDrop\NitroDrop.exe";
        ExplorerMenu.Register(reg, lnk, oldExe);

        // the old copy still exists → another copy's entry, leave it
        var svc = new ExplorerMenuService(reg, lnk, Exe, packaged: false, fileExists: p => p == oldExe);
        Assert.False(svc.IsEnabled);
        Assert.False(svc.StartupCheck());
        Assert.Equal(ExplorerMenuState.OtherExe, ExplorerMenu.GetState(reg, Exe));

        // the old copy is gone (folder moved) → re-point here
        svc = new ExplorerMenuService(reg, lnk, Exe, packaged: false, fileExists: _ => false);
        Assert.True(svc.StartupCheck());
        Assert.True(svc.IsEnabled);
        Assert.False(svc.StartupCheck());

        svc.SetEnabled(false);
        Assert.False(svc.IsEnabled);
        Assert.Empty(lnk.Links);
        Assert.False(svc.StartupCheck());   // off stays off
        svc.SetEnabled(true);
        Assert.True(svc.IsEnabled);
    }

    [Fact]
    public void Verb_written_in_another_language_follows_the_ui_language()
    {
        var reg = new FakeRegistry();
        var lnk = new FakeShortcuts();
        var svc = new ExplorerMenuService(reg, lnk, Exe, packaged: false, fileExists: _ => true);
        Assert.False(svc.RefreshLanguage());          // not registered → nothing to rewrite
        svc.SetEnabled(true);

        // as if registered while the UI was English
        foreach (var key in ExplorerMenu.ShellKeys) reg.SetString(key, "MUIVerb", "Send with NitroDrop");
        Assert.True(svc.StartupCheck());
        Assert.All(ExplorerMenu.ShellKeys, k => Assert.Equal(UiStrings.ExplorerMenuVerb, reg.GetString(k, "MUIVerb")));
        Assert.False(svc.StartupCheck());             // already current

        Assert.True(svc.RefreshLanguage());           // language switch: rewritten in place, still enabled
        Assert.True(svc.IsEnabled);
        Assert.Single(lnk.Links);

        var packaged = new ExplorerMenuService(new FakeRegistry(), new FakeShortcuts(), Exe, packaged: true);
        Assert.False(packaged.RefreshLanguage());
    }

    [Fact]
    public void Packaged_service_is_always_on_and_removes_portable_leftovers()
    {
        var reg = new FakeRegistry();
        var lnk = new FakeShortcuts();
        var svc = new ExplorerMenuService(reg, lnk, @"C:\Program Files\WindowsApps\N\NitroDrop.exe", packaged: true);
        Assert.True(svc.ManagedByWindows);
        Assert.True(svc.IsEnabled);
        svc.SetEnabled(false);
        svc.SetEnabled(true);
        Assert.Equal(0, reg.Writes);
        Assert.False(svc.StartupCheck());

        ExplorerMenu.Register(reg, lnk, Exe);
        Assert.True(svc.StartupCheck());
        Assert.Empty(reg.Keys);
        Assert.Empty(lnk.Links);
    }

    // ---------------- batching ----------------

    private sealed class FakeTime : TimeProvider
    {
        private long _ticks;
        private readonly List<FakeTimer> _timers = new();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new FakeTimer(this, callback, state);
            t.Change(dueTime, period);
            _timers.Add(t);
            return t;
        }

        public void Advance(TimeSpan by)
        {
            var end = _ticks + by.Ticks;
            while (true)
            {
                var next = _timers.Where(t => t.Due is not null && t.Due <= end).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null) break;
                _ticks = next.Due!.Value;
                next.Due = null;
                next.Fire();
            }
            _ticks = end;
        }

        private sealed class FakeTimer(FakeTime owner, TimerCallback cb, object? state) : ITimer
        {
            public long? Due;
            public void Fire() => cb(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
                return true;
            }
            public void Dispose() => Due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static (SendBatcher B, FakeTime T, List<(string[] Files, bool Continues)> Out) Batcher()
    {
        var t = new FakeTime();
        var b = new SendBatcher(t);
        var flushed = new List<(string[], bool)>();
        b.Flushed += (f, c) => flushed.Add((f.ToArray(), c));
        return (b, t, flushed);
    }

    [Fact]
    public void Rapid_sends_become_one_batch()
    {
        var (b, t, flushed) = Batcher();
        b.Add(new[] { @"C:\1" });
        t.Advance(TimeSpan.FromMilliseconds(100));
        b.Add(new[] { @"C:\2", @"c:\1" });
        t.Advance(TimeSpan.FromMilliseconds(250));
        b.Add(new[] { @"C:\3" });
        t.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Empty(flushed);   // each add restarts the 300 ms quiet period

        t.Advance(TimeSpan.FromMilliseconds(1));
        var (files, continues) = Assert.Single(flushed);
        Assert.Equal(new[] { @"C:\1", @"C:\2", @"C:\3" }, files);
        Assert.False(continues);

        t.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(flushed);
    }

    [Fact]
    public void A_batch_soon_after_the_previous_one_continues_it()
    {
        var (b, t, flushed) = Batcher();
        b.Add(new[] { @"C:\1" });
        t.Advance(TimeSpan.FromMilliseconds(300));
        t.Advance(TimeSpan.FromMilliseconds(900));    // slow process start on a big selection
        b.Add(new[] { @"C:\2" });
        t.Advance(TimeSpan.FromMilliseconds(300));
        t.Advance(TimeSpan.FromSeconds(3));           // a new right-click later
        b.Add(new[] { @"C:\1" });
        t.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(3, flushed.Count);
        Assert.Equal(new[] { @"C:\2" }, flushed[1].Files);
        Assert.True(flushed[1].Continues);
        Assert.Equal(new[] { @"C:\1" }, flushed[2].Files);
        Assert.False(flushed[2].Continues);
    }

    [Fact]
    public void Empty_send_still_flushes_and_dispose_stops_everything()
    {
        var (b, t, flushed) = Batcher();
        b.Add(Array.Empty<string>());
        t.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Empty(Assert.Single(flushed).Files);

        b.Add(new[] { @"C:\x" });
        b.Flush();                                    // explicit flush delivers immediately, once
        t.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, flushed.Count);

        b.Add(new[] { @"C:\y" });
        b.Dispose();
        t.Advance(TimeSpan.FromSeconds(1));
        b.Add(new[] { @"C:\z" });
        b.Flush();
        Assert.Equal(2, flushed.Count);
    }

    [Fact]
    public void Real_timer_flushes_after_the_quiet_period()
    {
        using var b = new SendBatcher(quiet: TimeSpan.FromMilliseconds(50));
        using var done = new ManualResetEventSlim();
        IReadOnlyList<string>? got = null;
        b.Flushed += (f, _) => { got = f; done.Set(); };
        b.Add(new[] { "a" });
        b.Add(new[] { "b" });
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "a", "b" }, got);
    }
}
