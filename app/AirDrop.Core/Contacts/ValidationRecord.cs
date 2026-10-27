using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AirDrop.Core.Plist;

namespace AirDrop.Core.Contacts;

/// <summary>
/// Apple ID validation record carried as SenderRecordData (/Discover and /Ask requests) and
/// ReceiverRecordData (/Discover response).
///
/// Structure, from PrivateDrop (seemoo-lab/PrivateDrop-Base, Models/RecordData.swift) and the PrivateDrop
/// paper (Heinrich et al., USENIX Security '21, §2.4/§5): a CMS SignedData (RFC 5652) whose encapsulated
/// content is a property list with keys Version, altDsID, encDsID, SuggestValidDuration, ValidAsOf,
/// ValidatedEmailHashes, ValidatedPhoneHashes. The hashes are SHA-256 of the identifiers, hex strings
/// (PrivateDrop compares them to lowercase base16). Signed by "Apple Application Integration 2 CA".
///
/// We only parse it for display (contact avatars). <see cref="SignatureValid"/> says whether the CMS
/// signature verifies against the certificate embedded in the blob; the chain to Apple's root is NOT
/// checked (<see cref="ChainVerified"/> is always false) because we do not pin Apple's root CA yet, so
/// a forged record would still parse. Never use it for access control.
/// </summary>
public sealed record ValidationRecord
{
    public long? Version { get; init; }
    public string? AltDsId { get; init; }
    public string? EncDsId { get; init; }
    public DateTime? ValidAsOf { get; init; }
    public long? SuggestValidDuration { get; init; }
    public IReadOnlyList<string> EmailHashes { get; init; } = [];
    public IReadOnlyList<string> PhoneHashes { get; init; } = [];
    /// <summary>The CMS signature verified against the embedded signer certificate (not against Apple's root).</summary>
    public bool SignatureValid { get; init; }
    /// <summary>Always false: Apple's root/intermediate are not pinned yet.</summary>
    public bool ChainVerified => false;
    public string? SignerSubject { get; init; }

    /// <summary>All hashes, lowercase hex, de-duplicated.</summary>
    public IReadOnlyList<string> AllHashes => EmailHashes.Concat(PhoneHashes).Distinct().ToList();

    /// <summary>Parses a CMS-wrapped record, or a bare plist (bplist00 or XML). Returns null if unparseable.</summary>
    public static ValidationRecord? TryParse(byte[]? blob)
    {
        if (blob == null || blob.Length == 0) return null;
        try
        {
            if (BinaryPlist.IsBinaryPlist(blob) || XmlPlist.LooksLikeXml(blob))
                return FromPlist(ReadPlist(blob), signatureValid: false, signer: null);
            var cms = SignedDataReader.Read(blob);
            return FromPlist(ReadPlist(cms.Content), cms.SignatureValid, cms.Signer?.Subject);
        }
        catch (Exception e) when (e is AsnContentException or InvalidDataException or CryptographicException or FormatException
                                      or ArgumentException or InvalidOperationException or System.Xml.XmlException or OverflowException)
        {
            return null;
        }
    }

    private static Dictionary<string, object> ReadPlist(byte[] data) =>
        BinaryPlist.IsBinaryPlist(data) ? BinaryPlist.ReadDictionary(data) : XmlPlist.ReadDictionary(data);

    public static ValidationRecord FromPlist(IReadOnlyDictionary<string, object> d, bool signatureValid, string? signer) => new()
    {
        Version = d.GetValueOrDefault("Version") as long?,
        AltDsId = d.GetValueOrDefault("altDsID") as string,
        EncDsId = d.GetValueOrDefault("encDsID") as string,
        ValidAsOf = d.GetValueOrDefault("ValidAsOf") as DateTime?,
        SuggestValidDuration = d.GetValueOrDefault("SuggestValidDuration") as long?,
        EmailHashes = Hashes(d.GetValueOrDefault("ValidatedEmailHashes")),
        PhoneHashes = Hashes(d.GetValueOrDefault("ValidatedPhoneHashes")),
        SignatureValid = signatureValid,
        SignerSubject = signer,
    };

