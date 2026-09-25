using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApTutor.Scene;

/// A handful of SceneOp fields (memCellSet.value, exprResolve.value, etc.) hold a value that's
/// meant to be DISPLAYED as text but is, semantically, "whatever the traced expression evaluated
/// to" — so a content-generating model naturally emits a bare JSON number/bool for something like
/// an int or boolean result (`"value": 42`) instead of a quoted string (`"value": "42"`), even
/// though the field's wire contract has always been a plain string. Rather than rejecting content
/// generation over a formatting choice the prompt never pinned down, this converter accepts a
/// string, number, or bool token and stores its literal text — a JSON array/object still throws,
/// since that really is a malformed op the model got wrong.
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()!,
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True or JsonTokenType.False => reader.GetBoolean().ToString().ToLowerInvariant(),
            _ => throw new JsonException($"Expected a string, number, or bool, got {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
