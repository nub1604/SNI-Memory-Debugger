namespace MemoryDebgugger.Services;

public static class SnesAddress
{
    // Übersetzt eine SNES-WRAM-Busadresse ($7E/$7F, wie in .sym-Dateien üblich)
    // in die lineare FX-Pak-Pro-WRAM-Adresse ($F5/$F6), die SNI im FxPakPro-
    // Adressraum erwartet ($F50000..F6FFFF = 128 KB WRAM, linear gemappt).
    public static uint ToFxPakPro(uint snesAddress)
    {
        var bank = (snesAddress >> 16) & 0xFF;
        var offset = snesAddress & 0xFFFF;

        return bank switch
        {
            0x7E => 0xF50000 + offset,
            0x7F => 0xF60000 + offset,
            _ => throw new NotSupportedException(
                $"Bank ${bank:X2} wird aktuell nicht unterstützt (nur WRAM $7E/$7F).")
        };
    }
}