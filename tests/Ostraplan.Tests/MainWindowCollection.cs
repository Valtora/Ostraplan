using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The test classes that build a real <c>MainWindow</c>, run one after another instead of beside each other. Each
/// builds its windows on an STA thread of its own, and two windows at once share state the app only ever touches from
/// its one UI thread: the real <c>%APPDATA%\Ostraplan\settings.json</c>, which a window loads when built and saves when
/// closed, and WPF's process-wide <c>DependencyPropertyDescriptor</c> for <c>ToolTip</c>, whose handler list
/// <c>ToolIcon</c> adds to. Run in parallel, either one fails a test at random.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MainWindowCollection
{
    public const string Name = "MainWindow";
}
