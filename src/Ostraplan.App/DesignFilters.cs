using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>The file-dialog filters for a design, in one place, so every dialog that reads one takes an
/// <c>.oplanz</c> and a zipped design as well, and both dialogs that write one offer both forms.</summary>
internal static class DesignFilters
{
    /// <summary>For a dialog that reads a design. A <c>.zip</c> is listed for the designs people share zipped, and
    /// <see cref="OplanFile.Load"/> reads the one design inside it.</summary>
    public const string Open =
        "Ostraplan ship (*.oplan;*.oplanz)|*.oplan;*.oplanz|Zipped ship (*.zip)|*.zip|All files (*.*)|*.*";

    /// <summary>For a dialog that writes a design. The extension picked decides the form (see
    /// <see cref="OplanFile.Save"/>).</summary>
    public const string Save = "Ostraplan ship (*.oplan)|*.oplan|Compressed Ostraplan ship (*.oplanz)|*.oplanz";

    /// <summary>The <see cref="Save"/> filter to start on, counted from 1 as the dialog counts: the one matching the
    /// design's own file, so Save As on a compressed design stays compressed unless the user picks otherwise.</summary>
    public static int SaveIndexFor(string? path) => path is not null && OplanFile.IsCompressed(path) ? 2 : 1;
}
