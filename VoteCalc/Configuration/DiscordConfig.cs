namespace MyDiscordApp.Configuration
{
    public class DiscordConfig
    {
        public string Token { get; set; } = string.Empty;
        public string? Proxy { get; set; }
        public ulong[] TestGuildIds { get; set; } = Array.Empty<ulong>();
        public ulong[] WhitelistUserIds { get; set; } = Array.Empty<ulong>();
        public string[] AllowedRoles { get; set; } = Array.Empty<string>();
    }

    public class AppConfig
    {
        public DiscordConfig Discord { get; set; } = new();
    }
}