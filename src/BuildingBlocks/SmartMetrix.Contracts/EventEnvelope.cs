using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartMetrix.Contracts;

public sealed record EventEnvelope<T>(
    Guid EventId,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    T Data);

public static class EventEnvelope
{
    public const int CurrentSchemaVersion = 1;

    public static EventEnvelope<T> Create<T>(T data, string correlationId) =>
        new(Guid.NewGuid(), CurrentSchemaVersion, DateTimeOffset.UtcNow, correlationId, data);
}

public static class EventSubjects
{
    public const string Prefix = "smartmetrix.v1";
    public const string All = Prefix + ".>";
    public const string DeadLetter = Prefix + ".dead-letter";

    public static string For<T>() => $"{Prefix}.{ToKebabCase(typeof(T).Name)}";

    private static string ToKebabCase(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $"-{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));
}

public sealed class UnsupportedSchemaVersionException(int actual, int supported)
    : Exception($"Schema version {actual} is not supported; expected {supported}.");

public static class EventEnvelopeSerializer
{
    public const int MaximumPayloadBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Serialize<T>(EventEnvelope<T> envelope)
    {
        if (envelope.SchemaVersion != EventEnvelope.CurrentSchemaVersion)
        {
            throw new UnsupportedSchemaVersionException(envelope.SchemaVersion, EventEnvelope.CurrentSchemaVersion);
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Event payload is {payload.Length} bytes; payloads above {MaximumPayloadBytes} bytes must use object-storage URIs.");
        }

        return payload;
    }

    public static EventEnvelope<T> Deserialize<T>(ReadOnlySpan<byte> payload)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope<T>>(payload, Options)
            ?? throw new JsonException("Event envelope is empty.");

        if (envelope.SchemaVersion != EventEnvelope.CurrentSchemaVersion)
        {
            throw new UnsupportedSchemaVersionException(envelope.SchemaVersion, EventEnvelope.CurrentSchemaVersion);
        }

        return envelope;
    }
}
