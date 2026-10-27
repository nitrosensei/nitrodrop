// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/election.rs: sync master election (SPEC §3.3, AWDL-7..9).

namespace Nitro.Awdl;

/// <summary>What decides between two masters first.</summary>
public enum Ranking
{
    /// <summary>Counter, then metric, as legacy ranked in shared mode.</summary>
    CounterFirst,
    /// <summary>
    /// Metric, then counter, as Apple devices rank: in Owly's capture of a dozen of them a master with metric 510 /
    /// counter 3214 gave way to one with 522 / 393 (AWDL-9).
    /// </summary>
    MetricFirst,
}

/// <summary>
/// A node's election state: its own, or what a peer advertises. A node follows whichever valid peer offers the best
/// master, by metric and counter, then the shorter path, then the higher address, and never a peer that follows it.
/// </summary>
public struct Election : IEquatable<Election>
{
    /// <summary>AWDL-8: the metric a node starts with.</summary>
    public const uint MetricInit = 60;
    /// <summary>AWDL-8: the counter a node starts with.</summary>
    public const uint CounterInit = 0;
    /// <summary>The longest path to a master that's followed (legacy's arbitrary limit).</summary>
    public const uint MaxHeight = 10;

    /// <summary>The node itself.</summary>
    public MacAddr Own;
    /// <summary>The top master it follows.</summary>
    public MacAddr Master;
    /// <summary>The node it syncs to, one hop towards the master.</summary>
    public MacAddr Sync;
    /// <summary>Hops to the master.</summary>
    public uint Height;
    public uint MasterMetric;
    public uint MasterCounter;
    public uint SelfMetric;
    public uint SelfCounter;

    /// <summary>A node that follows nobody but itself, with AWDL-8's metric and counter.</summary>
    public static Election OwnState(MacAddr addr) => new()
    {
        Own = addr, Master = addr, Sync = addr, Height = 0,
        MasterMetric = MetricInit, MasterCounter = CounterInit, SelfMetric = MetricInit, SelfCounter = CounterInit,
    };

    /// <summary>Whether the node is its own sync master.</summary>
    public readonly bool IsMaster => Sync == Own;

    private void ResetSelf()
    {
        Height = 0;
        Master = Own;
        Sync = Own;
        MasterMetric = SelfMetric;
        MasterCounter = SelfCounter;
    }

    private readonly (uint, uint) Rank(Ranking r) =>
        r == Ranking.CounterFirst ? (MasterCounter, MasterMetric) : (MasterMetric, MasterCounter);

    /// <summary>Runs the election over the valid peers' states. Returns whether the master or the sync node changed.</summary>
    public bool Run(IEnumerable<Election> peers, Ranking ranking)
    {
        var (oldMaster, oldSync) = (Master, Sync);
        ResetSelf();
        var best = this;
        foreach (var p in peers)
        {
            // The peer's height is whatever it sent: no arithmetic on it before this check.
            if (p.Height >= MaxHeight) continue;
            // No cycles: never follow a peer that follows us.
            if (p.Sync == Own) continue;
            int c = p.Rank(ranking).CompareTo(best.Rank(ranking));
            if (c < 0) continue;
            if (c == 0 && (p.Height > best.Height || (p.Height == best.Height && p.Own <= best.Own))) continue;
            best = p;
        }
        if (best.Own != Own)
        {
            Master = best.Master;
            Sync = best.Own;
            MasterMetric = best.MasterMetric;
            MasterCounter = best.MasterCounter;
            Height = best.Height + 1;
        }
        return Master != oldMaster || Sync != oldSync;
    }

    public readonly bool Equals(Election o) =>
        Own == o.Own && Master == o.Master && Sync == o.Sync && Height == o.Height && MasterMetric == o.MasterMetric
        && MasterCounter == o.MasterCounter && SelfMetric == o.SelfMetric && SelfCounter == o.SelfCounter;

    public override readonly bool Equals(object? obj) => obj is Election e && Equals(e);
    public override readonly int GetHashCode() => HashCode.Combine(Own, Master, Sync, Height, MasterMetric, MasterCounter);
}
