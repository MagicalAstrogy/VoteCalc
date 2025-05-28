using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyDiscordApp.Configuration;

namespace MyDiscordApp
{
    class Program
    {
        private static AppConfig _config = new();
        public static AppConfig Config => _config;
        
        static async Task Main(string[] args)
        {
            // Load configuration
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            
            var configuration = builder.Build();
            var json = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"));
            _config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            
            if (string.IsNullOrWhiteSpace(_config.Discord.Token))
            {
                Console.WriteLine("Please specify a token in appsettings.json.");
                Environment.Exit(1);
                return;
            }

            // 1. 初始化 DiscordClient
            var discordConfig = new DiscordConfiguration
            {
                MinimumLogLevel = LogLevel.Information,
                Token = _config.Discord.Token,
                TokenType = TokenType.Bot,
                Intents = DiscordIntents.Guilds | DiscordIntents.GuildMessages | DiscordIntents.GuildMessageReactions
            };
            
            if (!string.IsNullOrWhiteSpace(_config.Discord.Proxy))
            {
                discordConfig.Proxy = new WebProxy(_config.Discord.Proxy);
            }
            
            var discord = new DiscordClient(discordConfig);

            // 2. 注册 Slash Commands 扩展
            var slash = discord.UseSlashCommands(new SlashCommandsConfiguration
            {
                Services = null // 如果有依赖注入可传入 ServiceProvider
            });

            // 3. 将命令模块注册到指定的 Guild（开发阶段）或全局
            foreach (var guildId in _config.Discord.TestGuildIds)
            {
                slash.RegisterCommands<AnalyzeModule>(guildId);
            }
            slash.RegisterCommands<AnalyzeModule>();
            // 正式环境可注册全局命令： slash.RegisterCommands<AnalyzeModule>();

            // 4. 连接并运行
            await discord.ConnectAsync();
            await Task.Delay(-1);
        }
    }

    public class AnalyzeModule : ApplicationCommandModule
    {
        private readonly Stopwatch _watch = new Stopwatch();
        [SlashCommand("analyze", "统计论坛频道或指定主题帖的最高反应与有效用户数量（带调试日志）")]
        public async Task AnalyzeAsync(
            InteractionContext ctx,
            [Option("urls", "逗号分隔的频道或主题链接，如 https://discord.com/channels/服务器ID/频道ID)")] string urls,
            [Option("min_votes", "有效投票所需的最少投票作品数。")] long minVotes = 3,
            [Option("output_users", "是否输出所有有效投票用户,默认否")] bool outputUsers = false)
        {
            _watch.Restart();
            var whiteList = Program.Config.Discord.WhitelistUserIds;
            var allowedRoles = Program.Config.Discord.AllowedRoles;
            Console.WriteLine($"[DEBUG] /analyze invoked by {ctx.User.Username}, urls: {urls}");
            if (!whiteList.Contains(ctx.User.Id) && 
                !(ctx.Member?.Roles?.Any(r => allowedRoles.Contains(r.Name)) ?? false))
            {
                Console.WriteLine("[DEBUG] Not In WhiteList or proper role, skip");
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("⚠️ 不在白名单内，无法使用。"));
                return; 
            }
            // 延迟 ACK
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource,
                new DiscordInteractionResponseBuilder()
                    .WithContent("⏳ 正在统计，请稍后…")
                    .AsEphemeral(true));