    private static IReadOnlyList<string> Hashes(object? v)
    {
        if (v is not List<object> list) return [];
        var result = new List<string>();
        foreach (var item in list)
        {
            var hex = item switch
            {
                string s => s.Trim().ToLowerInvariant(),
                byte[] b => Convert.ToHexStringLower(b), // tolerate raw digests
                _ => null,
            };
            if (!string.IsNullOrEmpty(hex) && hex.All(Uri.IsHexDigit) && !result.Contains(hex)) result.Add(hex);
        }
        return result;
    }
}

/// <summary>
/// Minimal CMS SignedData reader on System.Formats.Asn1 (System.Security.Cryptography.Pkcs is not part of the
/// base .NET shared framework). Supports BER (indefinite lengths, constructed OCTET STRINGs), RSA PKCS#1 v1.5
/// and ECDSA signers, SHA-1/256/384/512, with or without signed attributes.
/// </summary>
public static class SignedDataReader
{
    private const string OidSignedData = "1.2.840.113549.1.7.2";
    private const string OidMessageDigest = "1.2.840.113549.1.9.4";

    public sealed record Result(byte[] Content, IReadOnlyList<X509Certificate2> Certificates, X509Certificate2? Signer, bool SignatureValid);

    public static Result Read(byte[] blob)
    {
        var outer = new AsnReader(blob, AsnEncodingRules.BER);
        var contentInfo = outer.ReadSequence();
        if (contentInfo.ReadObjectIdentifier() != OidSignedData) throw new InvalidDataException("Not a CMS SignedData");
        var explicit0 = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        var sd = explicit0.ReadSequence();
        sd.ReadInteger(); // version
        sd.ReadSetOf();   // digestAlgorithms

        var encap = sd.ReadSequence();
        encap.ReadObjectIdentifier(); // eContentType (id-data)
        byte[] content = [];
        if (encap.HasData)
        {
            var e0 = encap.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            content = e0.ReadOctetString();
        }

        var certs = new List<X509Certificate2>();
        var tag0 = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        var tag1 = new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true);
        if (sd.HasData && sd.PeekTag().HasSameClassAndValue(tag0))
        {
            var certSet = sd.ReadSetOf(tag0);
            while (certSet.HasData)
            {
                var encoded = certSet.ReadEncodedValue();
                if (encoded.Span[0] != 0x30) continue; // skip non-X.509 choices
                try { certs.Add(X509CertificateLoader.LoadCertificate(encoded.Span)); }
                catch (CryptographicException) { /* other CertificateChoices */ }
            }
        }
        if (sd.HasData && sd.PeekTag().HasSameClassAndValue(tag1)) sd.ReadEncodedValue(); // crls

