using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class PathMethodsTests
{
    [Test]
    public async Task SanitizeFileName_StripsIllegalCharsAndTrailingDots()
    {
        string cleaned = PathMethods.SanitizeFileName("  Spider-Man: Into/The*Verse?.  ");

        await Assert.That(cleaned).IsEqualTo("Spider-Man IntoTheVerse");
    }
}
