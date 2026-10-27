using Nitro.Continuity.Hfp;

namespace AirDrop.Tests.Continuity;

public class HfpTests
{
    const string IosLikeCindTest =
        "+CIND: (\"service\",(0,1)),(\"call\",(0,1)),(\"callsetup\",(0-3)),(\"callheld\",(0-2)),(\"signal\",(0-5)),(\"roam\",(0,1)),(\"battchg\",(0-5))";

    [Fact]
    public void ParseLine_BasicResults()
    {
        Assert.IsType<AtOk>(AtResultParser.ParseLine("OK"));
        Assert.IsType<AtRing>(AtResultParser.ParseLine("RING"));
        Assert.Null(Assert.IsType<AtError>(AtResultParser.ParseLine("ERROR")).CmeCode);
        Assert.Equal(30, Assert.IsType<AtError>(AtResultParser.ParseLine("+CME ERROR: 30")).CmeCode);
        Assert.Equal(new AtCiev(3, 1), AtResultParser.ParseLine("+CIEV: 3,1"));
    }

    [Fact]
    public void ParseLine_Clip_WithName()
    {
        var c = Assert.IsType<AtClip>(AtResultParser.ParseLine("+CLIP: \"+905551112233\",145,,,\"Ayşe Yılmaz\""));
        Assert.Equal("+905551112233", c.Number);
        Assert.Equal(145, c.Type);
        Assert.Equal("Ayşe Yılmaz", c.Name);
        var noName = Assert.IsType<AtClip>(AtResultParser.ParseLine("+CLIP: \"05551112233\",129"));
        Assert.Null(noName.Name);
    }

    [Fact]
    public void ParseLine_Brsf_Cind_Clcc()
    {
        var b = Assert.IsType<AtBrsf>(AtResultParser.ParseLine("+BRSF: 879"));
        Assert.True(b.Features.HasFlag(AgFeatures.InBandRingTone));
        Assert.True(b.Features.HasFlag(AgFeatures.RejectCall));
        var t = Assert.IsType<AtCindTest>(AtResultParser.ParseLine(IosLikeCindTest));
        Assert.Equal(["service", "call", "callsetup", "callheld", "signal", "roam", "battchg"], t.Names);
        var v = Assert.IsType<AtCindValues>(AtResultParser.ParseLine("+CIND: 1,0,0,0,4,0,3"));
        Assert.Equal([1, 0, 0, 0, 4, 0, 3], v.Values);
        var l = Assert.IsType<AtClcc>(AtResultParser.ParseLine("+CLCC: 1,1,4,0,0,\"+905551112233\",145"));
        Assert.Equal(4, l.Status); // incoming
        Assert.Equal("+905551112233", l.Number);
    }

    [Fact]
    public void ParseLine_UnknownAndMalformed()
    {
        Assert.IsType<AtUnsolicited>(AtResultParser.ParseLine("+VGS: 9"));
        Assert.IsType<AtUnknown>(AtResultParser.ParseLine("+CIEV: x"));
        Assert.IsType<AtUnknown>(AtResultParser.ParseLine("garbage"));
    }

    [Fact]
    public void Feed_FragmentedCrLfStream()
    {
        var p = new AtResultParser();
        Assert.Empty(p.Feed("\r\nRI"));
        var r1 = p.Feed("NG\r\n\r\n+CLIP: \"+90555\",145\r");
        Assert.Equal(2, r1.Count);
        Assert.IsType<AtRing>(r1[0]);
        Assert.IsType<AtClip>(r1[1]);
        Assert.IsType<AtOk>(Assert.Single(p.Feed("\n\r\nOK\r\n")));
    }

    [Fact]
    public void Session_SlcSequence_ThenIncomingAnsweredHungUp()
    {
        var s = new HfpSession();
        var states = new List<PhoneCallState>();
        (string? num, string? name) rang = default;
        s.StateChanged += states.Add;
        s.Ringing += (n, nm) => rang = (n, nm);

        Assert.Equal("AT+BRSF=52\r", s.Start()); // CLI(4) | remote volume(16) | enhanced call status(32)
        Assert.Null(s.OnResult(new AtBrsf((AgFeatures)871)));
        Assert.Equal(HfpCommands.CindTest, s.OnResult(new AtOk()));
        s.OnResult(AtResultParser.ParseLine(IosLikeCindTest));
        Assert.Equal(HfpCommands.CindRead, s.OnResult(new AtOk()));
        s.OnResult(new AtCindValues([1, 0, 0, 0, 4, 0, 3]));
        Assert.Equal(HfpCommands.CmerEnable, s.OnResult(new AtOk()));
        Assert.Equal(HfpCommands.ClipEnable, s.OnResult(new AtOk()));
        Assert.False(s.IsSlcEstablished);
        Assert.Null(s.OnResult(new AtOk()));
        Assert.True(s.IsSlcEstablished);
        Assert.Equal(3, s.IndicatorIndex["callsetup"]);

        s.OnResult(new AtCiev(3, 1));                 // callsetup = incoming
        Assert.Equal(PhoneCallState.Incoming, s.State);
        s.OnResult(new AtRing());
        s.OnResult(AtResultParser.ParseLine("+CLIP: \"+905551112233\",145,,,\"Ayşe\""));
        Assert.Equal(("+905551112233", "Ayşe"), rang);

        s.OnResult(new AtCiev(2, 1));                 // call = 1 (answered, ATA)
        s.OnResult(new AtCiev(3, 0));                 // callsetup = 0
        Assert.Equal(PhoneCallState.Active, s.State);
        s.OnResult(new AtCiev(2, 0));                 // AT+CHUP → call = 0
        Assert.Equal(PhoneCallState.Idle, s.State);
        Assert.Null(s.CallerNumber);
        Assert.Equal([PhoneCallState.Incoming, PhoneCallState.Active, PhoneCallState.Idle], states);
    }

    [Fact]
    public void Session_ClipErrorDoesNotBlockSlc()
    {
        var s = new HfpSession();
        s.Start();
        s.OnResult(new AtOk()); s.OnResult(new AtOk()); s.OnResult(new AtOk()); s.OnResult(new AtOk());
        Assert.Null(s.OnResult(new AtError()));
        Assert.True(s.IsSlcEstablished);
    }

    [Fact]
    public void Commands_Format()
    {
        Assert.Equal("ATD+905551112233;\r", HfpCommands.Dial("+905551112233"));
        Assert.Equal("ATA\r", HfpCommands.Answer);
        Assert.Equal("AT+CHUP\r", HfpCommands.HangUp);
        Assert.Equal("AT+VGS=15\r", HfpCommands.SpeakerGain(99));
    }
}
