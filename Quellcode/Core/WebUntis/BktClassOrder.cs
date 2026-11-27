using System.Globalization;

namespace WebUntisAuswertung.Core;

internal static class BktClassOrder
{
    private const string StageOrder = "UMOPA";
    private static readonly StringComparer ByName = StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), true);

    public static Student[] Sort(IEnumerable<Student> classes) => classes
        .Select(item => (Class: item, Key: SortKey(item.Name)))
        .OrderBy(item => item.Key.Prefix, ByName)
        .ThenBy(item => item.Key.Stage)
        .ThenBy(item => item.Key.Number)
        .ThenBy(item => item.Key.HasPSuffix)
        .ThenBy(item => item.Class.Name, ByName)
        .Select(item => item.Class)
        .ToArray();

    private static (string Prefix, int Stage, int Number, bool HasPSuffix) SortKey(string name)
    {
        if (name.Length >= 4 && char.IsLetter(name[0]) && char.IsLetter(name[1]))
        {
            var stage = StageOrder.IndexOf(char.ToUpperInvariant(name[2]));
            if (stage >= 0)
            {
                var suffix = name.AsSpan(3);
                if (suffix.Equals("P", StringComparison.OrdinalIgnoreCase)) return (name[..2], stage, 1, true);
                if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    return (name[..2], stage, number, false);
            }
        }
        return (name, -1, 0, false);
    }
}
