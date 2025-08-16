using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;

namespace VoteCalc
{
    /// <summary>
    /// 处理复制内容按钮的交互
    /// </summary>
    public class CopyContentHandler
    {
        /// <summary>
        /// 处理复制内容按钮点击事件
        /// 将消息的embed内容用markdown代码块包裹，便于复制
        /// </summary>
        public static async Task HandleCopyContentButton(DiscordClient client, ComponentInteractionCreateEventArgs e)
        {
            // 检查是否是复制内容按钮
            if (!e.Id.StartsWith("copy_content_"))
                return;

            try
            {
                // 延迟响应，表示正在处理
                await e.Interaction.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource, 
                    new DiscordInteractionResponseBuilder().AsEphemeral(true));

                // 获取原消息的Embed
                var sourceMessage = e.Message;
                if (sourceMessage?.Embeds == null || sourceMessage.Embeds.Count == 0)
                {
                    await e.Interaction.EditOriginalResponseAsync(
                        new DiscordWebhookBuilder().WithContent("❌ 源消息过久或不含embed。"));
                    return;
                }

                var sourceEmbed = sourceMessage.Embeds[0];
                var description = sourceEmbed.Description;

                if (string.IsNullOrEmpty(description))
                {
                    await e.Interaction.EditOriginalResponseAsync(
                        new DiscordWebhookBuilder().WithContent("❌ Embed内容为空。"));
                    return;
                }

                // 创建包含代码块的新Embed
                var copyEmbed = new DiscordEmbedBuilder()
                    .WithTitle("📝 复制内容")
                    .WithDescription($"```\n{description}\n```")
                    .WithColor(new DiscordColor("#99FF99")) // 绿色
                    .WithTimestamp(DateTime.UtcNow);

                // 发送包含可复制内容的响应
                await e.Interaction.EditOriginalResponseAsync(
                    new DiscordWebhookBuilder().AddEmbed(copyEmbed));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 处理复制时出错: {ex.Message}");
                
                try
                {
                    await e.Interaction.EditOriginalResponseAsync(
                        new DiscordWebhookBuilder().WithContent("❌ 处理复制时出现错误，请稍后重试。"));
                }
                catch
                {
                    // 如果编辑响应也失败，记录错误
                    Console.WriteLine("[ERROR] Failed to send error response");
                }
            }
        }

        /// <summary>
        /// 创建复制内容按钮
        /// </summary>
        /// <param name="buttonId">按钮的唯一标识符</param>
        /// <returns>Discord按钮组件</returns>
        public static DiscordButtonComponent CreateCopyButton(string buttonId = null)
        {
            // 如果没有提供ID，生成一个带时间戳的ID
            if (string.IsNullOrEmpty(buttonId))
            {
                buttonId = $"copy_content_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            }
            else if (!buttonId.StartsWith("copy_content_"))
            {
                buttonId = $"copy_content_{buttonId}";
            }

            return new DiscordButtonComponent(
                ButtonStyle.Secondary,  // 灰色按钮
                buttonId,
                "复制消息内容",
                emoji: new DiscordComponentEmoji("📋"));
        }
    }
}