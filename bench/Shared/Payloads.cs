using System.Diagnostics;
using System.Globalization;

namespace Bench;

/// <summary>The fixed work both servers do, so the stacks are compared on identical payloads.</summary>
internal static class Payloads
{
  public const int OneMebibyte = 1024 * 1024;

  /// <summary>1 MiB of ASCII text.</summary>
  public static readonly string Text = new('x', OneMebibyte);

  /// <summary>1 MiB of bytes returned as an inline image; each call base64-encodes it again.</summary>
  public static readonly byte[] Image = CreateImage();

  public const string ImageMimeType = "image/png";

  /// <summary>How many extra tools a server registers for the list row.</summary>
  public static int Filler(string[] args)
  {
    var i = Array.IndexOf(args, "--filler");
    return i >= 0 ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : 0;
  }

  /// <summary>Bytes allocated so far by this process, its peak working set in bytes and its CPU time in microseconds, as one line.</summary>
  public static string Stats()
  {
    using var process = Process.GetCurrentProcess();
    return GC.GetTotalAllocatedBytes(false).ToString(CultureInfo.InvariantCulture) + " " + process.PeakWorkingSet64.ToString(CultureInfo.InvariantCulture) + " " + ((long)(process.TotalProcessorTime.TotalMilliseconds * 1000)).ToString(CultureInfo.InvariantCulture);
  }

  private static byte[] CreateImage()
  {
    var bytes = new byte[OneMebibyte];
    new Random(1).NextBytes(bytes);
    return bytes;
  }
}
