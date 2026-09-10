namespace Snipvane.Services.Pipeline;

public class PipelineBackgroundService : BackgroundService
{
    private readonly IPipelineQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PipelineBackgroundService> _logger;

    public PipelineBackgroundService(
        IPipelineQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<PipelineBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Video pipeline worker started");

        await foreach (var videoId in _queue.ReadAllAsync(stoppingToken))
        {
            _logger.LogInformation("Dequeued video {VideoId} for processing", videoId);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<IVideoPipeline>();
                await pipeline.ProcessAsync(videoId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled pipeline error for video {VideoId}", videoId);
            }
        }

        _logger.LogInformation("Video pipeline worker stopped");
    }
}
