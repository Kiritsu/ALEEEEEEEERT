using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.EventArgs;
using DSharpPlus.VoiceNext;
using Newtonsoft.Json;

namespace Example
{
    public sealed class Program
    {
        public static async Task Main()
        {
            var json = "";
            using (var fs = File.OpenRead("config.json"))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false)))
            {
                json = await sr.ReadToEndAsync();
            }

            var jsonConfig = JsonConvert.DeserializeObject<BotConfig>(json);

            var client = new DiscordClient(new DiscordConfiguration
            {
                Token = Environment.GetEnvironmentVariable("ALERTBOT_TOKEN"),
                TokenType = TokenType.Bot,
                LogLevel = LogLevel.Debug,
                UseInternalLogHandler = true
            });

            client.Ready += OnClientReady;

            var cnext = client.UseCommandsNext(new CommandsNextConfiguration
            {
                CaseSensitive = false,
                StringPrefixes = jsonConfig.Prefixes
            });

            cnext.RegisterCommands<BasicCommands>();

            cnext.CommandExecuted += OnCommandExecuted;
            cnext.CommandErrored += OnCommandErrored;

            var vnext = client.UseVoiceNext();

            await client.ConnectAsync();

            _ = Task.Run(async () =>
            {
                var musicService = new MusicService(client, jsonConfig);
                await musicService.StartAsync();
            });

            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        private static Task OnCommandErrored(CommandErrorEventArgs e)
        {
            e.Context.Client.DebugLogger.LogMessage(LogLevel.Error, "ExampleBot", $"{e.Context.User.Username} tried executing '{e.Command?.QualifiedName ?? "<unknown command>"}' but it errored: {e.Exception.GetType()}: {e.Exception.Message ?? "<no message>"}", DateTime.Now);
            return Task.CompletedTask;
        }

        private static Task OnCommandExecuted(CommandExecutionEventArgs e)
        {
            e.Context.Client.DebugLogger.LogMessage(LogLevel.Info, "AlertBot", $"{e.Context.User.Username} successfully executed '{e.Command.QualifiedName}'", DateTime.Now);
            return Task.CompletedTask;
        }

        private static Task OnClientReady(ReadyEventArgs e)
        {
            e.Client.DebugLogger.LogMessage(LogLevel.Info, "AlertBot", "Bot connected!", DateTime.Now);
            return Task.CompletedTask;
        }
    }

    public class MusicService
    {
        private readonly DiscordClient _client;
        private readonly VoiceNextExtension _vnext;
        private readonly BotConfig _config;

        public MusicService(DiscordClient client, BotConfig config)
        {
            _client = client;
            _config = config;
            _vnext = _client.GetVoiceNext();
        }

        public async Task StartAsync()
        { 
            var timeToWait = TimeSpan.FromMinutes(60 - DateTime.Now.Minute - 1) + TimeSpan.FromSeconds(60 - DateTime.Now.Second);

            do
            {
                try
                {
                    _client.DebugLogger.LogMessage(LogLevel.Info, "MusicService", $"Ready to wait {timeToWait} minutes", DateTime.Now);

                    await Task.Delay(timeToWait);
                    timeToWait = TimeSpan.FromMinutes(_config.IntervalMinutes) - TimeSpan.FromSeconds(20);

                    var guild = _client.Guilds[_config.GuildId];

                    if (!(_config.CustomMessage is null))
                    {
                        var txtChannel = guild.Channels[_config.TextChannelId];
                        await txtChannel.SendMessageAsync(_config.CustomMessage);
                    }

                    var channel = guild.Channels[_config.VoiceChannelId];
                    var vnc = await _vnext.ConnectAsync(channel);

                    try
                    {
                        await vnc.SendSpeakingAsync(true);

                        var psi = new ProcessStartInfo
                        {
                            FileName = "ffmpeg.exe",
                            Arguments = $@"-i ""{_config.FileName}"" -ac 2 -f s16le -ar 48000 pipe:1",
                            RedirectStandardOutput = true,
                            UseShellExecute = false
                        };
                        var ffmpeg = Process.Start(psi);
                        var ffout = ffmpeg.StandardOutput.BaseStream;

                        var txStream = vnc.GetTransmitStream();
                        await ffout.CopyToAsync(txStream);
                        await txStream.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        _client.DebugLogger.LogMessage(LogLevel.Error, "MusicService", "An error occured", DateTime.Now, ex);
                    }
                    finally
                    {
                        await vnc.SendSpeakingAsync(false);
                        await Task.Delay(TimeSpan.FromSeconds(20));

                        vnc.Disconnect();
                    }
                }
                catch (Exception e)
                {
                    _client.DebugLogger.LogMessage(LogLevel.Error, "MusicService", "An error occured inner catch", DateTime.Now, e);
                }
            } while (true);
        }
    }

    public class BotConfig
    {
        [JsonProperty("alert_interval")]
        public int IntervalMinutes { get; set; }

        [JsonProperty("custom_message")]
        public string CustomMessage { get; set; }

        [JsonProperty("prefixes")]
        public string[] Prefixes { get; set; }

        [JsonProperty("voice_channel_id")]
        public ulong VoiceChannelId { get; set; }

        [JsonProperty("text_channel_id")]
        public ulong TextChannelId { get; set; }

        [JsonProperty("guild_id")]
        public ulong GuildId { get; set; }

        [JsonProperty("file_name")]
        public string FileName { get; set; }
    }

    public class BasicCommands : BaseCommandModule
    {
        [Command("Ping")]
        public async Task PingAsync(CommandContext ctx)
        {
            await ctx.RespondAsync($":ping_pong: | {ctx.Client.Ping} ms.");
        }
    }
}