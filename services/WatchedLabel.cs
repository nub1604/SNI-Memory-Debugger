using Avalonia.Controls;
using MemoryDebgugger.Services;

namespace MemoryDebgugger;

public sealed class WatchedLabel
{
    public required SymLabel Label { get; init; }
    public required TextBlock ValueText { get; init; }
    public required TextBox WriteValueBox { get; init; }
}