            // 解析 URL 列表
            var channelMsgs = new Dictionary<ulong /*id*/, DiscordThreadChannel>();
            foreach (var raw in urls.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()))
            {
                if (!Uri.IsWellFormedUriString(raw, UriKind.Absolute))
                {
                    Console.WriteLine($"[DEBUG] Skipping invalid URL: {raw}");
                    continue;
                }
                try
                {
                    var uri = new Uri(raw);
                    var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    // 主题链接：/channels/{guildId}/{channelId}/{messageId}
                    // 频道链接：/channels/{guildId}/{channelId}
                    if (segs.Length >= 3 && segs[0] == "channels")
                    {
                        var channelId = ulong.Parse(segs[2]);
                        Console.WriteLine($"[DEBUG] Parsed forum channel link: channelId={channelId}, fetching recent topics...");
                        var forumChannel = await ctx.Client.GetChannelAsync(channelId);
                        Console.WriteLine($"[DEBUG] Fetched Channel {forumChannel.IsThread}");
                        if (!forumChannel.IsThread)
                        {
                            Console.WriteLine("Not thread, continue.");
                            continue;
                        }

                        channelMsgs.TryAdd(forumChannel.Id,
                            forumChannel as DiscordThreadChannel ?? throw new InvalidOperationException());
                    }
                    else
                    {
                        Console.WriteLine($"[DEBUG] URL path not recognized: {uri.AbsolutePath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Parsing URL failed: {raw}, error: {ex.Message}");
                }
            }

            if (!channelMsgs.Any())
            {
                Console.WriteLine("[DEBUG] No valid channels or topics found. Exiting.");
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("⚠️ 未解析到有效的频道或主题链接。"));
                return;
            }

            // 执行统计
            var analyzer = new ReactionAnalyzer(ctx.Client);
            var combinedTop = new Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)>();

            Console.WriteLine("[DEBUG] Starting reaction gathering...");
            foreach (var info in channelMsgs)
            {
                var channel = info.Value;
                try
                {
                    var ch = channel;
                    Console.WriteLine($"[DEBUG] Gathering for channel {ch.Name}");
                    var tops = await analyzer.PurrGatherTopReactionsAsync(ch, new []{ch.Id});
                    foreach (var item in tops)
                    {
                        combinedTop[item.Key] = item.Value;
                        Console.WriteLine($"[DEBUG] Message {item.Key}: Top {item.Value.TopEmoji} x {item.Value.Count}, users={item.Value.Users.Count}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Error fetching reactions for channel {channel.Name}: {ex.Message}");
                }
            }

            Console.WriteLine("[DEBUG] Filtering valid users...");
            var validUsers = analyzer.MeowFilterValidUsers(combinedTop, minMessagesReacted: (int)minVotes);
            Console.WriteLine($"[DEBUG] Valid users count: {validUsers.Count}");
            var userIds = validUsers.Select(u => u.Id).ToHashSet();

            Console.WriteLine("[DEBUG] Counting effective reactions...");
            var effectiveCounts = analyzer.NapCountEffectiveReactions(combinedTop, userIds);

            // 构造输出
            var sb = new StringBuilder();
            foreach (var kv in combinedTop)
            {
                var id    = kv.Key;
                var channel = channelMsgs[id];
                var (e, tot, us) = kv.Value;
                var eff   = effectiveCounts[id];
                sb.AppendLine($"• 帖子 '{channel.Name}'：最高表情 {e} × {tot}，有效评价 {eff} 人 , 比例 [{(int)(eff * 100 / tot )}%]");
            }

            if (outputUsers)
            {
                sb.AppendLine("======");
                sb.AppendLine("有效用户");
                foreach (var discordUser in validUsers)
                {
                    sb.AppendLine($"• '{discordUser.Username}', {discordUser.Presence}");
                }
            }

            _watch.Stop();
            sb.AppendLine($"> 总计算时间：{_watch.Elapsed.TotalSeconds}");
            
            var resultText = sb.Length > 0 ? sb.ToString() : "⚠️ 无统计结果。";

            Console.WriteLine("[DEBUG] Analysis complete, sending result...");
            // 分段发送（基于换行）
            var paragraphs = SplitIntoChunksByLines(resultText, maxChunkSize: 1800);
            try
            {
                // 第一段：EditResponse
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent(paragraphs[0]));
                // 后续段落：FollowUp
                for (int idx = 1; idx < paragraphs.Count; idx++)
                {
                    Console.WriteLine($"[DEBUG] Sending follow-up chunk {idx + 1}/{paragraphs.Count}");
                    await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder()
                        .WithContent(paragraphs[idx])
                        .AsEphemeral(true)); 
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Error sending follow-up chunk: {ex.Message}, Content: {resultText}");
            }
        }
        
        /// <summary>
        /// 按行拆分文本，确保每段不超过 maxChunkSize 字符
        /// </summary>
        private List<string> SplitIntoChunksByLines(string text, int maxChunkSize)
        {
            var chunks = new List<string>();
            var current = new StringBuilder();
            foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.None))
            {
                // +1 for the '\n' character that will be re-added
                if (current.Length + line.Length + 1 > maxChunkSize)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                current.Append(line);
            }
            if (current.Length > 0)
                chunks.Add(current.ToString());
            return chunks;
        }
    }
}