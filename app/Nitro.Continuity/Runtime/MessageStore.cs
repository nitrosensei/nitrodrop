using Nitro.Continuity.Map;

namespace Nitro.Continuity.Runtime;

public enum MessageStatus { Received, Sending, Sent, Failed }

/// <param name="Handle">MAP message handle (or a local id for messages we sent and the phone has not reported yet).</param>
/// <param name="Address">Phone number / e-mail of the other party (sender for incoming, recipient for outgoing).</param>
public sealed record PhoneMessage(string Handle, string Address, string? Name, string Body, DateTimeOffset Time, bool Outgoing,
    bool Read = false, MessageStatus Status = MessageStatus.Received)
{
    public string ConversationKey => PhoneNumbers.Key(Address);
}

public static class PhoneNumbers
{
    /// <summary>
    /// Conversation key: last 10 digits of a phone number ("+90 555 123 45 67", "05551234567" and "905551234567" all map to
    /// "5551234567"), or the lower-cased trimmed text for e-mail / alphanumeric senders.
    /// </summary>
    public static string Key(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";
        var a = address.Trim();
        if (a.Contains('@') || a.Any(char.IsLetter)) return a.ToLowerInvariant();
        var digits = new string(a.Where(char.IsAsciiDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    /// <summary>"+905551234567" → "+90 555 123 45 67" (Turkish grouping for +90 / 0-prefixed 11 digits); others unchanged.</summary>
    public static string Pretty(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return "";
        var d = new string(number.Where(char.IsAsciiDigit).ToArray());
        if (number.TrimStart().StartsWith('+') && d.StartsWith("90") && d.Length == 12)
            return $"+90 {d[2..5]} {d[5..8]} {d[8..10]} {d[10..12]}";
        if (d.Length == 11 && d[0] == '0')
            return $"0{d[1..4]} {d[4..7]} {d[7..9]} {d[9..11]}";
        return number.Trim();
    }
}

/// <summary>
/// In-memory message list fed by MAP: listings, MNS event reports (NewMessage → GetMessage), and our own PushMessage
/// sends. iOS only reports messages while the PC is connected (no history), so this is the session's view.
/// Thread-safe; events are raised outside the lock.
/// </summary>
public sealed class MessageStore
{
    private readonly object _lock = new();
    private readonly List<PhoneMessage> _items = [];
    private int _localSeq;

    public event Action<PhoneMessage>? Added;
    /// <summary>Old handle (may differ from the new one when a local "sending" item got its MAP handle) and the new value.</summary>
    public event Action<string, PhoneMessage>? Changed;
    public event Action<PhoneMessage>? Removed;

    public IReadOnlyList<PhoneMessage> Messages { get { lock (_lock) return _items.ToList(); } }

    public PhoneMessage? Find(string handle) { lock (_lock) return _items.FirstOrDefault(m => m.Handle == handle); }

    /// <summary>Adds or replaces (same handle). Returns false when an identical item is already present.</summary>
    public bool Upsert(PhoneMessage m)
    {
        PhoneMessage? old;
        lock (_lock)
        {
            int i = _items.FindIndex(x => x.Handle == m.Handle);
            old = i >= 0 ? _items[i] : null;
            if (old == m) return false;
            if (i >= 0) _items[i] = m; else _items.Add(m);
        }
        if (old is null) Added?.Invoke(m); else Changed?.Invoke(m.Handle, m);
        return true;
    }

    /// <summary>Local placeholder for a reply being sent (status Sending). Returns its local handle.</summary>
    public PhoneMessage AddOutgoing(string address, string? name, string body, DateTimeOffset? at = null)
    {
        var m = new PhoneMessage($"local-{Interlocked.Increment(ref _localSeq)}", address, name, body, at ?? DateTimeOffset.Now,
            Outgoing: true, Read: true, Status: MessageStatus.Sending);
        Upsert(m);
        return m;
    }

    public void SetStatus(string handle, MessageStatus status, string? newHandle = null)
    {
        PhoneMessage? updated;
        lock (_lock)
        {
            int i = _items.FindIndex(x => x.Handle == handle);
            if (i < 0) return;
            updated = _items[i] with { Status = status, Handle = newHandle ?? handle };
            _items[i] = updated;
        }
        Changed?.Invoke(handle, updated);
    }

    public void MarkRead(string handle, bool read = true)
    {
        PhoneMessage? updated;
        lock (_lock)
        {
            int i = _items.FindIndex(x => x.Handle == handle);
            if (i < 0 || _items[i].Read == read) return;
            updated = _items[i] with { Read = read };
            _items[i] = updated;
        }
        Changed?.Invoke(handle, updated);
    }

    public void Remove(string handle)
    {
        PhoneMessage? gone;
        lock (_lock)
        {
            int i = _items.FindIndex(x => x.Handle == handle);
            if (i < 0) return;
            gone = _items[i];
            _items.RemoveAt(i);
        }
        Removed?.Invoke(gone);
    }

    public void Clear()
    {
        List<PhoneMessage> all;
        lock (_lock) { all = _items.ToList(); _items.Clear(); }
        foreach (var m in all) Removed?.Invoke(m);
    }

    /// <summary>Builds a message from a GetMessage bMessage (+ the listing/event metadata when known).</summary>
    public static PhoneMessage FromBMessage(string handle, BMessage b, DateTimeOffset? time = null, string? senderName = null, bool? outgoing = null)
    {
        bool isOut = outgoing ?? (b.Folder.Contains("sent", StringComparison.OrdinalIgnoreCase) || b.Folder.Contains("outbox", StringComparison.OrdinalIgnoreCase));
        string address = (isOut ? b.RecipientTel : b.OriginatorTel) ?? b.OriginatorTel ?? b.RecipientTel ?? "";
        string? name = senderName ?? (isOut ? b.RecipientName : null);
        return new PhoneMessage(handle, address, string.IsNullOrWhiteSpace(name) ? null : name, b.Body, time ?? DateTimeOffset.Now,
            isOut, Read: isOut || string.Equals(b.Status, "READ", StringComparison.OrdinalIgnoreCase),
            Status: isOut ? MessageStatus.Sent : MessageStatus.Received);
    }

    public static PhoneMessage FromListing(MapMessageListItem item, string? body = null, string folder = "inbox")
    {
        bool isOut = item.Sent == true || folder is "sent" or "outbox";
        string address = (isOut ? item.RecipientAddressing : item.SenderAddressing) ?? "";
        return new PhoneMessage(item.Handle, address, isOut ? null : item.SenderName, body ?? item.Subject ?? "",
            item.DateTime is { } d ? new DateTimeOffset(d) : DateTimeOffset.Now, isOut, item.Read ?? false,
            isOut ? MessageStatus.Sent : MessageStatus.Received);
    }

    /// <summary>
    /// Applies one MNS event. NewMessage fetches the bMessage through <paramref name="fetch"/> (MAS GetMessage) unless the
    /// handle is already known; SendingSuccess/Failure and DeliverySuccess update our outgoing items; MessageDeleted and
    /// ReadStatusChanged update the list. Returns the affected message (null when nothing changed).
    /// </summary>
    public async Task<PhoneMessage?> ApplyEventAsync(MapEvent ev, Func<string, Task<BMessage?>> fetch)
    {
        if (string.IsNullOrEmpty(ev.Handle)) return null;
        switch (ev.Type)
        {
            case "NewMessage":
            {
                if (Find(ev.Handle) is { } known) return known;
                BMessage? b = null;
                try { b = await fetch(ev.Handle).ConfigureAwait(false); }
                catch (Exception) when (ev.Subject is not null) { /* fall back to the event's subject preview */ }
                bool outgoing = ev.Folder?.Contains("sent", StringComparison.OrdinalIgnoreCase) == true
                                || ev.Folder?.Contains("outbox", StringComparison.OrdinalIgnoreCase) == true;
                PhoneMessage m;
                if (b is not null)
                    m = FromBMessage(ev.Handle, b, ev.DateTime is { } t ? new DateTimeOffset(t) : null, ev.SenderName, outgoing);
                else if (ev.Subject is not null)
                    m = new PhoneMessage(ev.Handle, ev.SenderName ?? "", ev.SenderName, ev.Subject,
                        ev.DateTime is { } t2 ? new DateTimeOffset(t2) : DateTimeOffset.Now, outgoing);
                else return null;
                // Our own reply coming back from "sent": attach the handle to the local placeholder instead of duplicating it.
                if (m.Outgoing && AdoptPlaceholder(m) is { } adopted) return adopted;
                Upsert(m);
                return m;
            }
            case "SendingSuccess":
            case "DeliverySuccess":
                SetStatus(ev.Handle, MessageStatus.Sent);
                return Find(ev.Handle);
            case "SendingFailure":
            case "DeliveryFailure":
                SetStatus(ev.Handle, MessageStatus.Failed);
                return Find(ev.Handle);
            case "MessageDeleted":
            {
                var m = Find(ev.Handle);
                Remove(ev.Handle);
                return m;
            }
            case "ReadStatusChanged":
                MarkRead(ev.Handle);
                return Find(ev.Handle);
            default:
                return null;
        }
    }

    private PhoneMessage? AdoptPlaceholder(PhoneMessage sent)
    {
        string? local;
        lock (_lock)
        {
            local = _items.FirstOrDefault(x => x.Handle.StartsWith("local-", StringComparison.Ordinal) && x.Outgoing
                                               && x.ConversationKey == sent.ConversationKey && x.Body == sent.Body)?.Handle;
        }
        if (local is null) return null;
        SetStatus(local, MessageStatus.Sent, sent.Handle);
        return Find(sent.Handle);
    }
}
