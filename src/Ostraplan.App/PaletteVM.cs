using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>Palette row: a buildable part plus its pre-built (frozen) thumbnail.</summary>
public sealed class PartVM(PartDef part, ImageSource thumb)
{
    public PartDef Part { get; } = part;
    public ImageSource Thumb { get; } = thumb;
    public string Friendly => Part.Friendly;
    public string Sub => Part.Origin is "core" ? Part.DefName : $"{Part.DefName} · {Part.Origin}";

    public bool Matches(string search) =>
        search.Length == 0
        || Part.Friendly.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Part.DefName.Contains(search, StringComparison.OrdinalIgnoreCase);
}
