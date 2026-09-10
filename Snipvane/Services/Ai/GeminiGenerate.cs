using System.Net;

namespace Snipvane.Services.Ai;

/// <summary>
/// Posts to Gemini generateContent with overload handling: on 503/429
/// switch to a fallback model first, then retry with backoff.
/// </summary>
public static class GeminiGenerate
{
    public const int MaxRetries = 5;

    public static IReadOnlyList<string> Models(string? primary)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var name in new[]
                 {
                     primary?.Trim(),
                     "gemini-2.5-flash",
                     "gemini-2.5-flash-lite"
                 })
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                list.Add(name);
            }
        }

        return list;
    }

    public static async Task<(string Body, string Model)> PostJsonAsync(
        HttpClient client,
        string apiKey,
        string primaryModel,
        object payload,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? lastResponse = null;
        var lastBody = "";
        var models = Models(primaryModel);

        for (var mi = 0; mi < models.Count; mi++)
        {
            var model = models[mi];
            var url =
                $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}";
            var hasMoreModels = mi < models.Count - 1;

            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                lastResponse = await client.PostAsJsonAsync(url, payload, cancellationToken);
                lastBody = await lastResponse.Content.ReadAsStringAsync(cancellationToken);

                if (lastResponse.IsSuccessStatusCode)
                {
                    if (mi > 0)
                    {
                        logger.LogInformation("Gemini succeeded with fallback model {Model}", model);
                    }

                    return (lastBody, model);
                }

                var code = (int)lastResponse.StatusCode;
                var transient = IsTransient(lastResponse.StatusCode, lastBody);

                if (IsMissingModel(code, lastBody))
                {
                    logger.LogWarning(
                        "Gemini model {Model} is not available ({Status}); trying next model",
                        model, code);
                    break;
                }

                if (!transient)
                {
                    logger.LogError("Gemini API failed ({Status}): {Body}", code, Trim(lastBody));
                    throw new InvalidOperationException($"Gemini API failed ({code}): {Trim(lastBody)}");
                }

                if (attempt == 1 && hasMoreModels)
                {
                    logger.LogWarning(
                        "Gemini model {Model} overloaded ({Status}); switching to fallback",
                        model, code);
                    break;
                }

                if (attempt < MaxRetries)
                {
                    var delay = Delay(attempt);
                    logger.LogWarning(
                        "Gemini {Model} {Status}; retry {Attempt}/{Max} after {Delay}s",
                        model, code, attempt, MaxRetries, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
            }
        }

        var lastCode = lastResponse is null ? 0 : (int)lastResponse.StatusCode;
        logger.LogError("Gemini API failed ({Status}): {Body}", lastCode, Trim(lastBody));
        throw new InvalidOperationException($"Gemini API failed ({lastCode}): {Trim(lastBody)}");
    }

    public static string Trim(string body) =>
        body.Length <= 1500 ? body : body[..1500];

    private static bool IsTransient(HttpStatusCode status, string body)
    {
        var code = (int)status;
        if (code is 429 or 500 or 503)
        {
            return true;
        }

        return body.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
            || body.Contains("high demand", StringComparison.OrdinalIgnoreCase)
            || body.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMissingModel(int statusCode, string body) =>
        statusCode is 404
        || (statusCode == 400 && body.Contains("not found", StringComparison.OrdinalIgnoreCase));

    private static TimeSpan Delay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(45, 6 * Math.Pow(2, attempt - 1)));
}
