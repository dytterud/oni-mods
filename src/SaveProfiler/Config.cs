using PeterHan.PLib.Options;

namespace SaveProfiler;

[Serializable]
[RestartRequired]
[ModInfo("https://github.com/dytterud/oni-mods", collapse: true)]
[ConfigFile(SharedConfigLocation: true)]
public class Config : SingletonOptions<Config>
{
    [Option("STRINGS.SAVEPROFILER_CONFIG.RECORDAUTOSAVES.TITLE", "STRINGS.SAVEPROFILER_CONFIG.RECORDAUTOSAVES.TOOLTIP")]
    public bool RecordAutosaves { get; set; } = true;

    [Option("STRINGS.SAVEPROFILER_CONFIG.RECORDMANUALSAVES.TITLE", "STRINGS.SAVEPROFILER_CONFIG.RECORDMANUALSAVES.TOOLTIP")]
    public bool RecordManualSaves { get; set; }

    /// <summary>
    /// Off by default, and the default is the point. The per-component patch fires once per
    /// component - hundreds of thousands of times in a mature colony - so it adds measurable time
    /// to the phase totals it sits inside. Leave it off for the headline numbers; turn it on for
    /// one run when you want to know which component types dominate, and read that run's phase
    /// figures as perturbed.
    /// </summary>
    [Option("STRINGS.SAVEPROFILER_CONFIG.COMPONENTATTRIBUTION.TITLE", "STRINGS.SAVEPROFILER_CONFIG.COMPONENTATTRIBUTION.TOOLTIP")]
    public bool ComponentAttribution { get; set; }

    [Option("STRINGS.SAVEPROFILER_CONFIG.WRITEJSON.TITLE", "STRINGS.SAVEPROFILER_CONFIG.WRITEJSON.TOOLTIP")]
    public bool WriteJson { get; set; } = true;

    [Option("STRINGS.SAVEPROFILER_CONFIG.KEEPREPORTS.TITLE", "STRINGS.SAVEPROFILER_CONFIG.KEEPREPORTS.TOOLTIP")]
    public int KeepReports { get; set; } = 20;
}
