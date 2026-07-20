using System.ComponentModel;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>Palette row: a buildable part (or loose item) plus its pre-built (frozen) thumbnail. Raises
/// <see cref="INotifyPropertyChanged"/> for <see cref="IsFavorite"/> so the row's star glyph updates live wherever
/// the same instance is shown (the catalog tab and the ★ Favorites tab share one instance per part).</summary>
public sealed class PartVM(PartDef part, ImageSource thumb, bool isLoose = false) : INotifyPropertyChanged
{
    public PartDef Part { get; } = part;
    public ImageSource Thumb { get; } = thumb;
    /// <summary>True for an ITEMS-tab loose item (armed as a single-click drop), false for a buildable part. Pairs
    /// with <see cref="PartDef.DefName"/> to key the part for Favorites/Recent.</summary>
    public bool IsLoose { get; } = isLoose;
    public string Friendly => Part.Friendly;
    public string Sub => Part.Origin is "core" ? Part.DefName : $"{Part.DefName} · {Part.Origin}";

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavGlyph)));
        }
    }

    /// <summary>The star drawn on the row: filled when pinned, hollow otherwise (the template colours/fades each).</summary>
    public string FavGlyph => _isFavorite ? "★" : "☆";

    public bool Matches(string search) =>
        search.Length == 0
        || Part.Friendly.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Part.DefName.Contains(search, StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;
}
