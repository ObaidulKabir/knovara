namespace Knovara.Cli;

/// <summary>
/// Splits argv into a command, its positional words, and --flag value options. Options can appear
/// anywhere; a flag with no following value (or followed by another flag) is treated as boolean "true".
/// </summary>
public sealed class Args
{
    public string? Command { get; }
    public IReadOnlyList<string> Positional { get; }
    private readonly Dictionary<string, string> _options;

    private Args(string? command, List<string> positional, Dictionary<string, string> options)
    {
        Command = command;
        Positional = positional;
        _options = options;
    }

    public static Args Parse(string[] argv)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = argv[i][2..];
                var hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal);
                options[name] = hasValue ? argv[++i] : "true";
            }
            else
            {
                positional.Add(argv[i]);
            }
        }

        var command = positional.Count > 0 ? positional[0] : null;
        if (command is not null) positional.RemoveAt(0);
        return new Args(command, positional, options);
    }

    public string? Option(string name) => _options.TryGetValue(name, out var value) ? value : null;

    /// <summary>Joins the remaining positional words back into free text, for a question that wasn't quoted.</summary>
    public string? Text() => Positional.Count > 0 ? string.Join(' ', Positional) : null;
}
