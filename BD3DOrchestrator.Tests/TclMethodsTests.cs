using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class TclMethodsTests
{
    [Test]
    public async Task Quote_EscapesSpecialTclCharacters()
    {
        string quoted = TclMethods.Quote(@"a$b[c]""\");

        await Assert.That(quoted).IsEqualTo("\"a\\$b\\[c\\]\\\"\\\\\"");
    }
}
