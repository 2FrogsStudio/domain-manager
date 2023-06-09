using Microsoft.Extensions.Hosting;
using Telegram.Bot;

namespace DomainManager.Services;

public class BotInit : IHostedService {
    private readonly ITelegramBotClient _botClient;

    public BotInit(ITelegramBotClient botClient) {
        _botClient = botClient;
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        await InitCommands(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }

    private async Task InitCommands(CancellationToken cancellationToken) {
        var commands = CommandHelpers.CommandAttributeByCommand.Values
            .Where(d => d is not null && d.RegisterCommand)
            .Select(d => new BotCommand {
                Command = d!.Text,
                Description = d.Description ?? string.Empty
            });
        await _botClient.SetMyCommandsAsync(commands, cancellationToken: cancellationToken);
    }
}