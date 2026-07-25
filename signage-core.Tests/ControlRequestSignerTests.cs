using System.Security.Cryptography;
using System.Text;
using PiSignage.Signage;
using Xunit;

public class ControlRequestSignerTests
{
    static readonly string[] AuthHeaderNames =
    {
        "X-PiSignage-Controller",
        "X-PiSignage-Counter",
        "X-PiSignage-Entity-SHA256",
        "X-PiSignage-Signature",
    };

    static string Sha256Hex(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void Signer_matches_python_known_vector()
    {
        var entityHash = Sha256Hex("{\"name\":\"Front\"}");
        var request = new HttpRequestMessage(HttpMethod.Post, "http://pi/api/name")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"name\":\"Front\"}"))
        };

        ControlRequestSigner.Sign(
            request,
            "store",
            Convert.FromHexString(new string('1', 64)),
            7,
            entityHash);

        Assert.Equal("store", Header(request, "X-PiSignage-Controller"));
        Assert.Equal("7", Header(request, "X-PiSignage-Counter"));
        Assert.Equal(entityHash, Header(request, "X-PiSignage-Entity-SHA256"));
        Assert.Equal(
            "5a2a17c6dacd1fbf9584c45e4b8348ee875d6ce4e9e15aa01d60942eb2e04ef5",
            Header(request, "X-PiSignage-Signature"));
    }

    [Theory]
    [InlineData(
        "http://pi/base/api/media/a%2Fb?x=%7E&y=a%20b",
        "/base/api/media/a%2Fb?x=~&y=a%20b")]
    [InlineData(
        "http://pi/base path/api?name=front tv",
        "/base%20path/api?name=front%20tv")]
    [InlineData("http://pi?x=1", "/?x=1")]
    [InlineData("http://pi/api/next?x=1#fragment", "/api/next?x=1")]
    public void Signer_uses_the_absolute_uris_transmitted_path_and_query(
        string uri,
        string transmittedPathAndQuery)
    {
        var request = new HttpRequestMessage(
            new HttpMethod("delete"),
            uri);
        var secret = Convert.FromHexString(new string('2', 64));
        var entityHash = new string('0', 64);
        var canonical = string.Join(
            "\n",
            "controller-2",
            "9",
            "DELETE",
            transmittedPathAndQuery,
            entityHash);
        var expected = Convert.ToHexString(
            HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();

        ControlRequestSigner.Sign(
            request,
            "controller-2",
            secret,
            9,
            entityHash);

        Assert.Equal(expected, Header(request, "X-PiSignage-Signature"));
    }

    [Fact]
    public void Signer_rejects_relative_request_uri_before_adding_headers()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/next");

        Assert.Throws<ArgumentException>(() => ControlRequestSigner.Sign(
            request,
            "controller",
            new byte[32],
            1,
            new string('0', 64)));
        AssertAuthHeadersAbsent(request);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("bad\r\nvalue")]
    [InlineData("bad\u0001value")]
    public void Signer_rejects_invalid_controller_ids_before_adding_headers(
        string controllerId)
    {
        var request = ValidRequest();

        Assert.Throws<ArgumentException>(() => ControlRequestSigner.Sign(
            request,
            controllerId,
            new byte[32],
            1,
            new string('0', 64)));
        AssertAuthHeadersAbsent(request);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Signer_rejects_non_positive_counters_before_adding_headers(long counter)
    {
        var request = ValidRequest();

        Assert.Throws<ArgumentOutOfRangeException>(() => ControlRequestSigner.Sign(
            request,
            "controller",
            new byte[32],
            counter,
            new string('0', 64)));
        AssertAuthHeadersAbsent(request);
    }

    [Theory]
    [InlineData("")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("00000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Signer_rejects_invalid_entity_hashes_before_adding_headers(
        string entityHash)
    {
        var request = ValidRequest();

        Assert.Throws<ArgumentException>(() => ControlRequestSigner.Sign(
            request,
            "controller",
            new byte[32],
            1,
            entityHash));
        AssertAuthHeadersAbsent(request);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Signer_requires_an_exact_32_byte_secret_before_adding_headers(
        int secretLength)
    {
        var request = ValidRequest();

        Assert.Throws<ArgumentException>(() => ControlRequestSigner.Sign(
            request,
            "controller",
            new byte[secretLength],
            1,
            new string('0', 64)));
        AssertAuthHeadersAbsent(request);
    }

    [Fact]
    public void Signer_accepts_uppercase_hex_entity_hash()
    {
        var request = ValidRequest();
        var entityHash = new string('A', 64);

        ControlRequestSigner.Sign(request, "controller", new byte[32], 1, entityHash);

        Assert.Equal(entityHash, Header(request, "X-PiSignage-Entity-SHA256"));
    }

    [Fact]
    public void Signing_again_replaces_authentication_headers()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://pi/api/next");
        var secret = Convert.FromHexString(new string('3', 64));
        var entityHash = new string('0', 64);

        ControlRequestSigner.Sign(request, "first", secret, 1, entityHash);
        ControlRequestSigner.Sign(request, "second", secret, 2, entityHash);

        Assert.Equal(new[] { "second" }, request.Headers.GetValues("X-PiSignage-Controller"));
        Assert.Equal(new[] { "2" }, request.Headers.GetValues("X-PiSignage-Counter"));
        Assert.Single(request.Headers.GetValues("X-PiSignage-Signature"));
    }

    [Fact]
    public void Signing_removes_authentication_headers_from_content_headers()
    {
        var request = ValidRequest();
        foreach (var name in AuthHeaderNames)
        {
            request.Headers.TryAddWithoutValidation(name, "request-old");
            request.Content!.Headers.TryAddWithoutValidation(name, "content-old");
        }

        ControlRequestSigner.Sign(
            request,
            "controller",
            new byte[32],
            1,
            new string('0', 64));

        foreach (var name in AuthHeaderNames)
        {
            Assert.Single(request.Headers.GetValues(name));
            Assert.False(request.Content!.Headers.Contains(name));
        }
    }

    static HttpRequestMessage ValidRequest() => new(
        HttpMethod.Post,
        "http://pi/api/next")
    {
        Content = new ByteArrayContent(Array.Empty<byte>()),
    };

    static void AssertAuthHeadersAbsent(HttpRequestMessage request)
    {
        foreach (var name in AuthHeaderNames)
        {
            Assert.False(request.Headers.Contains(name));
            Assert.False(request.Content?.Headers.Contains(name) ?? false);
        }
    }

    static string Header(HttpRequestMessage request, string name) =>
        request.Headers.GetValues(name).Single();
}
