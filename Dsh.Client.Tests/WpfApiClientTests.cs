using System.Net;
using System.Text;
using Dsh.Client;
using Dsh.Contract.Rpc;

namespace Dsh.Client.Tests;

/// <summary>
/// Unary-call tests against a stubbed <see cref="HttpMessageHandler"/> — the injection seam
/// Dsh.Client has always exposed but which was never exercised. Covers the request envelope
/// shape, ok:true/false discrimination, HTTP-level failures, and the respond path. No sockets.
/// </summary>
public class WpfApiClientTests
{
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest;
        public string? LastBody;

        public StubHttpHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _respond();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ServerResponse.RpcId is required on the contract, so every stubbed body echoes one.
    private const string OkPreamble = "{\"type\":\"server-response\",\"rpcId\":\"stub\",";

    [Fact]
    public async Task Call_posts_client_request_envelope_and_returns_value()
    {
        var stub = new StubHttpHandler(() => Json(HttpStatusCode.OK,
            OkPreamble + "\"result\":{\"ok\":true,\"value\":\"hello\"}}"));
        await using var client = new WpfApiClient("http://localhost:9", handler: stub);

        string value = await client.Call<string>("session.list", new { q = "x" });
        Assert.Equal("hello", value);
        Assert.Equal("http://localhost:9/api/session.list", stub.LastRequest!.RequestUri!.ToString());
        Assert.NotNull(stub.LastBody);
        Assert.Contains("\"type\":\"client-request\"", stub.LastBody);
        Assert.Contains("\"method\":\"session.list\"", stub.LastBody);
        Assert.Contains("\"payload\":{\"q\":\"x\"}", stub.LastBody);
        Assert.Contains("\"rpcId\":\"", stub.LastBody); // minted GUID rpcId
    }

    [Fact]
    public async Task Call_serializes_null_payload_as_empty_object()
    {
        // Host schemas are z.object: a literal null payload fails Zod parsing ("invalid
        // payload"); the client substitutes the empty object literal.
        var stub = new StubHttpHandler(() => Json(HttpStatusCode.OK,
            OkPreamble + "\"result\":{\"ok\":true,\"value\":\"v\"}}"));
        await using var client = new WpfApiClient("http://localhost:9", handler: stub);

        await client.Call<string>("host.describe", null);

        Assert.Contains("\"payload\":{}", stub.LastBody);
    }

    [Fact]
    public async Task Call_throws_RpcException_with_business_error_on_ok_false()
    {
        var stub = new StubHttpHandler(() => Json(HttpStatusCode.OK,
            OkPreamble + "\"result\":{\"ok\":false,\"error\":{\"code\":\"bad-request\",\"message\":\"nope\"}}}"));
        await using var client = new WpfApiClient("http://localhost:9", handler: stub);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.Call<string>("session.list", null));

        Assert.Equal(RpcErrorCode.BadRequest, ex.Error.Code);
        Assert.Equal("nope", ex.Error.Message);
    }

    [Fact]
    public async Task Call_surfaces_http_failure_as_HttpRequestException()
    {
        var stub = new StubHttpHandler(() => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        await using var client = new WpfApiClient("http://localhost:9", handler: stub);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.Call<string>("session.list", null));
    }

    [Fact]
    public async Task Respond_posts_client_response_and_returns_receipt()
    {
        var stub = new StubHttpHandler(() => Json(HttpStatusCode.OK, "{\"accepted\":true}"));
        await using var client = new WpfApiClient("http://localhost:9", handler: stub);

        var receipt = await client.Respond(RpcId.Of("r1"), new { ok = true });

        Assert.True(receipt.Accepted);
        Assert.Equal("http://localhost:9/api/respond", stub.LastRequest!.RequestUri!.ToString());
        Assert.NotNull(stub.LastBody);
        Assert.Contains("\"type\":\"client-response\"", stub.LastBody);
        Assert.Contains("\"rpcId\":\"r1\"", stub.LastBody);
    }
}
