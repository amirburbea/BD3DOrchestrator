using System;
using System.Runtime.CompilerServices;

namespace BD3DOrchestrator.Tests;

internal static class TestRunSettings
{
    /// <summary>
    /// TUnit writes an HTML and a JSON report into ./TestResults on every run, wherever the tests are started from.
    /// Turn both off before TUnit starts. A value already set in the environment (e.g. by CI) is left alone.
    /// </summary>
    [ModuleInitializer]
    internal static void DisableTUnitReports()
    {
        foreach (string variable in (string[])["TUNIT_DISABLE_HTML_REPORTER", "TUNIT_DISABLE_JSON_REPORT"])
        {
            if (Environment.GetEnvironmentVariable(variable) is null)
            {
                Environment.SetEnvironmentVariable(variable, "true");
            }
        }
    }
}
