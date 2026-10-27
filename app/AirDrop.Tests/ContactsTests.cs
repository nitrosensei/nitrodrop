using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AirDrop.Core.Contacts;
using AirDrop.Core.Plist;

namespace AirDrop.Tests;

public class ContactsTests
{
    private const string Jpeg = "/9j/4AAQSkZJRg=="; // FF D8 FF E0 00 10 'JFIF'

    private const string ICloudCard =
        "BEGIN:VCARD\r\nVERSION:3.0\r\nPRODID:-//Apple Inc.//iPhone OS 17.0//EN\r\nN:Temur;Emre Can;;;\r\n" +
        "FN:Emre Can Te\r\n mur\r\nitem1.TEL;type=pref:+90 555 123 45 67\r\nitem1.X-ABLabel:_$!<Mobile>!$_\r\n" +
        "item2.EMAIL;type=INTERNET;type=pref:Emre@Example.com\r\nPHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQ\r\n SkZJRg==\r\nEND:VCARD\r\n";

    private const string GoogleCards =
        "BEGIN:VCARD\nVERSION:3.0\nFN:Ayşe Öztürk\nN:Öztürk;Ayşe;;;\nEMAIL;TYPE=INTERNET;TYPE=HOME:ayse@gmail.com\n" +
        "TEL;TYPE=CELL:0532 111 22 33\nPHOTO:https://lh3.googleusercontent.com/abc\nEND:VCARD\n" +
        "BEGIN:VCARD\nVERSION:4.0\nFN:Çağrı Ğüneş\nTEL;VALUE=uri;TYPE=cell:tel:+90-533-444-5566\n" +
        "PHOTO:data:image/png;base64,iVBORw0KGgo=\nEND:VCARD\n" +
        "BEGIN:VCARD\nVERSION:2.1\nN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:=C5=9Eule;=C4=B0lker;;;\nTEL;CELL:05551112233\nEND:VCARD\n" +
        "BEGIN:VCARD\nVERSION:3.0\nFN:Sadece İsim\nEND:VCARD\n";

    [Fact]
    public void VCard_ICloudStyle_WithFoldedLinesAndPhoto()
    {
        var c = Assert.Single(VCard.Parse(ICloudCard));
        Assert.Equal("Emre Can Temur", c.DisplayName);
        Assert.Equal(["+90 555 123 45 67"], c.Phones);
        Assert.Equal(["Emre@Example.com"], c.Emails);
        Assert.Equal(Convert.FromBase64String(Jpeg), c.Photo);
    }

