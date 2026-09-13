namespace SaveProfiler;

public class STRINGS
{
    public class SAVEPROFILER_CONFIG
    {
        public class RECORDAUTOSAVES
        {
            public static LocString TITLE = "Profile autosaves";
            public static LocString TOOLTIP = "Write a timing report every time the game autosaves.";
        }

        public class RECORDMANUALSAVES
        {
            public static LocString TITLE = "Profile manual saves";
            public static LocString TOOLTIP = "Also write a report when you save the game yourself.";
        }

        public class COMPONENTATTRIBUTION
        {
            public static LocString TITLE = "Break down by component type";
            public static LocString TOOLTIP =
                "Measure every component separately, so the report can say which types cost the most.\n\n" +
                "This adds a timing wrapper to hundreds of thousands of calls per save, which slows the " +
                "save down and inflates the phase totals in the same report. Turn it on for a run when " +
                "you want the breakdown, and do not compare that run's phase timings against a run with " +
                "it off.";
        }

        public class WRITEJSON
        {
            public static LocString TITLE = "Also write JSON";
            public static LocString TOOLTIP = "Write a machine-readable copy of each report alongside the Markdown one.";
        }

        public class KEEPREPORTS
        {
            public static LocString TITLE = "Reports to keep";
            public static LocString TOOLTIP =
                "How many reports to keep before deleting the oldest. Set to 0 to keep every report.";
        }
    }
}
