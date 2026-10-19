using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMessages.Core;
using Xunit;

namespace OpenMessages.Core.Tests;

/// <summary>Shared vector with the Android (OtpCryptoTest) and macOS (OtpCryptoTests) apps.</summary>
public class OtpCryptoTests
{
    private const string Passphrase = "Grüße ABCD-EFGH";
    private static readonly byte[] Salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private const string Uid = "testUid123";
    private const string Plaintext = """{"code":"482913","sender":"+15551234567","body":"Your code is 482913","receivedAt":1760870400000}""";
    private static readonly OtpCrypto.Sealed Vector = new(
        "AQEBAQEBAQEBAQEB",
        "o3huhmbAJCdPX2n+7F4sAro/efe0GH3xTvcmRFe+Mb+ZfFEEzUXIVPuJzv6z098YWX1LfVeh6s35chcAcD+6ypDuYCRMLEDjk5TxZGCWCfuPt2awGfQf3NlkCj7uU0uYUorH9fQdzXanbetv0yDueW8=");

    [Fact]
    public void DeriveKeyMatchesVector() =>
        Assert.Equal("pdKKPF59vu2JWYdNAdDt9J6+uHEZHQyEa4pvrfBbQL8=",
            Convert.ToBase64String(OtpCrypto.DeriveKey(Passphrase, Salt, 1000)));

    [Fact]
    public void DecryptsVectorAndParsesPayload()
    {
        var key = OtpCrypto.DeriveKey(Passphrase, Salt, 1000);
        var plain = OtpCrypto.Decrypt(Vector, key, OtpCrypto.OtpAad(Uid));
        Assert.Equal(Plaintext, Encoding.UTF8.GetString(plain));
        var payload = JsonSerializer.Deserialize<OtpPayload>(plain)!;
        Assert.Equal("482913", payload.Code);
        Assert.Equal(1760870400000, payload.ReceivedAt);
    }

    [Fact]
    public void EncryptMatchesVectorWithSameIv()
    {
        var key = OtpCrypto.DeriveKey(Passphrase, Salt, 1000);
        var iv = Enumerable.Repeat((byte)1, 12).ToArray();
        Assert.Equal(Vector, OtpCrypto.Encrypt(Encoding.UTF8.GetBytes(Plaintext), key, OtpCrypto.OtpAad(Uid), iv));
    }

    [Fact]
    public void VerifyRejectsWrongKeyOrUid()
    {
        var key = OtpCrypto.DeriveKey(Passphrase, Salt, 1000);
        var check = OtpCrypto.Encrypt(Encoding.UTF8.GetBytes(OtpCrypto.CheckPlaintext), key, OtpCrypto.CheckAad(Uid));
        Assert.True(OtpCrypto.Verify(key, check, Uid));
        Assert.False(OtpCrypto.Verify(key, check, "someoneElse"));
        Assert.False(OtpCrypto.Verify(OtpCrypto.DeriveKey("wrong", Salt, 1000), check, Uid));
    }

    [Fact]
    public void NormalizeTrimsLikeThePhone() => Assert.Equal("ABCD-EFGH", OtpCrypto.Normalize("  ABCD-EFGH \n"));
}

public class FirestoreParsingTests
{
    [Fact]
    public void ParsesKeyInfoDocument()
    {
        var doc = JsonNode.Parse("""
        {"name":"projects/p/databases/(default)/documents/users/u","fields":{"otpSync":{"mapValue":{"fields":{
          "v":{"integerValue":"1"},"kdf":{"stringValue":"PBKDF2-HMAC-SHA256"},"iterations":{"integerValue":"600000"},
          "salt":{"stringValue":"AAECAwQFBgcICQoLDA0ODw=="},
          "check":{"mapValue":{"fields":{"iv":{"stringValue":"aXY="},"ct":{"stringValue":"Y3Q="}}}}}}}}}
        """);
        var info = FirestoreClient.ParseKeyInfo(doc)!;
        Assert.Equal(600000, info.Iterations);
        Assert.Equal(16, info.Salt.Length);
        Assert.Equal(new OtpCrypto.Sealed("aXY=", "Y3Q="), info.Check);
    }

    [Fact]
    public void MissingKeyInfoIsNull() => Assert.Null(FirestoreClient.ParseKeyInfo(JsonNode.Parse("""{"fields":{}}""")));

    [Fact]
    public void ParsesRunQueryRowsAndSkipsReadTimeOnlyRows()
    {
        var rows = JsonNode.Parse("""
        [{"document":{"name":"projects/p/databases/(default)/documents/users/u/otps/a","fields":{
           "v":{"integerValue":"1"},"iv":{"stringValue":"aXY="},"ct":{"stringValue":"Y3Q="},
           "createdAt":{"timestampValue":"2026-10-19T10:00:00.123456Z"},
           "expiresAt":{"timestampValue":"2026-10-20T10:00:00Z"}}},"readTime":"2026-10-19T10:00:05Z"},
         {"readTime":"2026-10-19T10:00:05Z"}]
        """);
        var otp = Assert.Single(FirestoreClient.ParseOtps(rows));
        Assert.EndsWith("/otps/a", otp.Name);
        Assert.Equal("2026-10-19T10:00:00.123456Z", otp.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), otp.ExpiresAt);
    }

    [Fact]
    public void ParsesNanosecondTimestamps()
    {
        var parsed = FirestoreClient.ParseTimestamp("2026-10-19T10:00:00.123456789Z")!.Value;
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero).AddTicks(1234567), parsed);
    }
}

public class AppConfigTests
{
    [Fact]
    public void ParsesValidConfig()
    {
        var config = AppConfig.Parse("""{"apiKey":"k","projectId":"p","googleClientId":"id","googleClientSecret":"s"}""");
        Assert.Equal(new AppConfig("k", "p", "id", "s"), config);
    }

    [Fact]
    public void ReportsMissingField()
    {
        var e = Assert.Throws<ConfigException>(() => AppConfig.Parse("""{"apiKey":"k","projectId":"p"}"""));
        Assert.Contains("googleClientId", e.Message);
    }
}