    [Fact]
    public void VCard_GoogleStyle_V4DataUri_V21QuotedPrintable_TurkishNames()
    {
        var cards = VCard.Parse(GoogleCards);
        Assert.Equal(4, cards.Count);
        Assert.Equal("Ayşe Öztürk", cards[0].DisplayName);
        Assert.Equal(["ayse@gmail.com"], cards[0].Emails);
        Assert.Null(cards[0].Photo); // remote URL ignored
        Assert.Equal("Çağrı Ğüneş", cards[1].DisplayName);
        Assert.Equal(["+90-533-444-5566"], cards[1].Phones);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, cards[1].Photo);
        Assert.Equal("İlker Şule", cards[2].DisplayName);
        Assert.Equal("Sadece İsim", cards[3].DisplayName);
    }

    [Fact]
    public void PhoneForms_ExpandLocalTurkishNumbers()
    {
        var tr = PhoneRegion.Turkey;
        Assert.Equal("905551234567", ContactHashing.PhoneForms("0555 123 45 67", tr).First());
        Assert.Equal("905551234567", ContactHashing.PhoneForms("+90 (555) 123-45-67", tr).First());
        Assert.Equal("905551234567", ContactHashing.PhoneForms("0090 555 123 45 67", tr).First());
        Assert.Equal("905551234567", ContactHashing.PhoneForms("555 123 45 67", tr).First());
        Assert.Contains("905551234567", ContactHashing.PhoneForms("905551234567", tr));
        Assert.Contains("+905551234567", ContactHashing.PhoneForms("0555 123 45 67", tr));
        Assert.Equal("15551234567", ContactHashing.PhoneForms("(555) 123-4567", PhoneRegion.UnitedStates).First());
        Assert.Equal("emre@example.com", ContactHashing.EmailForms(" Emre@Example.com ").First());
        Assert.Equal(ContactHashing.Sha256Hex("emre@example.com"),
            Convert.ToHexStringLower(SHA256.HashData("emre@example.com"u8.ToArray())));
    }

    private static Dictionary<string, object> RecordPlist(params string[] hashesOf) => new()
    {
        ["Version"] = 2L,
        ["altDsID"] = "000123-08-aaaa",
        ["encDsID"] = "enc",
        ["SuggestValidDuration"] = 2592000L,
        ["ValidAsOf"] = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        ["ValidatedEmailHashes"] = hashesOf.Where(h => h.Contains('@')).Select(h => (object)ContactHashing.Sha256Hex(h).ToUpperInvariant()).ToList(),
        ["ValidatedPhoneHashes"] = hashesOf.Where(h => !h.Contains('@')).Select(h => (object)ContactHashing.Sha256Hex(h)).ToList(),
    };

    [Fact]
    public void Record_BarePlist_HashesLowercased()
    {
        var r = ValidationRecord.TryParse(BinaryPlist.Write(RecordPlist("emre@example.com", "905551234567")));
        Assert.NotNull(r);
        Assert.Equal(2L, r!.Version);
        Assert.Equal("000123-08-aaaa", r.AltDsId);
        Assert.Equal(ContactHashing.Sha256Hex("emre@example.com"), Assert.Single(r.EmailHashes));
        Assert.Equal(ContactHashing.Sha256Hex("905551234567"), Assert.Single(r.PhoneHashes));
        Assert.False(r.SignatureValid);
        Assert.False(r.ChainVerified);
        Assert.Null(ValidationRecord.TryParse([1, 2, 3]));
        Assert.Null(ValidationRecord.TryParse(null));
    }

    [Fact]
    public void Record_XmlPlist()
    {
        var h = ContactHashing.Sha256Hex("905551234567");
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                  $"<plist version=\"1.0\"><dict><key>Version</key><integer>2</integer><key>ValidatedPhoneHashes</key><array><string>{h}</string></array>" +
                  "<key>ValidatedEmailHashes</key><array/><key>ValidAsOf</key><date>2026-10-01T00:00:00Z</date></dict></plist>";
        var r = ValidationRecord.TryParse(System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.NotNull(r);
        Assert.Equal(h, Assert.Single(r!.PhoneHashes));
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), r.ValidAsOf);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Record_CmsSigned_SignatureCheckedAgainstEmbeddedCert(bool ecdsa)
    {
        var content = BinaryPlist.Write(RecordPlist("emre@example.com", "905551234567"));
        var cms = TestCms.Sign(content, ecdsa);
        var r = ValidationRecord.TryParse(cms);
        Assert.NotNull(r);
        Assert.True(r!.SignatureValid);
        Assert.False(r.ChainVerified);
        Assert.Contains("Test Validation Record Signer", r.SignerSubject);
        Assert.Equal(2, r.AllHashes.Count);

        var tampered = (byte[])cms.Clone();
        tampered[^1] ^= 0x01; // last byte of the signature
        var t = ValidationRecord.TryParse(tampered);
        Assert.NotNull(t);
        Assert.False(t!.SignatureValid);
        Assert.Equal(2, t.AllHashes.Count);
    }

    [Fact]
    public void ContactBook_MatchesOnlyImportedContacts_AndPersists()
    {
        using var dir = new TempDir();
        var book = new ContactBook(dir.Path);
        Assert.Equal(1, book.ImportText(ICloudCard));
        Assert.Equal(3, book.ImportText(GoogleCards)); // the name-only card is skipped
        Assert.Equal(4, book.Count);

        // A record as an iPhone would send it: Apple's canonical forms (best guess), lowercase hex.
        var record = ValidationRecord.TryParse(TestCms.Sign(BinaryPlist.Write(RecordPlist("emre@example.com")), false))!;
        var m = book.Match(record.AllHashes);
        Assert.NotNull(m);
        Assert.Equal("Emre Can Temur", m!.DisplayName);
        Assert.Equal("image/jpeg", m.PhotoMimeType);

        Assert.Equal("Ayşe Öztürk", book.Match([ContactHashing.Sha256Hex("905321112233")])!.DisplayName);
        Assert.Equal("Çağrı Ğüneş", book.Match([ContactHashing.Sha256Hex("+905334445566").ToUpperInvariant()])!.DisplayName);
        Assert.Equal("İlker Şule", book.Match([ContactHashing.Sha256Hex("905551112233")])!.DisplayName);
        Assert.Null(book.Match([ContactHashing.Sha256Hex("905559999999"), ContactHashing.Sha256Hex("stranger@example.com")]));
        Assert.Null(book.Match(null));

        // Re-import is idempotent.
        Assert.Equal(1, book.ImportText(ICloudCard));
        Assert.Equal(4, book.Count);

        var reloaded = new ContactBook(dir.Path);
        reloaded.Load();
        Assert.Equal(4, reloaded.Count);
        var again = reloaded.Match(record.AllHashes)!;
        Assert.Equal(Convert.FromBase64String(Jpeg), again.Photo);
        Assert.True(File.Exists(Path.Combine(dir.Path, "index.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "photos"), "*.jpg"));

        reloaded.Clear();
        Assert.Equal(0, reloaded.Count);
        Assert.False(Directory.Exists(dir.Path));
    }

    [Fact]
    public async Task ContactBookResolver_ImportsVcfFile()
    {
        using var dir = new TempDir();
        var vcf = Path.Combine(dir.Path, "kişiler.vcf");
        File.WriteAllText(vcf, ICloudCard + GoogleCards, new System.Text.UTF8Encoding(true));
        var resolver = new AirDrop.App.Services.ContactBookResolver(new ContactBook(Path.Combine(dir.Path, "store")));
        Assert.Equal(4, await resolver.ImportAsync(vcf));
        var m = resolver.Match([ContactHashing.Sha256Hex("emre@example.com")]);
        Assert.Equal("Emre Can Temur", m?.Name);
        Assert.NotNull(m?.Photo);
    }
}

/// <summary>Builds a minimal RFC 5652 SignedData with signed attributes, like Apple's validation records.</summary>
internal static class TestCms
{
    public static byte[] Sign(byte[] content, bool ecdsa)
    {
        using var key = ecdsa ? (AsymmetricAlgorithm)ECDsa.Create(ECCurve.NamedCurves.nistP256) : RSA.Create(2048);
        var name = new X500DistinguishedName("CN=Test Validation Record Signer");
        var req = key is RSA rsa
            ? new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(name, (ECDsa)key, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var attrs = new AsnWriter(AsnEncodingRules.DER);
        using (attrs.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            using (attrs.PushSequence())
            {
                attrs.WriteObjectIdentifier("1.2.840.113549.1.9.3");
                using (attrs.PushSetOf()) attrs.WriteObjectIdentifier("1.2.840.113549.1.7.1");
            }
            using (attrs.PushSequence())
            {
                attrs.WriteObjectIdentifier("1.2.840.113549.1.9.4");
                using (attrs.PushSetOf()) attrs.WriteOctetString(SHA256.HashData(content));
            }
        }
        var attrsEncoded = attrs.Encode();
        var toSign = (byte[])attrsEncoded.Clone();
        toSign[0] = 0x31;
        byte[] signature = key is RSA r
            ? r.SignData(toSign, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : ((ECDsa)key).SignData(toSign, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier("1.2.840.113549.1.7.2");
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (w.PushSequence())
            {
                w.WriteInteger(1);
                using (w.PushSetOf()) using (w.PushSequence()) w.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier("1.2.840.113549.1.7.1");
                    using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) w.WriteOctetString(content);
                }
                using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0))) w.WriteEncodedValue(cert.RawData);
                using (w.PushSetOf())
                using (w.PushSequence())
                {
                    w.WriteInteger(1);
                    using (w.PushSequence())
                    {
                        w.WriteEncodedValue(cert.IssuerName.RawData);
                        w.WriteIntegerUnsigned(cert.SerialNumberBytes.Span);
                    }
                    using (w.PushSequence()) w.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                    w.WriteEncodedValue(attrsEncoded);
                    using (w.PushSequence())
                    {
                        w.WriteObjectIdentifier(key is RSA ? "1.2.840.113549.1.1.1" : "1.2.840.10045.4.3.2");
                        if (key is RSA) w.WriteNull();
                    }
                    w.WriteOctetString(signature);
                }
            }
        }
        return w.Encode();
    }
}
