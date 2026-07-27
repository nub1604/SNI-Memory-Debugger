namespace MemoryDebgugger.Services;

public sealed record SymLabel(uint Address, string Name, uint Size)
{
    public string AddressHex => Address.ToString("X6");
}