using System.Text;

namespace VoteCalc;

public static class StringBuilderExtensions
{
    private const string CRLF = "\r\n";

    public static StringBuilder AppendCrlf(this StringBuilder sb) =>
        sb.Append(CRLF);

    public static StringBuilder AppendLineCrlf(this StringBuilder sb, string? value = null)
    {
        if (value != null) sb.Append(value);
        return sb.Append(CRLF);
    }
}
