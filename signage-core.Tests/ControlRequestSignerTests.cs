using System.Security.Cryptography;
using System.Text;
using PiSignage.Signage;
using Xunit;

public class ControlRequestSignerTests
{
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

    [Fact]
    public void Signer_uses_the_exact_relative_path_query_and_uppercase_method()
    {
        var request = new HttpRequestMessage(
            new HttpMethod("delete"),
            "/api/media/signed.jpg?tag=a%2Fb&n=1");
        var secret = Convert.FromHexString(new string('2', 64));
        var entityHash = new string('0', 64);
        var canonical = string.Join(
            "\n",
            "controller-2",
            "9",
            "DELETE",
            "/api/media/signed.jpg?tag=a%2Fb&n=1",
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

    static string Header(HttpRequestMessage request, string name) =>
        request.Headers.GetValues(name).Single();
}
