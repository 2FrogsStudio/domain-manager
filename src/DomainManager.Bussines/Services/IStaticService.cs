namespace DomainManager.Services;

public interface IStaticService {
    Task<string?> GetBotUsername(CancellationToken cancellationToken);
}