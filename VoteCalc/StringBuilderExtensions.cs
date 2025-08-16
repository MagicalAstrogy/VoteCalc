using System.Text;
using DSharpPlus.Entities;

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
    
    public static string GetThreadUrl(this DiscordThreadChannel thread)
    {
        return $"https://discord.com/channels/{thread.GuildId}/{thread.Id}/{thread.Id}";
    }
}
