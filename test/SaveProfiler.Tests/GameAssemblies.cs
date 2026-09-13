using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace SaveProfiler.Tests;

/// <summary>
/// The committed <c>./lib</c> game assemblies are reference-only (refasmer output): their metadata
/// compiles but their method bodies can't execute. Any test that touches a Klei / Unity type at
/// runtime therefore needs a real ONI install, configured through <c>Directory.Build.props.user</c>.
///
/// Deliberately a copy of the gate in <c>BlueprintsIncluded.Tests</c> rather than a shared type:
/// the probe has to run in the assembly whose output directory the game assemblies were copied
/// into, and the two test projects have different csproj copy targets.
/// </summary>
internal static class GameAssemblies
{
    public static readonly bool Available = Probe();

    private static bool Probe()
    {
        try
        {
            Assembly asm = Assembly.Load("Assembly-CSharp-firstpass");
            return !asm.GetCustomAttributes(typeof(ReferenceAssemblyAttribute), inherit: false).Any();
        }
        catch
        {
            // No install configured (offline build): nothing was copied, so the load fails.
            return false;
        }
    }
}

public sealed class RequiresGameInstallFactAttribute : FactAttribute
{
    public RequiresGameInstallFactAttribute()
    {
        if (!GameAssemblies.Available)
            Skip = "Requires a real ONI install; the offline ./lib assemblies are reference-only.";
    }
}

public sealed class RequiresGameInstallTheoryAttribute : TheoryAttribute
{
    public RequiresGameInstallTheoryAttribute()
    {
        if (!GameAssemblies.Available)
            Skip = "Requires a real ONI install; the offline ./lib assemblies are reference-only.";
    }
}
