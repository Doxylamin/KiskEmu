using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// A theory that runs only on request: set AION_LONG_TESTS=1 (it plays every side, region and ascension quest at cap,
/// far longer than the rest of the suite). Keep [Trait("Category", "LongPlaythrough")] on it as well, for filters.
/// </summary>
public sealed class LongPlaythroughTheoryAttribute : TheoryAttribute
{
    public LongPlaythroughTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("AION_LONG_TESTS") != "1")
            Skip = "Long playthrough: set AION_LONG_TESTS=1 to run it.";
    }
}
