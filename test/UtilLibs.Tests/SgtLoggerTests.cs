using System;
using System.IO;
using UtilLibs;
using Xunit;

namespace UtilLibs.Tests
{
	/// <summary>
	/// Covers the four-value <c>debuglog</c> overload, whose message expression used to parse as
	/// <c>(a.ToString() + b) != null ? ...</c> - an always-true condition that dropped the first
	/// argument and dereferenced the second, so the single-argument call its own default parameters
	/// invite threw a NullReferenceException. Nothing in the repo reaches that overload (every call
	/// site passes a string and binds to <c>debuglog(string, string)</c>), so these tests exist to
	/// keep the latent bug from coming back rather than to cover a live path.
	/// </summary>
	public class SgtLoggerTests
	{
		/// <summary>The logger writes to <see cref="Console"/> rather than returning its message,
		/// so capture stdout for the duration of the call and hand back what it wrote. System.Action
		/// explicitly: a bare Action binds to Klei's game-action enum in this project.</summary>
		private static string CaptureConsole(System.Action action)
		{
			var original = Console.Out;
			using (var captured = new StringWriter())
			{
				Console.SetOut(captured);
				try
				{
					action();
				}
				finally
				{
					Console.SetOut(original);
				}
				return captured.ToString();
			}
		}

		[Fact]
		public void Debuglog_SingleValue_DoesNotThrowAndLogsIt()
		{
			string output = CaptureConsole(() => SgtLogger.debuglog((object)"solo"));

			Assert.Contains("solo", output);
		}

		[Fact]
		public void Debuglog_JoinsEverySuppliedValueWithSpaces()
		{
			string output = CaptureConsole(() => SgtLogger.debuglog("alpha", "beta", "gamma", "delta"));

			Assert.Contains("alpha beta gamma delta", output);
		}

		[Fact]
		public void Debuglog_SkipsOmittedTrailingValues()
		{
			string output = CaptureConsole(() => SgtLogger.debuglog("alpha", 42));

			Assert.Contains("alpha 42", output);
		}

		/// <summary>The casts are load-bearing: <c>debuglog(null, "after")</c> binds to the more
		/// specific <c>debuglog(string, string)</c> overload instead, which would pass this
		/// assertion without ever reaching the code under test (it logs "after" as the assembly
		/// name). Caught exactly that way - the test passed against the unfixed logger.</summary>
		[Fact]
		public void Debuglog_ToleratesANullFirstValue()
		{
			string output = CaptureConsole(() => SgtLogger.debuglog((object)null, (object)"after"));

			Assert.Contains("after", output);
		}
	}
}
