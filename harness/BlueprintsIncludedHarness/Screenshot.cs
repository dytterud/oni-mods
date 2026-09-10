using System.Collections;
using System.IO;
using UnityEngine;

namespace BlueprintsV2.Harness;

/// <summary>
/// Writes PNG frames into <see cref="HarnessGate.OutputDir"/> so a run can be inspected visually
/// afterwards - the part of a UI change that assertions can't reach (is the button there, does
/// text actually render).
///
/// Not an assertion mechanism: nothing here can fail a case. It produces evidence for a human,
/// or for an agent reading the files back.
/// </summary>
internal static class Screenshot
{
    /// <summary>Subfolder for this run's frames, wiped at the start of each run.</summary>
    public static string Dir { get; } = Path.Combine(HarnessGate.OutputDir, "shots");

    private static int sequence;

    public static void Reset()
    {
        sequence = 0;
        try
        {
            if (Directory.Exists(Dir))
                foreach (var f in Directory.GetFiles(Dir, "*.png"))
                    File.Delete(f);
            else
                Directory.CreateDirectory(Dir);
        }
        catch (IOException e)
        {
            Debug.LogWarning("[BPI-Harness] could not clear screenshot dir: " + e.Message);
        }
    }

    /// <summary>
    /// Captures the full frame to <c>NN-name.png</c>.
    ///
    /// Must be yielded on: the capture reads the backbuffer, so it needs a frame to have finished
    /// rendering. <c>WaitForEndOfFrame</c> alone is not enough when the caller is driven from a
    /// coroutine that itself started mid-frame, so this yields a plain frame first.
    /// </summary>
    public static IEnumerator Capture(string name, HarnessLog? log = null)
    {
        yield return null;
        yield return new WaitForEndOfFrame();

        string path = Path.Combine(Dir, $"{++sequence:D2}-{name}.png");
        Texture2D? shot = null;
        try
        {
            shot = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(path, shot.EncodeToPNG());
            log?.Line($"  screenshot: {Path.GetFileName(path)} ({shot.width}x{shot.height})");
        }
        catch (System.Exception e)
        {
            ///never fail a run over evidence-gathering
            log?.Line("  screenshot FAILED (" + name + "): " + e.Message);
        }
        finally
        {
            if (shot != null)
                UnityEngine.Object.Destroy(shot);
        }
    }
}
