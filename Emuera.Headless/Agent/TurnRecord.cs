using System.Collections.Generic;

namespace MinorShift.Emuera.GameView;

internal record TurnRecord(
    string text,
    string state,
    string? inputType,
    bool needValue,
    List<ButtonEntry> buttons,
    string? error = null,
    int? protocolVersion = null
);

internal record ButtonEntry(string label, object value);
