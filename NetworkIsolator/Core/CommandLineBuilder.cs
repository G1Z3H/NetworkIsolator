using System.Text;

namespace NetworkIsolator.Core;

/// <summary>Correct construction of Windows command lines (spaces, quotes, backslashes).</summary>
public static class CommandLineBuilder
{
    /// <summary>Escapes ONE argument following the rules of the Windows C runtime parser.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return arg;

        var sb = new StringBuilder("\"");
        for (int i = 0; i < arg.Length;)
        {
            char c = arg[i++];
            if (c == '\\')
            {
                int backslashes = 1;
                while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }

                if (i == arg.Length)
                    sb.Append('\\', backslashes * 2);            // before the closing quote
                else if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1).Append('"'); // before a literal quote
                    i++;
                }
                else
                    sb.Append('\\', backslashes);
            }
            else if (c == '"')
                sb.Append("\\\"");
            else
                sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Joins several arguments into one line.</summary>
    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    /// <summary>"C:\path\app.exe" arguments  (the path is always quoted).</summary>
    public static string ForExecutable(string exePath, string? arguments) =>
        string.IsNullOrWhiteSpace(arguments)
            ? "\"" + exePath + "\""
            : "\"" + exePath + "\" " + arguments;
}
