using System.Text.Json;
using Dsh.Contract.Methods;
using Xunit;

namespace Dsh.Contract.Tests;

/// <summary>
/// Wire-format tests for the messageFeedback RPC contracts (P2-4), mirroring the host
/// types in packages/feedback/message-feedback/src/types.ts. Asserts the STJ binding
/// produces/exactly round-trips the wire strings the host expects.
/// </summary>
public class MessageFeedbackWireTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void PutRequest_serializes_to_host_shape()
    {
        var request = new MessageFeedbackPutRequest
        {
            SessionId = "s1",
            MessageId = "m1",
            Rating = MessageFeedbackRating.Positive,
            IfVersion = 3,
        };
        var json = JsonSerializer.Serialize(request, Options);
        Assert.Equal(
            "{\"sessionId\":\"s1\",\"messageId\":\"m1\",\"rating\":\"positive\",\"ifVersion\":3}",
            json);
    }

    [Fact]
    public void Rating_roundtrips_positive_and_negative()
    {
        foreach (var (rating, wire) in new[]
                 {
                     (MessageFeedbackRating.Positive, "positive"),
                     (MessageFeedbackRating.Negative, "negative"),
                 })
        {
            var json = JsonSerializer.Serialize(rating, Options);
            Assert.Equal($"\"{wire}\"", json);
            var back = JsonSerializer.Deserialize<MessageFeedbackRating>(json, Options);
            Assert.Equal(rating, back);
        }
    }

    [Fact]
    public void ListValue_deserializes_items()
    {
        var json = "{\"items\":[" +
                   "{\"messageId\":\"m1\",\"rating\":\"positive\",\"version\":1," +
                   "\"createdAt\":1786778389567,\"updatedAt\":1786778389567}," +
                   "{\"messageId\":\"m2\",\"rating\":\"negative\",\"note\":\"bad\",\"version\":2," +
                   "\"createdAt\":1786778389567,\"updatedAt\":1786778389567}]}";
        var value = JsonSerializer.Deserialize<MessageFeedbackListValue>(json, Options)!;
        Assert.Equal(2, value.Items.Length);
        Assert.Equal(MessageFeedbackRating.Positive, value.Items[0].Rating);
        Assert.Equal("bad", value.Items[1].Note);
        Assert.Equal(MessageFeedbackRating.Negative, value.Items[1].Rating);
    }

    [Fact]
    public void DeleteRequest_serializes_ifVersion()
    {
        var request = new MessageFeedbackDeleteRequest
        {
            SessionId = "s1",
            MessageId = "m1",
            IfVersion = 2,
        };
        var json = JsonSerializer.Serialize(request, Options);
        Assert.Equal("{\"sessionId\":\"s1\",\"messageId\":\"m1\",\"ifVersion\":2}", json);
    }
}
