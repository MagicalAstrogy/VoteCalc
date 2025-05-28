# VoteCalc - Discord 评价统计机器人 🎀

这是悠纪开发的一个用于 Discord 评价统计的小工具。

## 项目简介 📊

这个项目主要是帮助 Discord 服务器统计论坛频道中的反应投票情况的。在组织赛事投票的时候，存在某些用户的刷票情况，以及跨服务器的作品评价统计的问题，这个工具就是为了解决这些问题而生的～

## 主要功能 ✨

- **智能投票统计**：自动获取指定帖子的最高反应表情和投票用户
- **有效用户筛选**：根据最少投票数要求，筛选出真正参与的有效用户
- **分组作品支持**：支持将多个相关帖子视为同一作品进行统计（用 `~` 分隔）
- **排序输出**：结果按有效评价数量从多到少排序，一目了然呢

## 使用方法 🛠️

### 配置文件设置

首先需要创建 `appsettings.json` 配置文件（可以参考 `appsettings.example.json`）：

```json
{
  "Discord": {
    "Token": "你的机器人Token",
    "Proxy": "代理地址（可选）",
    "TestGuildIds": [服务器ID列表],
    "WhitelistUserIds": [白名单用户ID],
    "AllowedRoles": ["允许使用的角色名称"]
  }
}
```

### 斜杠命令使用

在 Discord 中使用 `/analyze` 命令：

```
/analyze urls:<Discord帖子链接> [min_votes:3] [output_users:false]
```

**参数说明**：
- `urls`：逗号分隔的 Discord 帖子链接
  - 支持单个链接：`https://discord.com/channels/服务器ID/频道ID`
  - 支持多个链接：用逗号 `,` 分隔
  - 支持分组作品：用波浪号 `~` 连接同一作品的多个链接
- `min_votes`：有效投票所需的最少投票作品数（默认为3）
- `output_users`：是否输出有效用户列表（默认否）

### 使用示例

```
/analyze urls:https://discord.com/channels/123/456,https://discord.com/channels/123/789
```

分组作品示例（将两个帖子视为同一作品）：
```
/analyze urls:https://discord.com/channels/123/456~https://discord.com/channels/123/789
```

## 技术架构 🏗️

这个项目使用了以下技术栈：
- **C# .NET 8.0**：主要开发语言
- **DSharpPlus**：Discord API 的 .NET 封装库
- **Slash Commands**：现代化的 Discord 命令交互方式

## 部署说明 🚀

### Docker 部署（推荐）

项目包含了完整的 CI/CD 配置：

1. GitHub Actions 会自动构建 Docker 镜像
2. 使用提供的 `votecalc.service` 可以作为系统服务运行

### 手动部署

```bash
# 克隆项目
git clone https://github.com/你的用户名/VoteCalc.git

# 进入项目目录
cd VoteCalc

# 构建项目
dotnet build

# 运行项目
dotnet run --project VoteCalc/VoteCalc.csproj
```
## 注意事项 ⚠️

- 机器人需要有读取消息和反应的权限
- 大量消息统计可能需要一些时间，请耐心等待
- 确保配置文件中的 Token 安全，不要泄露哦

## 贡献指南 💝

如果你想为这个项目做贡献，我会很开心的呢！请遵循以下步骤：

1. Fork 这个仓库
2. 创建你的功能分支 (`git checkout -b feature/AmazingFeature`)
3. 提交你的更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 开启一个 Pull Request

如果有任何问题，可以在 Issues 中提出，我会尽快回复的～

## 许可证 📄

这个项目使用 AGPL 3.0 许可证 - 详情请查看 [LICENSE](LICENSE) 文件

