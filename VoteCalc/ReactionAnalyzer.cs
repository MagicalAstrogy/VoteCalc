using DSharpPlus;
using DSharpPlus.Entities;

namespace VoteCalc;

public class ReactionAnalyzer
{
    private readonly DiscordClient _client;

    public ReactionAnalyzer(DiscordClient client)
    {
        _client = client;
    }

    public async Task<Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)>> PurrGatherTopReactionsAsync(
        DiscordChannel channel,
        IEnumerable<ulong> messageIds,
        string validReactions = "")
    {
        var result = new Dictionary<ulong, (DiscordEmoji, int, List<DiscordUser>)>();
        
        // Parse valid reactions if provided
        var validReactionsList = string.IsNullOrWhiteSpace(validReactions) 
            ? new List<string>() 
            : validReactions.Split(',').Select(r => r.Trim()).Where(r => !string.IsNullOrEmpty(r)).ToList();
        
        foreach (var msgId in messageIds)
        {
            try
            {
                Console.WriteLine($"[DEBUG] Retrieving message {msgId} in channel {channel.Id}");
                var message = await channel.GetMessageAsync(msgId);
                
                // Filter reactions based on validReactions list
                var filteredReactions = message.Reactions;
                if (validReactionsList.Any())
                {
                    filteredReactions = message.Reactions
                        .Where(r => validReactionsList.Contains(r.Emoji.ToString()))
                        .ToList();
                }
                
                var topReaction = filteredReactions.OrderByDescending(r => r.Count).FirstOrDefault();
                
                if (topReaction == null)
                {
                    Console.WriteLine($"[DEBUG] No valid reactions for message {msgId}");
                    
                    // If validReactions is specified and no valid reactions found, use first valid reaction with count 0
                    if (validReactionsList.Any())
                    {
                        var defaultEmoji = DiscordEmoji.FromName(_client, validReactionsList.First());
                        result[msgId] = (defaultEmoji, 0, new List<DiscordUser>());
                    }
                    else
                    {
                        // Default to sunflower with count 0 if no validReactions specified
                        var sunflowerEmoji = DiscordEmoji.FromName(_client, ":sunflower:");
                        result[msgId] = (sunflowerEmoji, 0, new List<DiscordUser>());
                    }
                    continue;
                }
                
                Console.WriteLine($"[DEBUG] Top reaction for {msgId}: {topReaction.Emoji} x {topReaction.Count}");
                var users = (await message.GetReactionsAsync(topReaction.Emoji, 4096)).ToList();
                Console.WriteLine($"[DEBUG] Users retrieved for reaction: {users.Count}");
                result[msgId] = (topReaction.Emoji, topReaction.Count, users);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Error gathering for {msgId}: {ex.Message}");
            }
        }
        return result;
    }

    class ReactionsCountInfo
    {
        public required DiscordUser User;
        public required int ReactionCount;
    }
    public HashSet<DiscordUser> MeowFilterValidUsers(
        Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)> topReactions,
        int minMessagesReacted = 4)
    {
        var userReactionCounts = new Dictionary<ulong, ReactionsCountInfo>();
        foreach (var kv in topReactions)
        {
            foreach (var user in kv.Value.Users.Distinct())
            {
                userReactionCounts.TryAdd(user.Id, new ReactionsCountInfo(){User = user, ReactionCount = 0});
                userReactionCounts[user.Id].ReactionCount++;
                Console.WriteLine($"[DEBUG] User {user.Username} reacted to {kv.Key}, total {userReactionCounts[user.Id].ReactionCount}");
            }
        }
        var valid = userReactionCounts.Where(kv => kv.Value.ReactionCount >= minMessagesReacted)
            .Select(kv => kv.Value.User)
            .ToHashSet();
        return valid;
    }

    public Dictionary<ulong, int> NapCountEffectiveReactions(
        Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)> topReactions,
        HashSet<ulong> validUsers)
    {
        var counts = new Dictionary<ulong, int>();
        foreach (var kv in topReactions)
        {
            var count = kv.Value.Users.Select(u => u.Id).Intersect(validUsers).Count();
            Console.WriteLine($"[DEBUG] Effective for {kv.Key}: {count}");
            counts[kv.Key] = count;
        }
        return counts;
    }
    
    public Dictionary<ulong, double> NapCountWeightedEffectiveReactions(
        Dictionary<ulong, (DiscordEmoji TopEmoji, int Count, List<DiscordUser> Users)> topReactions,
        HashSet<ulong> validUsers,
        Dictionary<ulong, double> userWeights)
    {
        var counts = new Dictionary<ulong, double>();
        foreach (var kv in topReactions)
        {
            double weightedCount = 0;
            foreach (var user in kv.Value.Users)
            {
                if (validUsers.Contains(user.Id))
                {
                    var weight = userWeights.ContainsKey(user.Id) ? userWeights[user.Id] : 1.0;
                    weightedCount += weight;
                }
            }
            Console.WriteLine($"[DEBUG] Weighted effective for {kv.Key}: {weightedCount:F2}");
            counts[kv.Key] = weightedCount;
        }
        return counts;
    }
}