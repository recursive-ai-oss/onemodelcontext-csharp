namespace RecursiveAI.OneModelContext.Protocol;

/// <summary>
/// Diagnostic IDs and messages for the features the 2026-07-28 revision deprecates (SEP-2577). The text carries the revision's own migration
/// advice from its deprecated features registry. The features stay fully implemented for as long as a negotiated revision carries them.
/// </summary>
internal static class DeprecatedFeatures
{
  /// <summary>Roots.</summary>
  internal const string RootsId = "OMC0001";

  /// <summary>Sampling.</summary>
  internal const string SamplingId = "OMC0002";

  /// <summary>Logging.</summary>
  internal const string LoggingId = "OMC0003";

  /// <summary>Dynamic Client Registration.</summary>
  internal const string RegistrationId = "OMC0004";

  internal const string Roots =
    "Roots are deprecated as of protocol version 2026-07-28. Pass directories or files to the server through tool parameters, resource URIs or server configuration instead.";

  internal const string Sampling =
    "Sampling is deprecated as of protocol version 2026-07-28. Integrate with LLM provider APIs directly instead of asking the client's model.";

  internal const string Logging =
    "Logging is deprecated as of protocol version 2026-07-28. Write diagnostics to standard error (stdio servers) or use OpenTelemetry instead.";

  internal const string Registration =
    "Dynamic Client Registration is deprecated as of protocol version 2026-07-28. Use Client ID Metadata Documents or pre-registered clients instead.";
}
