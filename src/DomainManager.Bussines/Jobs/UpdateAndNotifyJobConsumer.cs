using System.Diagnostics;
using DomainManager.Requests;
using MassTransit;
using MassTransit.Mediator;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Whois;

namespace DomainManager.Jobs;

public class UpdateAndNotifyJobConsumer : IConsumer<UpdateAndNotifyJob> {
    private readonly ITelegramBotClient _botClient;
    private readonly ApplicationDbContext _db;
    private readonly TimeSpan _expirationTimeSpan = TimeSpan.FromDays(10);
    private readonly ILogger<UpdateAndNotifyJobConsumer> _logger;
    private readonly IScopedMediator _mediator;
    private readonly IWhoisLookup _whoisLookup;

    public UpdateAndNotifyJobConsumer(ILogger<UpdateAndNotifyJobConsumer> logger, IScopedMediator mediator,
        ApplicationDbContext db, IWhoisLookup whoisLookup, ITelegramBotClient botClient) {
        _logger = logger;
        _mediator = mediator;
        _db = db;
        _whoisLookup = whoisLookup;
        _botClient = botClient;
    }

    public async Task Consume(ConsumeContext<UpdateAndNotifyJob> context) {
        var chatId = context.Message.ChatId;
        _logger.LogDebug("Updating by {ChatId}..", chatId);

        var cancellationToken = context.CancellationToken;

        await UpdateDomainMonitors(chatId, cancellationToken);
        await UpdateSslMonitors(chatId, cancellationToken);

        await NotifyDomainMonitors(chatId, cancellationToken);
        await NotifySslMonitors(chatId, cancellationToken);

        _logger.LogDebug("Updating by {ChatId}.. Done", chatId);
    }

    private async Task NotifySslMonitors(long chatId, CancellationToken cancellationToken) {
        var almostExpiredMonitors = await _db.SslMonitorByChat
            .Where(m => m.ChatId == chatId)
            .Select(m => m.SslMonitor)
            .Where(m => m.NotAfter - DateTime.UtcNow <= _expirationTimeSpan)
            .OrderBy(m => m.NotAfter)
            .Select(d => $"{d.NotAfter,12:s} {d.Host}")
            .ToArrayAsync(cancellationToken);
        if (almostExpiredMonitors.Length == 0) {
            return;
        }
        var text = "Almost expired SSL certificate list:\n" +
                   "```\n" +
                   "Expired on   | Host\n" +
                   string.Join('\n', almostExpiredMonitors) +
                   "```";
        await SendNotification(chatId, text, cancellationToken);
    }

    private async Task NotifyDomainMonitors(long chatId, CancellationToken cancellationToken) {
        var almostExpiredMonitors = await _db.DomainMonitorByChat
            .Where(m => m.ChatId == chatId)
            .Select(m => m.DomainMonitor)
            .Where(m => m.ExpirationDate - DateTime.UtcNow <= _expirationTimeSpan)
            .OrderBy(m => m.ExpirationDate)
            .Select(d => $"{d.ExpirationDate,12:d} {d.Domain}")
            .ToArrayAsync(cancellationToken);

        if (almostExpiredMonitors.Length == 0) {
            return;
        }
        var text = "Almost expired domain list:\n" +
                   "```\n" +
                   "Expired on   | Domain\n" +
                   string.Join('\n', almostExpiredMonitors) +
                   "```";
        await SendNotification(chatId, text, cancellationToken);
    }

    private async Task SendNotification(long chatId, string text, CancellationToken cancellationToken) {
        try {
            await _botClient.SendTextMessageAsync(
                chatId,
                text,
                ParseMode.Markdown,
                cancellationToken: cancellationToken
            );
        } catch (Exception e) {
            _logger.LogError(e, "Something went wrong with notification");
        }
    }

    private async Task UpdateDomainMonitors(long chatId, CancellationToken cancellationToken) {
        var monitors = await _db.DomainMonitorByChat.Where(db => db.ChatId == chatId)
            .Select(dm => dm.DomainMonitor).ToArrayAsync(cancellationToken);
        foreach (var monitor in monitors) {
            _logger.LogDebug("Updating by {ChatId} Domain: {Domain}..", chatId, monitor.Domain);
            try {
                var whois = await _whoisLookup.LookupAsync(monitor.Domain);
                monitor.LastUpdateDate = DateTime.UtcNow;
                monitor.ExpirationDate = whois.Expiration?.ToUniversalTime();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Failed to update domain monitor {ChatId} {Domain}", chatId, monitor.Domain);
            }
            _logger.LogDebug("Updating by {ChatId} Domain: {Domain}.. Done", chatId, monitor.Domain);
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateSslMonitors(long chatId, CancellationToken cancellationToken) {
        var monitors = await _db.SslMonitorByChat.Where(m => m.ChatId == chatId).Select(m => m.SslMonitor)
            .ToArrayAsync(cancellationToken);
        foreach (var monitor in monitors) {
            _logger.LogDebug("Updating by {ChatId} Host: {Host}.. ", chatId, monitor.Host);
            var response = await _mediator
                .CreateRequestClient<GetCertificateInfo>()
                .GetResponse<CertificateInfo, MessageResponse>(new { Hostname = monitor.Host }, cancellationToken);

            if (response.Is(out Response<CertificateInfo>? certInfoResponse)) {
                var certInfo = certInfoResponse.Message;
                monitor.LastUpdateDate = DateTime.UtcNow;
                monitor.Issuer = certInfo.Issuer;
                monitor.NotAfter = certInfo.NotAfter.ToUniversalTime();
                monitor.NotBefore = certInfo.NotBefore.ToUniversalTime();
                monitor.Errors = certInfo.Errors;
                _logger.LogDebug("Updating by {ChatId} Host: {Host}.. Done", chatId, monitor.Host);
                continue;
            }

            if (response.Is(out Response<MessageResponse>? error)) {
                _logger.LogWarning("Failed to update ssl certificate info by host {Host}\n{Error}", monitor.Host,
                    error);
                continue;
            }

            throw new UnreachableException();
        }
        await _db.SaveChangesAsync(cancellationToken);
    }
}