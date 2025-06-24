using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
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
                // 添加GuildMembers intent以支持RequestMembersAsync
                Intents = DiscordIntents.Guilds | DiscordIntents.GuildMessages | DiscordIntents.GuildMessageReactions | DiscordIntents.GuildMembers
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
            /*
            foreach (var guildId in _config.Discord.TestGuildIds)
            {
                slash.RegisterCommands<AnalyzeModule>(guildId);
            }*/
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
            public double WeightedCount { get; set; }
            public int Percentage { get; set; }
        }
        
        private class AnalyzeContext
        {
            public Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)> CombinedTop { get; set; }
            public Dictionary<ulong, DiscordThreadChannel> ChannelMsgs { get; set; }
            public Dictionary<ulong, List<ulong>> ChannelGroups { get; set; }
            public Dictionary<ulong, string> PrimaryChannelNames { get; set; }
            public Dictionary<ulong, int> EffectiveCounts { get; set; }
            public HashSet<ulong> UserIds { get; set; }
            public HashSet<DiscordUser> ValidUsers { get; set; }
            public bool OutputUsers { get; set; }
            public Dictionary<ulong, double> UserWeights { get; set; }
            public Dictionary<string, int> RoleDistribution { get; set; }
            public List<string> ErrorInfo { get; set; }
        }
        
        /// <summary>
        /// 预扫描items列表，提取消息中的Discord频道链接
        /// </summary>
        /// <param name="items">原始URL列表（将被原地修改）</param>
        /// <param name="client">Discord客户端</param>
        /// <param name="errorInfo">收集运行过程中的错误信息</param>
        /// <returns>返回消息链接集合</returns>
        private async Task<HashSet<string>> 
            PreScanForMessageLinksAsync(List<string> items, DiscordClient client, List<string>? errorInfo)
        {
            var extractedUrls = new List<string>();
            var messageItems = new HashSet<string>(); // 记录已处理的消息链接
            
            foreach (var item in items)
            {
                // 检查是否包含冒号分隔的URL
                var colonParts = item.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()).ToList();
                
                foreach (var raw in colonParts)
                {
                    if (!Uri.IsWellFormedUriString(raw, UriKind.Absolute))
                        continue;
                        
                    try
                    {
                        var uri = new Uri(raw);
                        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        
                        // 检查是否是消息链接格式：/channels/{guildId}/{channelId}/{messageId}
                        if (segs.Length == 4 && segs[0] == "channels")
                        {
                            messageItems.Add(item); // 记录这是一个消息链接项
                            
                            var guildId = ulong.Parse(segs[1]);
                            var channelId = ulong.Parse(segs[2]);
                            var messageId = ulong.Parse(segs[3]);
                            
                            Console.WriteLine($"[DEBUG] Found message link: guild={guildId}, channel={channelId}, message={messageId}");
                            
                            // 获取消息内容
                            try
                            {
                                var channel = await client.GetChannelAsync(channelId);
                                var message = await channel.GetMessageAsync(messageId, true);
                                
                                if (message != null)
                                {
                                    var content = message.Content;
                                    if (message.Embeds.Count > 0)
                                    {
                                        foreach (var embed in message.Embeds)
                                        {
                                            content += '\n';
                                            content += embed.Description;
                                            Console.WriteLine($"[DEBUG] Embed msg, get description.");
                                        }
                                    }
                                    // 先检查消息内容中是否包含~连接的链接
                                    // 按行分割消息内容，每行可能包含一个或多个用~连接的链接
                                    var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                    
                                    foreach (var line in lines)
                                    {
                                        // 检查这一行是否包含Discord频道链接
                                        var pattern = @"https://discord\.com/channels/(\d+)/(\d+)";
                                        var matches = System.Text.RegularExpressions.Regex.Matches(line, pattern);
                                        
                                        if (matches.Count > 0)
                                        {
                                            // 检查这一行是否包含~分隔符
                                            if (line.Contains("~"))
                                            {
                                                // 提取所有URL并用~连接
                                                var urlsInLine = new List<string>();
                                                foreach (System.Text.RegularExpressions.Match match in matches)
                                                {
                                                    urlsInLine.Add(match.Value);
                                                }
                                                var joinedUrls = string.Join("~", urlsInLine);
                                                extractedUrls.Add(joinedUrls);
                                                Console.WriteLine($"[DEBUG] Extracted grouped URLs from message: {joinedUrls}");
                                            }
                                            else
                                            {
                                                // 没有~分隔符，单独添加每个URL
                                                foreach (System.Text.RegularExpressions.Match match in matches)
                                                {
                                                    extractedUrls.Add(match.Value);
                                                    Console.WriteLine($"[DEBUG] Extracted single URL from message: {match.Value}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ERROR] Failed to fetch message content: {ex.Message}");
                                errorInfo?.Add($"获取消息内容失败 (ID: {messageId}): {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Failed to parse URL in pre-scan: {raw}, error: {ex.Message}");
                        errorInfo?.Add($"预扫描解析URL失败 ({raw}): {ex.Message}");
                    }
                }
            }
            
            // 原地修改items列表，添加提取的URL
            items.AddRange(extractedUrls);
            
            return messageItems;
        }
        
        /// <summary>
        /// 解析URL字符串，提取Discord频道信息
        /// 支持逗号和冒号分隔符，冒号分隔的项目会被视为同一作品
        /// </summary>
        /// <param name="urls">包含Discord链接的字符串，支持逗号和冒号分隔</param>
        /// <param name="client">Discord客户端</param>
        /// <param name="errorInfo">收集运行过程中的错误信息</param>
        /// <returns>返回频道映射、分组信息和主频道名称</returns>
        private async Task<(Dictionary<ulong, DiscordThreadChannel> channelMsgs, 
                          Dictionary<ulong, List<ulong>> channelGroups, 
                          Dictionary<ulong, string> primaryChannelNames)> 
            ParseUrlsAsync(string urls, DiscordClient client, List<string>? errorInfo)
        {
            var channelMsgs = new Dictionary<ulong, DiscordThreadChannel>();
            var channelGroups = new Dictionary<ulong, List<ulong>>(); 
            var primaryChannelNames = new Dictionary<ulong, string>();
            
            // 首先按逗号分割获取独立项或冒号分组
            var items = urls.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()).ToList();
            
            // 预扫描阶段：提取消息中的频道链接
            var messageItems = await PreScanForMessageLinksAsync(items, client, errorInfo);
            
            foreach (var item in items)
            {
                // 跳过已经在预扫描中处理过的消息链接
                if (messageItems.Contains(item))
                {
                    Console.WriteLine($"[DEBUG] Skipping message item: {item}");
                    continue;
                }
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
                        errorInfo?.Add($"解析URL失败 ({raw}): {ex.Message}");
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
            GatherReactionsAsync(Dictionary<ulong, DiscordThreadChannel> channelMsgs, ReactionAnalyzer analyzer, string validReactions = "", List<string>? errorInfo = null)
        {
            var combinedTop = new Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)>();
            
            Console.WriteLine("[DEBUG] Starting reaction gathering...");
            foreach (var info in channelMsgs)
            {
                var channel = info.Value;
                try
                {
                    Console.WriteLine($"[DEBUG] Gathering for channel {channel.Name}");
                    var tops = await analyzer.PurrGatherTopReactionsAsync(channel, new[] { channel.Id }, validReactions);
                    
                    foreach (var item in tops)
                    {
                        combinedTop[item.Key] = item.Value;
                        Console.WriteLine($"[DEBUG] Message {item.Key}: Top {item.Value.TopEmoji} x {item.Value.Count}, users={item.Value.Users.Count}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Error fetching reactions for channel {channel.Name}: {ex.Message}");
                    errorInfo?.Add($"获取频道反应失败 ({channel.Name}): {ex.Message}");
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
        /// 依据 validUsers 列表，一次性拉取这些成员在指定 Guild 中的角色信息。
        /// </summary>
        /// <param name="client"></param>
        /// <param name="guild">目标 DiscordGuild。</param>
        /// <param name="validUsers">欲查询的 DiscordUser 集合。</param>
        /// <param name="ct">可选的取消令牌。</param>
        /// <returns>
        /// 字典：key 为用户 ID，value 为该成员在 guild 中拥有的全部角色集合。
        /// </returns>
        public static async Task<Dictionary<ulong, List<DiscordRole>>> FetchRolesForAllUsersAsync(DiscordClient client,
            DiscordGuild guild,
            HashSet<DiscordUser> validUsers,
            CancellationToken ct = default,
            List<string>? errorInfo = null)
        {
            const int BatchSize = 100; // Discord 限制
            const int MaxRetries = 3; // 最大重试次数
            const int TimeoutMilliseconds = 3000; // 3秒超时
            
            var result = new Dictionary<ulong, List<DiscordRole>>();
            var idBatches = validUsers
                .Select(u => u.Id)
                .Distinct()
                .Chunk(BatchSize); // .NET 8 才有的扩展；若在旧版可自行实现

            // 获取Discord客户端

            foreach (var idBatch in idBatches)
            {
                // 重试逻辑
                for (int retry = 0; retry <= MaxRetries; retry++)
                {
                    try
                    {
                        // 1. 预备
                        var nonce = Guid.NewGuid().ToString("N");   // 32 字符，刚好
                        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

                        // 2. 订阅一次性的事件处理器
                        async Task Handler(DiscordClient _, DSharpPlus.EventArgs.GuildMembersChunkEventArgs e)
                        {
                            if (e.Guild.Id != guild.Id || e.Nonce != nonce)
                                return;                                 // 不是我们这次请求的包
                            client.GuildMembersChunked -= Handler;  // 解绑
                            tcs.TrySetResult(0);
                        }

                        client.GuildMembersChunked += Handler;

                        try
                        {
                            // 3. 发送请求
                            await guild.RequestMembersAsync(userIds: idBatch, presences: false, nonce: nonce);

                            // 4. 等待完成或超时
                            using (var timeoutCts = new CancellationTokenSource(TimeoutMilliseconds))
                            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                            {
                                using (linkedCts.Token.Register(() =>
                                {
                                    client.GuildMembersChunked -= Handler;
                                    tcs.TrySetCanceled();
                                }))
                                {
                                    await tcs.Task.ConfigureAwait(false);
                                }
                            }

                            // 成功处理，跳出重试循环
                            break;
                        }
                        catch (OperationCanceledException) when (retry < MaxRetries)
                        {
                            // 超时但还有重试机会
                            client.GuildMembersChunked -= Handler; // 确保移除事件处理器
                            Console.WriteLine($"[WARNING] RequestMembersAsync timeout for batch (attempt {retry + 1}/{MaxRetries + 1}), retrying...");
                            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); // 重试前等待1秒
                        }
                        catch (OperationCanceledException)
                        {
                            // 最后一次重试也失败了
                            client.GuildMembersChunked -= Handler; // 确保移除事件处理器
                            throw new TimeoutException($"RequestMembersAsync failed after {MaxRetries + 1} attempts (3s timeout each)");
                        }
                    }
                    catch (TimeoutException)
                    {
                        // 重新抛出超时异常
                        errorInfo?.Add(string.Format($"RequestMembersAsync failed after {MaxRetries + 1} attempts"));
                    }
                    catch (Exception ex) when (retry < MaxRetries)
                    {
                        // 其他异常，如果还有重试机会
                        Console.WriteLine($"[ERROR] RequestMembersAsync failed (attempt {retry + 1}/{MaxRetries + 1}): {ex.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); // 重试前等待1秒
                    }
                    catch (Exception ex)
                    {
                        // 最后一次重试也失败了
                        errorInfo?.Add(string.Format($"RequestMembersAsync failed after {MaxRetries + 1} attempts"));
                    }
                }
                // 成功获取数据，处理结果
                foreach (var id in idBatch)
                {
                    if (guild.Members.TryGetValue(id, out var member))
                    {
                        if (result.TryGetValue(id, out var list))
                        {
                            list.AddRange(member.Roles.Where(role =>
                                list.Any(element => element.Name == role.Name) == false));
                        }
                        else
                        {
                            result.Add(id, member.Roles.ToList());
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }

            return result;
        }
        
        /// <summary>
        /// 批量获取用户在多个Guild中的角色信息并计算权重
        /// </summary>
        /// <param name="client">Discord客户端</param>
        /// <param name="validUsers">有效用户集合</param>
        /// <param name="roleWeights">角色权重配置</param>
        /// <param name="errorInfo">收集运行过程中的错误信息</param>
        /// <returns>用户ID到权重的映射</returns>
        private async Task<(Dictionary<ulong, double> userWeights, Dictionary<string, int> roleDistribution)> GetUserWeightsAsync(
            DiscordClient client,
            HashSet<DiscordUser> validUsers,
            Dictionary<string, double> roleWeights,
            List<string>? errorInfo)
        {
            var userWeights = new Dictionary<ulong, double>();
            var roleDistribution = new Dictionary<string, int>();
            
            // 如果没有配置角色权重，所有用户权重都是1.0
            if (roleWeights == null || !roleWeights.Any())
            {
                foreach (var user in validUsers)
                {
                    userWeights[user.Id] = 1.0;
                }
                return (userWeights, roleDistribution);
            }
            
            // 获取所有需要检查的Guild
            var guildIds = Program.Config.Discord.TestGuildIds;
            var userIds = validUsers.Select(u => u.Id).ToList();
            
            Console.WriteLine($"[DEBUG] Fetching member info for {userIds.Count} users across {guildIds.Length} guilds");
            
            // 为每个用户初始化默认权重和角色
            var userHighestRole = new Dictionary<ulong, string>();
            foreach (var userId in userIds)
            {
                userWeights[userId] = 1.0;
                userHighestRole[userId] = "默认"; // 默认角色，权重为1.0
            }
            
            // 遍历每个Guild获取成员信息
            foreach (var guildId in guildIds)
            {
                try
                {
                    var guild = await client.GetGuildAsync(guildId);
                    if (guild == null)
                    {
                        Console.WriteLine($"[WARNING] Could not get guild {guildId}");
                        continue;
                    }
                    
                    // 使用FetchRolesForAllKittensAsync获取成员角色信息
                    var memberRoles = await FetchRolesForAllUsersAsync(client, guild, validUsers);
                    
                    Console.WriteLine($"[DEBUG] Retrieved {memberRoles.Count} members from guild {guild.Name}");
                    
                    // 处理每个有效用户在该Guild中的角色
                    foreach (var userId in userIds)
                    {
                        if (memberRoles.TryGetValue(userId, out var roles))
                        {
                            // 获取该用户在当前Guild中的最高权重和对应角色
                            double maxWeight = 1.0;
                            string maxWeightRole = "默认";
                            foreach (var role in roles)
                            {
                                if (roleWeights.TryGetValue(role.Name, out var weight))
                                {
                                    if (weight > maxWeight)
                                    {
                                        maxWeight = weight;
                                        maxWeightRole = role.Name;
                                    }
                                    var user = validUsers.FirstOrDefault(u => u.Id == userId);
                                    //Console.WriteLine($"[DEBUG] User {user?.Username ?? userId.ToString()} has role {role.Name} with weight {weight} in guild {guild.Name}");
                                }
                            }
                            
                            // 更新用户权重（取所有Guild中的最高权重）
                            if (maxWeight > userWeights[userId])
                            {
                                userWeights[userId] = maxWeight;
                                userHighestRole[userId] = maxWeightRole;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to get members from guild {guildId}: {ex.Message}");
                    errorInfo?.Add($"获取服务器成员信息失败 (ID: {guildId}): {ex.Message}");
                }
            }
            
            // 统计角色分布
            foreach (var roleName in userHighestRole.Values)
            {
                if (!roleDistribution.ContainsKey(roleName))
                {
                    roleDistribution[roleName] = 0;
                }
                roleDistribution[roleName]++;
            }
            
            // 输出最终权重结果
            foreach (var kv in userWeights.Where(w => w.Value > 1.0))
            {
                var user = validUsers.First(u => u.Id == kv.Key);
                var role = userHighestRole[kv.Key];
                //Console.WriteLine($"[DEBUG] User {user.Username} final weight: {kv.Value} (role: {role})");
            }
            
            return (userWeights, roleDistribution);
        }
        
        /// <summary>
        /// 构建输出文本，处理分组频道的聚合显示
        /// </summary>
        private string BuildOutputText(AnalyzeContext context)
        {
            var sb = new StringBuilder();
            var processedChannels = new HashSet<ulong>();
            
            // 收集所有需要显示的项目及其有效评价数量
            var itemsToDisplay = new List<ReactionSummary>();
            
            foreach (var kv in context.CombinedTop)
            {
                var id = kv.Key;
                
                // 跳过已作为分组一部分处理过的频道
                if (processedChannels.Contains(id))
                    continue;
                
                // 检查该频道是否是某个分组的主频道
                var groupPrimary = context.ChannelGroups.FirstOrDefault(g => g.Value.Contains(id)).Key;
                if (groupPrimary != 0)
                {
                    // 处理分组项目 - 聚合所有成员的结果
                    var groupIds = context.ChannelGroups[groupPrimary];
                    var primaryName = context.PrimaryChannelNames[groupPrimary];
                    
                    // 收集分组内所有频道的反应数据
                    var totalReactions = 0;
                    DiscordEmoji topEmoji = null;
                    
                    // 统计分组内的唯一有效用户和加权分数
                    var uniqueEffectiveUsers = new HashSet<ulong>();
                    var uniqueUsers = new HashSet<ulong>();
                    double groupWeightedScore = 0.0; // 分组的加权总分
                    
                    foreach (var groupId in groupIds)
                    {
                        if (context.CombinedTop.TryGetValue(groupId, out var groupData))
                        {
                            var (emoji, count, users) = groupData;
                            if (topEmoji == null) topEmoji = emoji;
                            
                            // 统计有效用户
                            foreach (var user in users)
                            {
                                uniqueUsers.Add(user.Id);
                                if (context.UserIds.Contains(user.Id))
                                {
                                    // 如果是第一次遇到这个有效用户，添加其权重
                                    if (uniqueEffectiveUsers.Add(user.Id))
                                    {
                                        // 获取该用户的权重（如果有权重数据）
                                        if (context.UserWeights != null)
                                        {
                                            // 从用户权重映射中获取权重，默认为1.0
                                            var userWeight = context.UserWeights.ContainsKey(user.Id) ? context.UserWeights[user.Id] : 1.0;
                                            groupWeightedScore += userWeight;
                                        }
                                    }
                                }
                            }
                        }
                        processedChannels.Add(groupId);
                    }
                    totalReactions = uniqueUsers.Count;
                    
                    var uniqueEffectiveCount = uniqueEffectiveUsers.Count;
                    var percentage = totalReactions > 0 ? (int)(uniqueEffectiveCount * 100 / totalReactions) : 0;
                    
                    // 如果没有权重数据，加权分数等于有效用户数
                    if (context.UserWeights == null)
                    {
                        groupWeightedScore = uniqueEffectiveCount;
                    }
                    
                    itemsToDisplay.Add(new ReactionSummary
                    {
                        Name = primaryName,
                        Emoji = topEmoji,
                        TotalReactions = totalReactions,
                        EffectiveCount = uniqueEffectiveCount,
                        WeightedCount = groupWeightedScore,
                        Percentage = percentage
                    });
                }
                else
                {
                    // 处理单个频道
                    var channel = context.ChannelMsgs[id];
                    var (e, tot, us) = kv.Value;
                    var eff = context.EffectiveCounts[id];
                    var percentage = tot > 0 ? (int)(eff * 100 / tot) : 0;
                    
                    // 计算单个频道的加权分数
                    double channelWeightedScore = eff;
                    if (context.UserWeights != null)
                    {
                        channelWeightedScore = 0.0;
                        foreach (var user in us)
                        {
                            if (context.UserIds.Contains(user.Id))
                            {
                                var userWeight = context.UserWeights.ContainsKey(user.Id) ? context.UserWeights[user.Id] : 1.0;
                                channelWeightedScore += userWeight;
                            }
                        }
                    }
                    
                    itemsToDisplay.Add(new ReactionSummary
                    {
                        Name = channel.Name,
                        Emoji = e,
                        TotalReactions = tot,
                        EffectiveCount = eff,
                        WeightedCount = channelWeightedScore,
                        Percentage = percentage
                    });
                    processedChannels.Add(id);
                }
            }
            
            // 按有效评价数量从多到少排序（如果有权重则按权重排序）
            var sortedItems = context.UserWeights != null 
                ? itemsToDisplay.OrderByDescending(item => item.WeightedCount)
                : itemsToDisplay.OrderByDescending(item => item.EffectiveCount);
            
            // 构建输出文本
            foreach (var item in sortedItems)
            {
                sb.AppendLineCrlf($"• 帖子 '{item.Name}'");
                // 如果有权重且权重不等于人数，显示加权分数
                if (context.UserWeights != null && Math.Abs(item.WeightedCount - item.EffectiveCount) > 0.01)
                {
                    sb.AppendLineCrlf(
                        $"最高表情 {item.Emoji} × {item.TotalReactions}，有效评价 {item.EffectiveCount} 人，加权后 {item.WeightedCount:F2} 分，比例 [{item.Percentage}%]");
                }
                else
                {
                    sb.AppendLineCrlf(
                        $"最高表情 {item.Emoji} × {item.TotalReactions}，有效评价 {item.EffectiveCount} 人 , 比例 [{item.Percentage}%]");
                }
            }
            
            // 如果有角色分布信息，显示角色统计
            if (context.RoleDistribution != null && context.RoleDistribution.Any())
            {
                sb.AppendLineCrlf("======");
                sb.AppendLineCrlf("角色权重分布");
                foreach (var kv in context.RoleDistribution.OrderByDescending(r => r.Value))
                {
                    sb.AppendLineCrlf($"• {kv.Key}: {kv.Value} 人");
                }
            }
            
            // 如果需要输出有效用户列表
            if (context.OutputUsers)
            {
                sb.AppendLineCrlf("======");
                sb.AppendLineCrlf("有效用户");
                foreach (var discordUser in context.ValidUsers)
                {
                    sb.AppendLineCrlf($"• '{discordUser.Username}', {discordUser.Presence}");
                }
            }
            
            // 如果有错误信息，显示在最后
            if (context.ErrorInfo != null && context.ErrorInfo.Any())
            {
                sb.AppendLineCrlf("======");
                sb.AppendLineCrlf("⚠️ 处理过程中遇到的错误：");
                foreach (var error in context.ErrorInfo)
                {
                    sb.AppendLineCrlf($"• {error}");
                }
            }
            
            return sb.ToString();
        }
        
        [SlashCommand("analyze", "统计论坛频道或指定主题帖的最高反应与有效用户数量（带调试日志）")]
        public async Task AnalyzeAsync(
            InteractionContext ctx,
            [Option("urls", "逗号分隔的频道链接，如 https://discord.com/channels/服务器ID/频道ID；或是汇总的帖子链接)")] string urls,
            [Option("min_votes", "有效投票所需的最少投票作品数。")] long minVotes = 3,
            [Option("output_users", "是否输出所有有效投票用户,默认否")] bool outputUsers = false,
            [Option("global_visible", "是否可以被自己以外的用户看到")] bool globalVisible = false,
            [Option("valid_reactions", "逗号分隔的有效反应列表，如 :thumbsup:,:heart:,:star:")] string validReactions = "",
            [Option("role_weight", "角色权重JSON，如 {\"创作者\": 1.25, \"尖耳朵\": 2, \"已验证\": 1}")] string roleWeight = "")
            // roleWeight参数说明:
            // - 允许为不同角色设置投票权重，用于加权计算投票结果
            // - 格式为JSON对象，键为角色名称，值为权重数值
            // - 示例: {"创作者": 1.25, "尖耳朵": 2, "已验证": 1}
            //   - 创作者角色的票数会乘以1.25
            //   - 尖耳朵角色的票数会乘以2
            //   - 已验证角色的票数保持原值（乘以1）
            // - 如果用户拥有多个角色，将使用最高权重
            // - 未指定的角色默认权重为1.0
        {
            _watch.Restart();
                      
            // 发送处理中的响应
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource,
                new DiscordInteractionResponseBuilder()
                    .WithContent("⏳ 正在统计，请稍后…")
                    .AsEphemeral(!globalVisible));
            
            
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
  
            
            // 步骤0：解析和验证角色权重JSON（如果提供）
            Dictionary<string, double> roleWeights = null;
            if (!string.IsNullOrWhiteSpace(roleWeight))
            {
                try
                {
                    // 解析JSON格式的角色权重配置
                    roleWeights = JsonSerializer.Deserialize<Dictionary<string, double>>(roleWeight);
                    if (roleWeights != null && roleWeights.Any())
                    {
                        Console.WriteLine($"[DEBUG] Parsed role weights: {string.Join(", ", roleWeights.Select(kv => $"{kv.Key}={kv.Value}"))}");
                        
                        // 验证权重值的合理性（应该大于0）
                        var invalidWeights = roleWeights.Where(kv => kv.Value <= 0).ToList();
                        if (invalidWeights.Any())
                        {
                            var errorMsg = $"⚠️ 角色权重值必须大于0，错误的权重：{string.Join(", ", invalidWeights.Select(kv => $"{kv.Key}={kv.Value}"))}";
                            await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent(errorMsg));
                            return;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[ERROR] Failed to parse role weights JSON: {ex.Message}");
                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"⚠️ 角色权重JSON格式错误：{ex.Message}"));
                    return;
                }
            }
            
            await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"⏳ 评价收集中..."));

            // 初始化错误信息列表
            var errorInfo = new List<string>();

            // 步骤1：解析URL并获取频道信息
            var (channelMsgs, channelGroups, primaryChannelNames) = await ParseUrlsAsync(urls, ctx.Client, errorInfo);

            if (!channelMsgs.Any())
            {
                Console.WriteLine("[DEBUG] No valid channels or topics found. Exiting.");
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("⚠️ 未解析到有效的频道或主题链接。"));
                return;
            }
            await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"⏳ 已获取 {channelMsgs.Count} 个参赛作品..."));

            // 步骤2：收集所有频道的反应数据
            var analyzer = new ReactionAnalyzer(ctx.Client);
            var combinedTop = await GatherReactionsAsync(channelMsgs, analyzer, validReactions, errorInfo);
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

            // 步骤5.5：计算用户权重（如果提供了roleWeight参数）
            Dictionary<ulong, double> userWeights = null;
            Dictionary<string, int> roleDistribution = null;
            if (roleWeights != null && roleWeights.Any())
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"⏳ 正在获取用户角色信息..."));
                var (weights, distribution) = await GetUserWeightsAsync(ctx.Client, validUsers, roleWeights, errorInfo);
                userWeights = weights;
                roleDistribution = distribution;
                
                // 输出有特殊权重的用户数量
                var usersWithWeights = userWeights.Count(w => w.Value > 1.0);
                if (usersWithWeights > 0)
                {
                    Console.WriteLine($"[DEBUG] Found {usersWithWeights} users with weights > 1.0");
                }
            }

            // 步骤6：构建输出文本
            var analyzeContext = new AnalyzeContext
            {
                CombinedTop = combinedTop,
                ChannelMsgs = channelMsgs,
                ChannelGroups = channelGroups,
                PrimaryChannelNames = primaryChannelNames,
                EffectiveCounts = effectiveCounts,
                UserIds = userIds,
                ValidUsers = validUsers,
                OutputUsers = outputUsers,
                UserWeights = userWeights,
                RoleDistribution = roleDistribution,
                ErrorInfo = errorInfo
            };
            var resultText = BuildOutputText(analyzeContext);
            
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
                        .AsEphemeral(!globalVisible)); 
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