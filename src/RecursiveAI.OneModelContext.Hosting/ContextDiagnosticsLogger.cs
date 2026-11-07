using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecursiveAI.OneModelContext.Diagnostics;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>
/// Forwards <see cref="ContextDiagnostics"/> events to <see cref="ILogger"/> under the category "RecursiveAI.OneModelContext" while the host runs.
/// Only the event's text and the exception's type and scrubbed message are logged, never the exception object or a stack, and bearer
/// tokens, JWTs and token-like query values are replaced by "[redacted]" before anything is written.
/// </summary>
internal sealed partial class ContextDiagnosticsLogger(ILoggerFactory loggerFactory) : IHostedService
{
  private IDisposable? _subscription;

  public Task StartAsync(CancellationToken cancellationToken)
  {
    var logger = loggerFactory.CreateLogger(ContextDiagnostics.SourceName);
    _subscription = ContextDiagnostics.Subscribe(e => Log(logger, e));
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    Interlocked.Exchange(ref _subscription, null)?.Dispose();
    return Task.CompletedTask;
  }

  internal static void Log(ILogger logger, DiagnosticEvent e)
  {
    var message = Scrub(e.Message);
    var method = e.Method ?? "-";
    var fault = e.Exception is null ? "-" : e.Exception.GetType().Name + ": " + Scrub(e.Exception.Message);
    switch (e.Kind)
    {
      case DiagnosticKind.HandlerFault:
        HandlerFault(logger, message, method, fault);
        break;
      case DiagnosticKind.InvalidMessage:
        InvalidMessage(logger, message, method);
        break;
      case DiagnosticKind.AuditFault:
        AuditFault(logger, message, fault);
        break;
      case DiagnosticKind.AuthRejected:
        AuthRejected(logger, message, method);
        break;
      case DiagnosticKind.Warning:
        Warning(logger, message);
        break;
      default:
        Detail(logger, e.Kind, message);
        break;
    }
  }

  internal static string Scrub(string text)
  {
    text = Bearer().Replace(text, "Bearer [redacted]");
    text = Jwt().Replace(text, "[redacted]");
    return TokenParameter().Replace(text, "$1=[redacted]");
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "{Message} (method {Method}; {Fault})")]
  private static partial void HandlerFault(ILogger logger, string message, string method, string fault);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "{Message} (method {Method})")]
  private static partial void InvalidMessage(ILogger logger, string message, string method);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "{Message} ({Fault})")]
  private static partial void AuditFault(ILogger logger, string message, string fault);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "{Message} (method {Method})")]
  private static partial void AuthRejected(ILogger logger, string message, string method);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "{Message}")]
  private static partial void Warning(ILogger logger, string message);

  [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "{Kind}: {Message}")]
  private static partial void Detail(ILogger logger, DiagnosticKind kind, string message);

  [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=\-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex Bearer();

  [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.?[A-Za-z0-9_\-]*", RegexOptions.CultureInvariant)]
  private static partial Regex Jwt();

  [GeneratedRegex(@"\b(access_token|refresh_token|id_token|client_secret|code|token|assertion)=[^&\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex TokenParameter();
}
