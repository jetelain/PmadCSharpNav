namespace Pmad.CSharpNav.Cli.Formatting;

public sealed class ColorTheme
{
    public bool Enabled { get; }

    public ColorTheme(bool enabled)
    {
        Enabled = enabled;
    }

    public static ColorTheme None => new(false);
    public static ColorTheme Default => new(true);

    public string Reset => Enabled ? "\x1b[0m" : "";
    public string Bold(string text) => Enabled ? $"\x1b[1m{text}\x1b[0m" : text;
    public string Dim(string text) => Enabled ? $"\x1b[90m{text}\x1b[0m" : text;
    public string Header(string text) => Enabled ? $"\x1b[1m\x1b[96m{text}\x1b[0m" : text; // Bold Bright Cyan
    public string Symbol(string text) => Enabled ? $"\x1b[1m\x1b[36m{text}\x1b[0m" : text; // Bold Cyan
    public string Kind(string text) => Enabled ? $"\x1b[33m{text}\x1b[0m" : text; // Yellow
    public string Location(string text) => Enabled ? $"\x1b[96m{text}\x1b[0m" : text; // Bright Cyan
    public string Assembly(string text) => Enabled ? $"\x1b[32m{text}\x1b[0m" : text; // Green
    public string Member(string text) => Enabled ? $"\x1b[95m{text}\x1b[0m" : text; // Bright Magenta
    public string Snippet(string text) => Enabled ? $"\x1b[90m{text}\x1b[0m" : text; // Dim / Gray
    public string TableBorder(string text) => Enabled ? $"\x1b[90m{text}\x1b[0m" : text; // Dim Gray
    public string TableHeader(string text) => Enabled ? $"\x1b[1m\x1b[97m{text}\x1b[0m" : text; // Bold Bright White
    public string Error(string text) => Enabled ? $"\x1b[1m\x1b[91m{text}\x1b[0m" : text; // Bold Bright Red
    public string Warning(string text) => Enabled ? $"\x1b[1m\x1b[93m{text}\x1b[0m" : text; // Bold Bright Yellow
}
