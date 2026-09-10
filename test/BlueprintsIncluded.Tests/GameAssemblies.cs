using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace BlueprintsIncluded.Tests;

	/// <summary>
	/// The committed <c>./lib</c> game assemblies are reference-only (refasmer output):
	/// their metadata compiles but their method bodies can't execute. Any test that
	/// touches a Klei / Unity type at runtime therefore needs a real ONI install,
	/// configured through <c>Directory.Build.props.user</c>.
	///
	/// The probe needs those assemblies to be <em>loadable from the test output</em>, which they
	/// are not by default: every game <c>&lt;Reference&gt;</c> in <c>Directory.Build.props</c> is
	/// <c>&lt;Private&gt;False&lt;/Private&gt;</c> - right for the mod, which must not ship copies
	/// of what ONI already provides. <c>CopyGameAssembliesForRuntime</c> in this project's csproj
	/// copies them for non-offline builds; without it <see cref="Assembly.Load"/> always threw and
	/// every gated test skipped even with an install configured.
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
