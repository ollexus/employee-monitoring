using System.Text;

namespace EmployeeMonitoring.Server.Monitoring;

/// <summary>
/// Необязательная HTTP-аутентификация панели оператора (Basic).
/// Включается параметром Dashboard:RequireAuthorization.
/// </summary>
public sealed class BasicAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DashboardOptions _options;
    private readonly ILogger<BasicAuthorizationMiddleware> _logger;

    public BasicAuthorizationMiddleware(RequestDelegate next, DashboardOptions options, ILogger<BasicAuthorizationMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.RequireAuthorization || context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string? header = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            }
            catch (FormatException)
            {
                decoded = string.Empty;
            }

            int separator = decoded.IndexOf(':');
            string user = separator >= 0 ? decoded[..separator] : decoded;
            string password = separator >= 0 ? decoded[(separator + 1)..] : string.Empty;

            if (string.Equals(user, _options.UserName, StringComparison.Ordinal) &&
                string.Equals(password, _options.Password, StringComparison.Ordinal))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            _logger.LogWarning("Неудачная попытка входа в панель из {Remote}", context.Connection.RemoteIpAddress);
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        // Значение заголовка передаётся в кодировке latin-1, поэтому realm содержит только ASCII.
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Monitoring Dashboard\", charset=\"UTF-8\"";
        await context.Response.WriteAsync("Требуется авторизация.").ConfigureAwait(false);
    }
}
