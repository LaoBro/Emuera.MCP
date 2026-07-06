using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.GameView;

internal record TurnRecord(
    string state,
    string? inputType,
    bool needValue,
    List<TurnOp> ops,
    string? error = null,
    int? protocolVersion = null
);

internal abstract record TurnOp(string type);

internal record PrintOp(
    List<PrintSegment> segments,
    ButtonRef? button
) : TurnOp("print");

internal record NewLineOp(string? align) : TurnOp("newline");

internal record ClearLineOp(int n) : TurnOp("clearline");

internal record ClearOp() : TurnOp("clear");

internal record SetBgOp(string color) : TurnOp("set_bg");

internal record PrintSegment(
    string text,
    string? color,
    bool? bold,
    bool? italic,
    string? fontname
);

internal record ButtonRef(object value, bool isInteger);

internal sealed class TurnOpConverter : JsonConverter<TurnOp>
{
    public override TurnOp? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("TurnOp deserialization not supported");

    public override void Write(Utf8JsonWriter writer, TurnOp value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, options);
    }
}
