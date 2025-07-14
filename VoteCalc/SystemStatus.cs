using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DSharpPlus.SlashCommands;
using DSharpPlus.Entities;
using DSharpPlus;

namespace VoteCalc
{
    public class SystemStatus : ApplicationCommandModule
    {
        [SlashCommand("status", "显示当前系统状态，包括内存占用、CPU使用率等信息")]
        public async Task StatusAsync(InteractionContext ctx)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource,
                new DiscordInteractionResponseBuilder()
                    .WithContent("⏳ 正在获取系统状态信息...")
                    .AsEphemeral(false));

            var statusInfo = GetSystemStatus();
            
            var embed = new DiscordEmbedBuilder()
                .WithTitle("🖥️ 系统状态")
                .WithColor(DiscordColor.Blue)
                .WithTimestamp(DateTime.UtcNow)
                .AddField("操作系统", statusInfo.OperatingSystem, true)
                .AddField("运行时间", statusInfo.Uptime, true)
                .AddField("进程ID", statusInfo.ProcessId.ToString(), true)
                .AddField("CPU 使用率", $"{statusInfo.CpuUsage:F1}%", true)
                .AddField("进程内存", statusInfo.ProcessMemory, true)
                .AddField("总内存使用", statusInfo.TotalMemoryUsage, true)
                .AddField("工作集内存", statusInfo.WorkingSetMemory, true)
                .AddField("GC 内存", statusInfo.GcMemory, true)
                .AddField("线程数", statusInfo.ThreadCount.ToString(), true)
                .AddField(".NET 版本", statusInfo.DotNetVersion, true)
                .AddField("程序版本", statusInfo.AppVersion, true)
                .WithFooter($"服务器时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent("")
                .AddEmbed(embed));
        }

        private SystemStatusInfo GetSystemStatus()
        {
            var process = Process.GetCurrentProcess();
            var statusInfo = new SystemStatusInfo
            {
                ProcessId = process.Id,
                DotNetVersion = RuntimeInformation.FrameworkDescription,
                OperatingSystem = GetOSInfo(),
                AppVersion = GetAppVersion()
            };

            try
            {
                statusInfo.Uptime = FormatTimeSpan(DateTime.Now - process.StartTime);
                
                statusInfo.ProcessMemory = FormatBytes(process.WorkingSet64);
                statusInfo.WorkingSetMemory = FormatBytes(process.WorkingSet64);
                statusInfo.ThreadCount = process.Threads.Count;
                
                long gcMemory = GC.GetTotalMemory(false);
                statusInfo.GcMemory = FormatBytes(gcMemory);
                
                statusInfo.CpuUsage = GetCpuUsage();
                
                statusInfo.TotalMemoryUsage = GetSystemMemoryInfo();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to get some system info: {ex.Message}");
            }

            return statusInfo;
        }

        private string GetOSInfo()
        {
            var os = RuntimeInformation.OSDescription;
            var arch = RuntimeInformation.OSArchitecture;
            return $"{os} ({arch})";
        }

        private string GetAppVersion()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version != null ? version.ToString() : "Unknown";
        }

        private double GetCpuUsage()
        {
            try
            {
                var startTime = DateTime.UtcNow;
                var startCpuUsage = Process.GetCurrentProcess().TotalProcessorTime;
                
                System.Threading.Thread.Sleep(500);
                
                var endTime = DateTime.UtcNow;
                var endCpuUsage = Process.GetCurrentProcess().TotalProcessorTime;
                
                var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
                var totalMsPassed = (endTime - startTime).TotalMilliseconds;
                var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);
                
                return cpuUsageTotal * 100;
            }
            catch
            {
                return -1;
            }
        }

        private string GetSystemMemoryInfo()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var memInfo = System.IO.File.ReadAllLines("/proc/meminfo");
                    var totalLine = memInfo.FirstOrDefault(l => l.StartsWith("MemTotal:"));
                    var availableLine = memInfo.FirstOrDefault(l => l.StartsWith("MemAvailable:"));
                    
                    if (totalLine != null && availableLine != null)
                    {
                        var total = ParseMemInfoLine(totalLine);
                        var available = ParseMemInfoLine(availableLine);
                        var used = total - available;
                        var percentage = (used * 100.0) / total;
                        
                        return $"{FormatBytes(used)} / {FormatBytes(total)} ({percentage:F1}%)";
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var output = ExecuteCommand("wmic", "OS get TotalVisibleMemorySize,FreePhysicalMemory /Value");
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    
                    long total = 0, free = 0;
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("TotalVisibleMemorySize="))
                            total = long.Parse(line.Split('=')[1]) * 1024;
                        else if (line.StartsWith("FreePhysicalMemory="))
                            free = long.Parse(line.Split('=')[1]) * 1024;
                    }
                    
                    if (total > 0)
                    {
                        var used = total - free;
                        var percentage = (used * 100.0) / total;
                        return $"{FormatBytes(used)} / {FormatBytes(total)} ({percentage:F1}%)";
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to get system memory info: {ex.Message}");
            }
            
            return "N/A";
        }

        private long ParseMemInfoLine(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
            {
                return kb * 1024;
            }
            return 0;
        }

        private string ExecuteCommand(string command, string arguments)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalDays >= 1)
            {
                return $"{(int)timeSpan.TotalDays}天 {timeSpan.Hours}小时 {timeSpan.Minutes}分钟";
            }
            else if (timeSpan.TotalHours >= 1)
            {
                return $"{(int)timeSpan.TotalHours}小时 {timeSpan.Minutes}分钟";
            }
            else
            {
                return $"{timeSpan.Minutes}分钟 {timeSpan.Seconds}秒";
            }
        }

        private class SystemStatusInfo
        {
            public string OperatingSystem { get; set; }
            public string Uptime { get; set; }
            public int ProcessId { get; set; }
            public double CpuUsage { get; set; }
            public string ProcessMemory { get; set; }
            public string TotalMemoryUsage { get; set; }
            public string WorkingSetMemory { get; set; }
            public string GcMemory { get; set; }
            public int ThreadCount { get; set; }
            public string DotNetVersion { get; set; }
            public string AppVersion { get; set; }
        }
    }
}