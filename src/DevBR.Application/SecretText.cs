using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBR.Application;

/// <summary>
/// Wraps a password or token so it cannot reach logs or reports through ToString, string
/// interpolation, or record printing. The value is only obtainable through <see cref="Reveal"/>.
/// </summary>
[JsonConverter(typeof(SecretTextJsonConverter))]
public sealed class SecretText : IEquatable<SecretText>
{
    private readonly string _value;

    public SecretText(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public string Reveal() => _value;

    public override string ToString() => "[redacted]";

    public bool Equals(SecretText? other) => other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as SecretText);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);
}

/// <summary>Serializes the value for in-memory IPC only. Never use with log or report serializers.</summary>
public sealed class SecretTextJsonConverter : JsonConverter<SecretText>
{
    public override SecretText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return string.IsNullOrEmpty(value) ? null : new SecretText(value);
    }

    public override void Write(Utf8JsonWriter writer, SecretText value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Reveal());
}
