namespace Nitro.Continuity.Ancs;

/// <summary>A phone notification as the UI sees it (event + fetched attributes).</summary>
public sealed class PhoneNotification
{
    public uint Uid { get; init; }
    public AncsCategoryId Category { get; set; }
    public AncsEventFlags Flags { get; set; }
    public string? AppIdentifier { get; set; }
    public string? AppDisplayName { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Message { get; set; }
    public DateTime? Date { get; set; }
    public string? PositiveActionLabel { get; set; }
    public string? NegativeActionLabel { get; set; }
    public bool AttributesLoaded { get; set; }

    /// <summary>
    /// Who it is from: for messaging apps (MobileSMS, WhatsApp, Instagram, Telegram…) and IncomingCall iOS puts the
    /// sender / caller in Title. Use with <see cref="AppIdentifier"/> (bundle id) to route "continue on PC".
    /// </summary>
    public string? Sender => string.IsNullOrEmpty(Title) ? null : Title;

    public bool HasPositiveAction => Flags.HasFlag(AncsEventFlags.PositiveAction);
    public bool HasNegativeAction => Flags.HasFlag(AncsEventFlags.NegativeAction);
    public bool IsIncomingCall => Category == AncsCategoryId.IncomingCall;
    public bool IsPreExisting => Flags.HasFlag(AncsEventFlags.PreExisting);
}

/// <summary>
/// Transport-agnostic ANCS state machine. The GATT layer feeds Notification Source / Data Source bytes in and sends the
/// Control Point writes this class asks for via <see cref="WriteControlPoint"/>. No WinRT types here.
/// </summary>
public sealed class AncsNotificationCenter
{
    private readonly Dictionary<uint, PhoneNotification> _items = new();
    private readonly Dictionary<string, string> _appNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _appNameRequested = new(StringComparer.Ordinal);
    private readonly AncsDataSourceAssembler _assembler = new();
    private readonly IReadOnlyCollection<AncsAttributeRequest> _attrs;

    /// <summary>Raised with the exact bytes to write (with response) to the Control Point.</summary>
    public event Action<byte[]>? WriteControlPoint;
    /// <summary>New notification with attributes loaded (pre-existing ones included; check <see cref="PhoneNotification.IsPreExisting"/>).</summary>
    public event Action<PhoneNotification>? Added;
    public event Action<PhoneNotification>? Modified;
    public event Action<PhoneNotification>? Removed;

    public AncsNotificationCenter(IReadOnlyCollection<AncsAttributeRequest>? attributes = null) =>
        _attrs = attributes ?? AncsAttributes.Default;

    public IReadOnlyCollection<PhoneNotification> Items => _items.Values;
    public PhoneNotification? Get(uint uid) => _items.GetValueOrDefault(uid);

    public void OnNotificationSource(ReadOnlySpan<byte> data)
    {
        var ev = AncsNotificationEvent.Parse(data);
        switch (ev.EventId)
        {
            case AncsEventId.NotificationAdded:
            case AncsEventId.NotificationModified:
                bool isNew = !_items.TryGetValue(ev.NotificationUid, out var n);
                n ??= new PhoneNotification { Uid = ev.NotificationUid };
                n.Category = ev.Category; n.Flags = ev.Flags; n.AttributesLoaded = false;
                _items[ev.NotificationUid] = n;
                // a Modified event for a UID we never saw (e.g. after reconnect) is reported as Added
                _modifiedPending[ev.NotificationUid] = !isNew;
                _assembler.ExpectNotificationAttributes(ev.NotificationUid, _attrs);
                WriteControlPoint?.Invoke(AncsCommands.GetNotificationAttributes(ev.NotificationUid, _attrs));
                break;
            case AncsEventId.NotificationRemoved:
                if (_items.Remove(ev.NotificationUid, out var gone)) Removed?.Invoke(gone);
                break;
        }
    }

    private readonly Dictionary<uint, bool> _modifiedPending = new();

    public void OnDataSource(ReadOnlySpan<byte> data)
    {
        foreach (var r in _assembler.Feed(data))
        {
            switch (r)
            {
                case AncsNotificationAttributesResponse na when _items.TryGetValue(na.NotificationUid, out var n):
                    n.AppIdentifier = na[AncsNotificationAttributeId.AppIdentifier] ?? n.AppIdentifier;
                    n.Title = na[AncsNotificationAttributeId.Title];
                    n.Subtitle = na[AncsNotificationAttributeId.Subtitle];
                    n.Message = na[AncsNotificationAttributeId.Message];
                    n.Date = AncsDataSourceAssembler.ParseDate(na[AncsNotificationAttributeId.Date]);
                    n.PositiveActionLabel = na[AncsNotificationAttributeId.PositiveActionLabel];
                    n.NegativeActionLabel = na[AncsNotificationAttributeId.NegativeActionLabel];
                    n.AttributesLoaded = true;
                    if (n.AppIdentifier is { Length: > 0 } app)
                    {
                        if (_appNames.TryGetValue(app, out var name)) n.AppDisplayName = name;
                        else if (_appNameRequested.Add(app))
                        {
                            _assembler.ExpectAppAttributes(app, 1);
                            WriteControlPoint?.Invoke(AncsCommands.GetAppAttributes(app, AncsAppAttributeId.DisplayName));
                        }
                    }
                    bool wasModify = _modifiedPending.Remove(na.NotificationUid, out var m) && m;
                    (wasModify ? Modified : Added)?.Invoke(n);
                    break;
                case AncsAppAttributesResponse aa:
                    if (aa.Attributes.TryGetValue(AncsAppAttributeId.DisplayName, out var dn))
                    {
                        _appNames[aa.AppIdentifier] = dn;
                        foreach (var item in _items.Values.Where(i => i.AppIdentifier == aa.AppIdentifier))
                        {
                            item.AppDisplayName = dn;
                            Modified?.Invoke(item);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>Answer (Positive) / decline (Negative) a call, or the notification's own actions (e.g. "Clear").</summary>
    public byte[] PerformAction(uint uid, AncsActionId action)
    {
        var cmd = AncsCommands.PerformNotificationAction(uid, action);
        WriteControlPoint?.Invoke(cmd);
        return cmd;
    }

    /// <summary>Call on disconnect: iOS re-sends every live notification flagged PreExisting on the next subscription.</summary>
    public void Reset()
    {
        _items.Clear(); _modifiedPending.Clear(); _assembler.Reset(); _appNameRequested.Clear();
    }
}
