using System.Diagnostics;
using System.Net;
using System.Text;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyDiscordApp.Configuration;

namespace VoteCalc
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
            var json = Environment.GetEnvironmentVariable("VOTE_CONFIG");
            if (string.IsNullOrEmpty(json))
            {
                json = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"));
            }
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
            //不再直接注册所有 Guild， 只受理部分 Guild 的请求。
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
        
        private struct ReactionSummary
        {
            public string Name { get; set; }
            public DiscordEmoji Emoji { get; set; }
            public int TotalReactions { get; set; }
            public int EffectiveCount { get; set; }
            public int Percentage { get; set; }
        }
        
        /// <summary>
        /// 解析URL字符串，提取Discord频道信息
        /// 支持逗号和冒号分隔符，冒号分隔的项目会被视为同一作品
        /// </summary>
        /// <param name="urls">包含Discord链接的字符串，支持逗号和冒号分隔</param>
        /// <param name="client">Discord客户端</param>
        /// <returns>返回频道映射、分组信息和主频道名称</returns>
        private async Task<(Dictionary<ulong, DiscordThreadChannel> channelMsgs, 
                          Dictionary<ulong, List<ulong>> channelGroups, 
                          Dictionary<ulong, string> primaryChannelNames)> 
            ParseUrlsAsync(string urls, DiscordClient client)
        {
            var channelMsgs = new Dictionary<ulong, DiscordThreadChannel>();
            var channelGroups = new Dictionary<ulong, List<ulong>>(); 
            var primaryChannelNames = new Dictionary<ulong, string>();
            
            // 首先按逗号分割获取独立项或冒号分组
            var items = urls.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()).ToList();
            
            foreach (var item in items)
            {
                // 检查是否包含冒号分隔的URL
                var colonParts = item.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()).ToList();
                var parsedChannels = new List<(ulong channelId, DiscordThreadChannel channel)>();
                
                // 解析当前项中的所有URL（无论是单个还是冒号分隔的多个）
                foreach (var raw in colonParts)
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
                        
                        // Discord链接格式：/channels/{guildId}/{channelId}
                        if (segs.Length >= 3 && segs[0] == "channels")
                        {
                            var channelId = ulong.Parse(segs[2]);
                            Console.WriteLine($"[DEBUG] Parsed forum channel link: channelId={channelId}");
                            
                            var forumChannel = await client.GetChannelAsync(channelId);
                            Console.WriteLine($"[DEBUG] Fetched Channel {forumChannel.IsThread}");
                            
                            if (!forumChannel.IsThread)
                            {
                                Console.WriteLine("Not thread, continue.");
                                continue;
                            }

                            var threadChannel = forumChannel as DiscordThreadChannel ?? throw new InvalidOperationException();
                            channelMsgs.TryAdd(forumChannel.Id, threadChannel);
                            parsedChannels.Add((forumChannel.Id, threadChannel));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Parsing URL failed: {raw}, error: {ex.Message}");
                    }
                }
                
                // 处理冒号分隔的分组逻辑
                if (colonParts.Count > 1 && parsedChannels.Count > 0)
                {
                    // 使用第一个解析的频道作为主频道
                    var primaryId = parsedChannels[0].channelId;
                    var primaryName = parsedChannels[0].channel.Name;
                    
                    // 创建包含所有已解析频道的分组
                    var groupIds = parsedChannels.Select(pc => pc.channelId).ToList();
                    channelGroups[primaryId] = groupIds;
                    primaryChannelNames[primaryId] = primaryName;
                    
                    Console.WriteLine($"[DEBUG] Created group: primary={primaryId} ({primaryName}), members={string.Join(",", groupIds)}");
                }
            }
            
            return (channelMsgs, channelGroups, primaryChannelNames);
        }
        
        /// <summary>
        /// 收集所有频道的反应数据
        /// </summary>
        private async Task<Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)>> 
            GatherReactionsAsync(Dictionary<ulong, DiscordThreadChannel> channelMsgs, ReactionAnalyzer analyzer)
        {
            var combinedTop = new Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)>();
            
            Console.WriteLine("[DEBUG] Starting reaction gathering...");
            foreach (var info in channelMsgs)
            {
                var channel = info.Value;
                try
                {
                    Console.WriteLine($"[DEBUG] Gathering for channel {channel.Name}");
                    var tops = await analyzer.PurrGatherTopReactionsAsync(channel, new[] { channel.Id });
                    
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
            
            return combinedTop;
        }
        
        /// <summary>
        /// 处理分组频道的投票合并逻辑
        /// 将同一分组内的多个频道视为一个作品进行投票计数
        /// </summary>
        private Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)> 
            MergeGroupedChannelsForVoting(
                Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)> combinedTop,
                Dictionary<ulong, List<ulong>> channelGroups)
        {
            var mergedTopForVoting = new Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)>();
            var processedForVoting = new HashSet<ulong>();
            
            foreach (var kv in combinedTop)
            {
                var channelId = kv.Key;
                if (processedForVoting.Contains(channelId))
                    continue;
                
                // 检查该频道是否属于某个分组
                var groupPrimary = channelGroups.FirstOrDefault(g => g.Value.Contains(channelId)).Key;
                if (groupPrimary != 0)
                {
                    // 该频道属于一个分组，合并组内所有频道的用户
                    var groupChannels = channelGroups[groupPrimary];
                    var mergedUsers = new HashSet<DiscordUser>();
                    DiscordEmoji topEmoji = null;
                    int totalCount = 0;
                    
                    foreach (var groupChannelId in groupChannels)
                    {
                        if (combinedTop.TryGetValue(groupChannelId, out var channelData))
                        {
                            if (topEmoji == null) topEmoji = channelData.TopEmoji;
                            totalCount += channelData.Count;
                            // 使用HashSet确保用户唯一性
                            foreach (var user in channelData.Users)
                            {
                                mergedUsers.Add(user);
                            }
                        }
                        processedForVoting.Add(groupChannelId);
                    }
                    
                    // 使用主频道ID添加合并后的条目
                    mergedTopForVoting[groupPrimary] = (topEmoji, totalCount, mergedUsers.ToList());
                    Console.WriteLine($"[DEBUG] Merged group {groupPrimary}: {mergedUsers.Count} unique users across {groupChannels.Count} channels");
                }
                else
                {
                    // 单个频道，不属于任何分组
                    mergedTopForVoting[channelId] = kv.Value;
                    processedForVoting.Add(channelId);
                }
            }
            
            return mergedTopForVoting;
        }
        
        /// <summary>
        /// 构建输出文本，处理分组频道的聚合显示
        /// </summary>
        private string BuildOutputText(
            Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)> combinedTop,
            Dictionary<ulong, DiscordThreadChannel> channelMsgs,
            Dictionary<ulong, List<ulong>> channelGroups,
            Dictionary<ulong, string> primaryChannelNames,
            Dictionary<ulong, int> effectiveCounts,
            HashSet<ulong> userIds,
            HashSet<DiscordUser> validUsers,
            bool outputUsers)
        {
            var sb = new StringBuilder();
            var processedChannels = new HashSet<ulong>();
            
            // 收集所有需要显示的项目及其有效评价数量
            var itemsToDisplay = new List<ReactionSummary>();
            
            foreach (var kv in combinedTop)
            {
                var id = kv.Key;
                
                // 跳过已作为分组一部分处理过的频道
                if (processedChannels.Contains(id))
                    continue;
                
                // 检查该频道是否是某个分组的主频道
                var groupPrimary = channelGroups.FirstOrDefault(g => g.Value.Contains(id)).Key;
                if (groupPrimary != 0)
                {
                    // 处理分组项目 - 聚合所有成员的结果
                    var groupIds = channelGroups[groupPrimary];
                    var primaryName = primaryChannelNames[groupPrimary];
                    
                    // 收集分组内所有频道的反应数据
                    var totalReactions = 0;
                    DiscordEmoji topEmoji = null;
                    
                    // 统计分组内的唯一有效用户
                    var uniqueEffectiveUsers = new HashSet<ulong>();
                    var uniqueUsers = new HashSet<ulong>();
                    foreach (var groupId in groupIds)
                    {
                        if (combinedTop.TryGetValue(groupId, out var groupData))
                        {
                            var (emoji, count, users) = groupData;
                            if (topEmoji == null) topEmoji = emoji;
                            
                            // 统计有效用户
                            foreach (var user in users)
                            {
                                if (userIds.Contains(user.Id))
                                {
                                    uniqueEffectiveUsers.Add(user.Id);
                                }
                                uniqueUsers.Add(user.Id);
                            }
                        }
                        processedChannels.Add(groupId);
                    }
                    totalReactions = uniqueUsers.Count;
                    
                    var uniqueEffectiveCount = uniqueEffectiveUsers.Count;
                    var percentage = totalReactions > 0 ? (int)(uniqueEffectiveCount * 100 / totalReactions) : 0;
                    
                    itemsToDisplay.Add(new ReactionSummary
                    {
                        Name = primaryName,
                        Emoji = topEmoji,
                        TotalReactions = totalReactions,
                        EffectiveCount = uniqueEffectiveCount,
                        Percentage = percentage
                    });
                }
                else
                {
                    // 处理单个频道
                    var channel = channelMsgs[id];
                    var (e, tot, us) = kv.Value;
                    var eff = effectiveCounts[id];
                    var percentage = tot > 0 ? (int)(eff * 100 / tot) : 0;
                    
                    itemsToDisplay.Add(new ReactionSummary
                    {
                        Name = channel.Name,
                        Emoji = e,
                        TotalReactions = tot,
                        EffectiveCount = eff,
                        Percentage = percentage
                    });
                    processedChannels.Add(id);
                }
            }
            
            // 按有效评价数量从多到少排序
            var sortedItems = itemsToDisplay.OrderByDescending(item => item.EffectiveCount);
            
            // 构建输出文本
            foreach (var item in sortedItems)
            {
                sb.AppendLineCrlf($"• 帖子 '{item.Name}'");
                sb.AppendLineCrlf(
                    $"最高表情 {item.Emoji} × {item.TotalReactions}，有效评价 {item.EffectiveCount} 人 , 比例 [{item.Percentage}%]");
            }
            
            // 如果需要输出有效用户列表
            if (outputUsers)
            {
                sb.AppendLineCrlf("======");
                sb.AppendLineCrlf("有效用户");
                foreach (var discordUser in validUsers)
                {
                    sb.AppendLineCrlf($"• '{discordUser.Username}', {discordUser.Presence}");
                }
            }
            
            return sb.ToString();
        }
        
        [SlashCommand("analyze", "统计论坛频道或指定主题帖的最高反应与有效用户数量（带调试日志）")]
        public async Task AnalyzeAsync(
            InteractionContext ctx,
            [Option("urls", "逗号分隔的频道或主题链接，如 https://discord.com/channels/服务器ID/频道ID)")] string urls,
            [Option("min_votes", "有效投票所需的最少投票作品数。")] long minVotes = 3,
            [Option("output_users", "是否输出所有有效投票用户,默认否")] bool outputUsers = false)
        {
            _watch.Restart();
                      
            // 发送处理中的响应
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource,
                new DiscordInteractionResponseBuilder()
                    .WithContent("⏳ 正在统计，请稍后…")
                    .AsEphemeral(true));
            
            // 权限检查
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
  
            
            await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"⏳ 评价收集中..."));


            // 步骤1：解析URL并获取频道信息
            var (channelMsgs, channelGroups, primaryChannelNames) = await ParseUrlsAsync(urls, ctx.Client);

            if (!channelMsgs.Any())
            {
                Console.WriteLine("[DEBUG] No valid channels or topics found. Exiting.");
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("⚠️ 未解析到有效的频道或主题链接。"));
                return;
            }
            await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"⏳ 已获取 {channelMsgs.Count} 个参赛作品..."));

            // 步骤2：收集所有频道的反应数据
            var analyzer = new ReactionAnalyzer(ctx.Client);
            var combinedTop = await GatherReactionsAsync(channelMsgs, analyzer);
            await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"⏳ 评价收集完成..."));

            // 步骤3：处理分组频道的投票合并
            // 同一分组内的多个频道会被视为一个作品，用户对组内多个频道的投票只计为一次
            Console.WriteLine("[DEBUG] Filtering valid users...");
            var mergedTopForVoting = MergeGroupedChannelsForVoting(combinedTop, channelGroups);
            
            // 步骤4：筛选有效用户（根据最少投票数要求）
            var validUsers = analyzer.MeowFilterValidUsers(mergedTopForVoting, minMessagesReacted: (int)minVotes);
            Console.WriteLine($"[DEBUG] Valid users count: {validUsers.Count}");
            var userIds = validUsers.Select(u => u.Id).ToHashSet();

            // 步骤5：统计每个频道的有效反应数
            Console.WriteLine("[DEBUG] Counting effective reactions...");
            var effectiveCounts = analyzer.NapCountEffectiveReactions(combinedTop, userIds);

            // 步骤6：构建输出文本
            var resultText = BuildOutputText(
                combinedTop, 
                channelMsgs, 
                channelGroups, 
                primaryChannelNames, 
                effectiveCounts, 
                userIds, 
                validUsers, 
                outputUsers);
            
            _watch.Stop();
            resultText += $"\n> 总计算时间：{_watch.Elapsed.TotalSeconds}";
            
            if (string.IsNullOrWhiteSpace(resultText))
            {
                resultText = "⚠️ 无统计结果。";
            }

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