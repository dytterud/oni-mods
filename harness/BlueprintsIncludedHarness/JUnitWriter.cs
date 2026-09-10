using System.Security;
using System.Text;

namespace BlueprintsV2.Harness;

/// <summary>Writes a minimal JUnit XML report the launcher can parse.</summary>
internal static class JUnitWriter
{
    public static void Write(string path, ResultSet results)
    {
        int failures = results.Count - results.PassCount;
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<testsuite name=\"BlueprintsIncludedHarness\" tests=\"{results.Count}\" failures=\"{failures}\">");
        foreach (var r in results.Items)
        {
            if (r.Passed)
            {
                sb.AppendLine($"  <testcase name=\"{X(r.Name)}\" />");
            }
            else
            {
                sb.AppendLine($"  <testcase name=\"{X(r.Name)}\">");
                sb.AppendLine($"    <failure message=\"assertion failed\">{X(r.Message ?? "")}</failure>");
                sb.AppendLine("  </testcase>");
            }
        }
        sb.AppendLine("</testsuite>");
        System.IO.File.WriteAllText(path, sb.ToString());
    }

    private static string X(string s) => SecurityElement.Escape(s);
}
