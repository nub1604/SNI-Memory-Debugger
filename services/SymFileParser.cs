using System.Globalization;

namespace MemoryDebgugger.Services;

public static class SymFileParser
{
    public static List<SymLabel> Parse(string path, bool includeRomLabels)
    {
        var rawLabels = new List<(uint Address, string Name)>();
        var sizes = new Dictionary<string, uint>(StringComparer.Ordinal);

        string? section = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            switch (section)
            {
                case "labels":
                {
                    if (!uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var addr))
                        continue;

                    var name = parts[1].Trim();
                    if (name.StartsWith("__local_", StringComparison.Ordinal))
                        continue;

                    rawLabels.Add((addr, name));
                    break;
                }
                case "definitions":
                {
                    if (!uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size))
                        continue;

                    var name = parts[1].Trim();
                    if (name.StartsWith("_sizeof___local_", StringComparison.Ordinal))
                        continue;
                    if (!name.StartsWith("_sizeof_", StringComparison.Ordinal))
                        continue;

                    sizes[name["_sizeof_".Length..]] = size;
                    break;
                }
                default:
                {
                    if (!uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var addr))
                        continue;

                    var name = parts[1].Trim();
                    if (name.StartsWith("__local_", StringComparison.Ordinal))
                        continue;

                    rawLabels.Add((addr, name));
                    break;
                }
            }
        }

        var result = new List<SymLabel>(rawLabels.Count);
        foreach (var (addr, name) in rawLabels)
        {
            var bank = (addr >> 16) & 0xFF;
            var isWram = bank is 0x7E or 0x7F;

           
            if (!includeRomLabels && !isWram)
                continue;

            var size = sizes.TryGetValue(name, out var s) ? s : 1u;
            result.Add(new SymLabel(addr, name, size));
        }

        return result.OrderBy(l => l.Address).ToList();
    }
}