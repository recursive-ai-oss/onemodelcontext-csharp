using RecursiveAI.OneModelContext;

namespace ChatTools;

/// <summary>A tiny weather service.</summary>
public partial class Forecast(string unit = "degrees")
{
  /// <summary>Gives tomorrow's forecast for a city.</summary>
  /// <param name="city">The city's name.</param>
  [Tool(ReadOnly = true)]
  public string GetForecast(string city) => $"Tomorrow in {city}: sunny, 21 {unit}.";
}
