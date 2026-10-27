using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AirDrop.App.Services;

public enum TransferDirection { Received, Sent }

public sealed class HistoryEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public TransferDirection Direction { get; set; }
    public string DeviceName { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    public DeviceKind DeviceKind { get; set; }
    public List<string> Paths { get; set; } = new();
    public List<string> Links { get; set; } = new();
    public long TotalBytes { get; set; }
    public bool Succeeded { get; set; } = true;
    public string? Note { get; set; }
}

/// <summary>Transfer history persisted as JSON (newest first, capped).</summary>
public sealed class HistoryStore
{
    private const int Max = 500;
    private static string FilePath => Path.Combine(AppSettings.DataDir, "history.json");

    public List<HistoryEntry> Entries { get; private set; } = new();

    public static HistoryStore Load()
    {
        var s = new HistoryStore();
        try
        {
            if (File.Exists(FilePath))
                s.Entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception) { }
        return s;
    }

    public void Add(HistoryEntry e)
    {
        Entries.Insert(0, e);
        if (Entries.Count > Max) Entries = Entries.Take(Max).ToList();
        Save();
    }

    public void Clear()
    {
        Entries.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Entries));
        }
        catch (Exception) { }
    }
}
