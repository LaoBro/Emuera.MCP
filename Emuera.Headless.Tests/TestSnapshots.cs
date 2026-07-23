using MinorShift.Emuera.GameView;

namespace Emuera.Headless.Tests;

internal static class TestSnapshots
{
    internal static ButtonRef Button(
        object value, bool isInteger,
        long generation,
        int? col = null, int? width = null)
        => new(value, isInteger, generation, col, width);
}