        X509Certificate2? signer = null;
        bool valid = false;
        var signerInfos = sd.ReadSetOf();
        if (signerInfos.HasData)
        {
            var si = signerInfos.ReadSequence();
            si.ReadInteger();
            var sid = si.ReadEncodedValue();
            var digestAlg = ReadAlgorithm(si);
            ReadOnlyMemory<byte>? signedAttrs = null;
            byte[]? messageDigest = null;
            if (si.PeekTag().HasSameClassAndValue(tag0))
            {
                signedAttrs = si.ReadEncodedValue();
                messageDigest = FindMessageDigest(signedAttrs.Value);
            }
            var sigAlg = ReadAlgorithm(si);
            var signature = si.ReadOctetString();

            signer = FindSigner(certs, sid);
            var hashName = HashFromOid(digestAlg) ?? HashFromSignatureOid(sigAlg);
            if (signer != null && hashName is { } hn)
            {
                byte[] signedBytes;
                if (signedAttrs is { } attrs)
                {
                    // RFC 5652 §5.4: the signature covers the DER SET OF attributes (tag 0x31, not [0]).
                    signedBytes = attrs.ToArray();
                    signedBytes[0] = 0x31;
                    var digest = Hash(hn, content);
                    valid = messageDigest != null && CryptographicOperations.FixedTimeEquals(digest, messageDigest) &&
                            Verify(signer, sigAlg, hn, signedBytes, signature);
                }
                else valid = Verify(signer, sigAlg, hn, content, signature);
            }
        }
        return new Result(content, certs, signer, valid);
    }

    private static string ReadAlgorithm(AsnReader r)
    {
        var seq = r.ReadSequence();
        var oid = seq.ReadObjectIdentifier();
        return oid;
    }

    private static byte[]? FindMessageDigest(ReadOnlyMemory<byte> attrs)
    {
        var r = new AsnReader(attrs, AsnEncodingRules.BER);
        var set = r.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
        while (set.HasData)
        {
            var attr = set.ReadSequence();
            var oid = attr.ReadObjectIdentifier();
            var values = attr.ReadSetOf();
            if (oid == OidMessageDigest && values.HasData) return values.ReadOctetString();
        }
        return null;
    }

    private static X509Certificate2? FindSigner(List<X509Certificate2> certs, ReadOnlyMemory<byte> sid)
    {
        try
        {
            var r = new AsnReader(sid, AsnEncodingRules.BER);
            var tag = r.PeekTag();
            if (tag.HasSameClassAndValue(Asn1Tag.Sequence))
            {
                // IssuerAndSerialNumber
                var ias = r.ReadSequence();
                var issuer = ias.ReadEncodedValue().ToArray();
                var serial = ias.ReadIntegerBytes().ToArray();
                var match = certs.FirstOrDefault(c =>
                    c.IssuerName.RawData.AsSpan().SequenceEqual(issuer) &&
                    TrimLeadingZeros(c.SerialNumberBytes.ToArray()).SequenceEqual(TrimLeadingZeros(serial)));
                if (match != null) return match;
            }
            else if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 0)
            {
                var ski = r.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
                var hex = Convert.ToHexString(ski);
                var match = certs.FirstOrDefault(c =>
                    c.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Any(x => string.Equals(x.SubjectKeyIdentifier, hex, StringComparison.OrdinalIgnoreCase)));
                if (match != null) return match;
            }
        }
        catch (AsnContentException) { }
        return certs.Count == 1 ? certs[0] : null;
    }

    private static byte[] TrimLeadingZeros(byte[] b)
    {
        int i = 0;
        while (i < b.Length - 1 && b[i] == 0) i++;
        return b[i..];
    }

    private static HashAlgorithmName? HashFromOid(string oid) => oid switch
    {
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        _ => null,
    };

    private static HashAlgorithmName? HashFromSignatureOid(string oid) => oid switch
    {
        "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1" => HashAlgorithmName.SHA1,
        "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
        "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
        "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
        _ => null,
    };

    private static byte[] Hash(HashAlgorithmName name, byte[] data) => name.Name switch
    {
        "SHA1" => SHA1.HashData(data),
        "SHA384" => SHA384.HashData(data),
        "SHA512" => SHA512.HashData(data),
        _ => SHA256.HashData(data),
    };

    private static bool Verify(X509Certificate2 cert, string sigAlg, HashAlgorithmName hash, byte[] data, byte[] signature)
    {
        if (sigAlg == "1.2.840.113549.1.1.10") return false; // RSASSA-PSS: parameters not parsed, unsupported
        using (var rsa = cert.GetRSAPublicKey())
            if (rsa != null) return rsa.VerifyData(data, signature, hash, RSASignaturePadding.Pkcs1);
        using (var ec = cert.GetECDsaPublicKey())
            if (ec != null) return ec.VerifyData(data, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
        return false;
    }
}
