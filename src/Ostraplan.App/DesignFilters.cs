using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>The file-dialog filters for a design, in one place, so every dialog that reads one takes both forms and
/// a zipped design as well, and both dialogs that write one offer both forms with the compressed one first.</summary>
internal static class DesignFilters
{
    /// <summary>For a dialog that reads a design. A <c>.zip</c> is listed for the designs people share zipped, and
    /// <see cref="OplanFile.Load"/> reads the one design inside it.</summary>
    public const string Open =
        "Ostraplan ship (*.oplanz;*.oplan)|*.oplanz;*.oplan|Zipped ship (*.zip)|*.zip|All files (*.*)|*.*";

    /// <summary>For a dialog that writes a design. The extension picked decides the form (see
    /// <see cref="OplanFile.Save"/>), and the compressed one is the default.</summary>
    public const string Save = "Ostraplan ship (*.oplanz)|*.oplanz|Uncompressed Ostraplan ship (*.oplan)|*.oplan";

    /// <summary>The <see cref="Save"/> filter to start on, counted from 1 as the dialog counts: the uncompressed one
    /// only for a design the user has already chosen to keep plain (see <see cref="DocumentSession.KeepPlain"/>).</summary>
    public static int SaveIndexFor(bool keepPlain) => keepPlain ? 2 : 1;
}
