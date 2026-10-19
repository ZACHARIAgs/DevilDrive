using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CapstonePdm.Core.Git;

/// <summary>
/// Builds a Windows command line (netstandard2.0 has no ProcessStartInfo.ArgumentList).
/// Follows the CommandLineToArgvW quoting rules.
/// </summary>
internal static class CommandLine
{
    private static readonly char[] NeedsQuoting = { ' ', '\t', '\n', '\v', '"' };

    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(NeedsQuoting) < 0) return arg;

        var sb = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == arg.Length)
            {
                // Backslashes before the closing quote must be doubled.
                sb.Append('\\', backslashes * 2);
                break;
            }

            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(arg[i]);
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
