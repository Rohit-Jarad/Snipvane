using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Snipvane.Data;
using Snipvane.Options;
using Snipvane.Services.Clips;
using Snipvane.Services.FFmpeg;
using Snipvane.Services.Highlights;
using Snipvane.Services.Pipeline;
using Snipvane.Services.Storage;
using Snipvane.Services.Subtitles;
using Snipvane.Services.Transcription;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
ApplyApiKeyFallbacks(builder.Configuration);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024;
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024;
    options.ValueLengthLimit = int.MaxValue;
});

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<FFmpegOptions>(builder.Configuration.GetSection(FFmpegOptions.SectionName));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection(OpenAIOptions.SectionName));
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection(PipelineOptions.SectionName));

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var (isPostgres, connectionString) = DatabaseProvider.Resolve(builder.Configuration);

// SQL Server locally; Postgres on Render via DATABASE_URL.
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (isPostgres)
    {
        options.UseNpgsql(connectionString);
    }
    else
    {
        options.UseSqlServer(connectionString);
    }
});

builder.Services.AddHttpClient("openai", client =>
{
    client.BaseAddress = new Uri("https://api.openai.com/v1/");
    client.Timeout = TimeSpan.FromMinutes(15);
});
builder.Services.AddHttpClient("gemini", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddHttpClient("claude", client =>
{
    client.Timeout = TimeSpan.FromMinutes(3);
});

builder.Services.AddSingleton<IMediaStorage, LocalMediaStorage>();
builder.Services.AddSingleton<IPipelineQueue, PipelineQueue>();
builder.Services.AddSingleton<ISubtitleService, AssSubtitleService>();
builder.Services.AddSingleton<IFFmpegService, FFmpegService>();
builder.Services.AddScoped<WhisperTranscriptionService>();
builder.Services.AddScoped<GeminiTranscriptionService>();
builder.Services.AddScoped<ITranscriptionService, CompositeTranscriptionService>();
builder.Services.AddScoped<IHighlightAnalyzer, HighlightDetectionService>();
builder.Services.AddScoped<IClipGenerationService, ClipGenerationService>();
builder.Services.AddScoped<IVideoPipeline, VideoPipelineService>();
builder.Services.AddHostedService<PipelineBackgroundService>();

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? ["http://localhost:5173"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
        policy.WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (isPostgres)
    {
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        await db.Database.MigrateAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors("frontend");
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapGet("/api/health", (IOptionsSnapshot<OpenAIOptions> openai, IOptionsSnapshot<AiOptions> ai) =>
{
    var provider = string.IsNullOrWhiteSpace(ai.Value.Provider) ? "Gemini" : ai.Value.Provider.Trim();
    var transcriptionProvider = string.IsNullOrWhiteSpace(ai.Value.TranscriptionProvider)
        ? "Gemini"
        : ai.Value.TranscriptionProvider.Trim();
    var geminiReady = !string.IsNullOrWhiteSpace(ai.Value.GeminiApiKey);
    var highlightsConfigured = provider.Equals("Claude", StringComparison.OrdinalIgnoreCase)
        ? !string.IsNullOrWhiteSpace(ai.Value.ClaudeApiKey)
        : geminiReady;
    var transcriptionConfigured = transcriptionProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
        ? !string.IsNullOrWhiteSpace(openai.Value.ApiKey)
        : geminiReady;

    return Results.Ok(new
    {
        status = "ok",
        name = "Snipvane",
        whisperConfigured = transcriptionConfigured,
        highlightsConfigured,
        highlightProvider = provider,
        transcriptionProvider
    });
});

app.MapFallbackToFile("index.html");
app.Run();

static void ApplyApiKeyFallbacks(ConfigurationManager config)
{
    if (string.IsNullOrWhiteSpace(config["OpenAI:ApiKey"]))
    {
        var key = FirstNonEmpty(config["OPENAI_API_KEY"], Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        if (key is not null)
        {
            config["OpenAI:ApiKey"] = key;
        }
    }

    if (string.IsNullOrWhiteSpace(config["Ai:GeminiApiKey"]))
    {
        var key = FirstNonEmpty(
            config["GEMINI_API_KEY"],
            config["GOOGLE_API_KEY"],
            Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
            Environment.GetEnvironmentVariable("GOOGLE_API_KEY"));
        if (key is not null)
        {
            config["Ai:GeminiApiKey"] = key;
        }
    }

    if (string.IsNullOrWhiteSpace(config["Ai:ClaudeApiKey"]))
    {
        var key = FirstNonEmpty(config["ANTHROPIC_API_KEY"], Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        if (key is not null)
        {
            config["Ai:ClaudeApiKey"] = key;
        }
    }
}

static string? FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
