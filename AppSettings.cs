using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EksaCompanion
{
    public class AppSettings
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "EKSA_E910_Companion";

        public bool StartMinimized { get; set; }
        public bool AutoStart { get; set; }
        public bool MicEncEnabled { get; set; }
        public bool SurroundEnabled { get; set; }
        public string SelectedPreset { get; set; }
        public sbyte[] EqBands { get; set; }
        public DateTime LastChargedTime { get; set; }
        public DateTime LastActiveTime { get; set; }

        public AppSettings()
        {
            StartMinimized = true;
            AutoStart = false;
            MicEncEnabled = false; // Default OFF per user requirement
            SurroundEnabled = false;
            SelectedPreset = "ZXG"; // User's custom preset by default!
            EqBands = new sbyte[10] { 2, 3, 2, -2, 0, 1, 0, 2, -3, 3 };
            LastChargedTime = DateTime.Now;
            LastActiveTime = DateTime.Now;
        }

        public static string ConfigPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "EksaCompanion",
                    "settings.json"
                );
            }
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            try
            {
                // Ensure presets are loaded from OEM config if available
                Presets.LoadOemPresets();

                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    settings.StartMinimized = ExtractBool(json, "StartMinimized", true);
                    settings.AutoStart = ExtractBool(json, "AutoStart", false);
                    settings.MicEncEnabled = ExtractBool(json, "MicEncEnabled", false);
                    settings.SurroundEnabled = ExtractBool(json, "SurroundEnabled", false);
                    settings.SelectedPreset = ExtractString(json, "SelectedPreset", "ZXG");
                    if (settings.SelectedPreset == "ZXG (Custom)")
                        settings.SelectedPreset = "ZXG";
                    settings.EqBands = ExtractBands(json);
                    string lastCharged = ExtractString(json, "LastChargedTime", "");
                    DateTime lct;
                    if (!string.IsNullOrEmpty(lastCharged) && DateTime.TryParse(lastCharged, out lct))
                        settings.LastChargedTime = lct;
                    else
                        settings.LastChargedTime = DateTime.Now;

                    string lastActive = ExtractString(json, "LastActiveTime", "");
                    DateTime lat;
                    if (!string.IsNullOrEmpty(lastActive) && DateTime.TryParse(lastActive, out lat))
                        settings.LastActiveTime = lat;
                    else
                        settings.LastActiveTime = DateTime.Now;

                    // Overnight charging detection: if PC was off/sleeping for >= 3.5h, headset was charged overnight!
                    TimeSpan gap = DateTime.Now - settings.LastActiveTime;
                    if (gap.TotalHours >= 3.5)
                    {
                        settings.LastChargedTime = DateTime.Now;
                        settings.LastActiveTime = DateTime.Now;
                        settings.Save();
                    }
                }
                else
                {
                    // Save default settings including ZXG custom preset
                    settings.Save();
                }
            }
            catch { }
            return settings;
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine(string.Format("  \"StartMinimized\": {0},", StartMinimized.ToString().ToLower()));
                sb.AppendLine(string.Format("  \"AutoStart\": {0},", AutoStart.ToString().ToLower()));
                sb.AppendLine(string.Format("  \"MicEncEnabled\": {0},", MicEncEnabled.ToString().ToLower()));
                sb.AppendLine(string.Format("  \"SurroundEnabled\": {0},", SurroundEnabled.ToString().ToLower()));
                sb.AppendLine(string.Format("  \"SelectedPreset\": \"{0}\",", SelectedPreset));
                sb.AppendLine(string.Format("  \"LastChargedTime\": \"{0:o}\",", LastChargedTime));
                sb.AppendLine(string.Format("  \"LastActiveTime\": \"{0:o}\",", LastActiveTime));
                sb.Append("  \"EqBands\": [");
                for (int i = 0; i < EqBands.Length; i++)
                {
                    sb.Append(EqBands[i]);
                    if (i < EqBands.Length - 1) sb.Append(", ");
                }
                sb.AppendLine("]");
                sb.AppendLine("}");

                File.WriteAllText(ConfigPath, sb.ToString());

                // Update Windows registry startup if changed
                SetAutoStart(AutoStart);
            }
            catch { }
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true))
                {
                    if (key != null)
                    {
                        string appPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        if (enable)
                            key.SetValue(AppRegistryName, "\"" + appPath + "\" --minimized");
                        else
                            key.DeleteValue(AppRegistryName, false);
                    }
                }
            }
            catch { }
        }

        private static bool ExtractBool(string json, string key, bool def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            return m.Success ? bool.Parse(m.Groups[1].Value) : def;
        }

        private static string ExtractString(string json, string key, string def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : def;
        }

        private static sbyte[] ExtractBands(string json)
        {
            sbyte[] def = new sbyte[10] { 2, 3, 2, -2, 0, 1, 0, 2, -3, 3 };
            Match m = Regex.Match(json, "\"EqBands\"\\s*:\\s*\\[([^\\]]+)\\]");
            if (m.Success)
            {
                string[] parts = m.Groups[1].Value.Split(',');
                if (parts.Length == 10)
                {
                    sbyte[] res = new sbyte[10];
                    for (int i = 0; i < 10; i++)
                    {
                        sbyte val;
                        if (sbyte.TryParse(parts[i].Trim(), out val))
                            res[i] = val;
                    }
                    return res;
                }
            }
            return def;
        }
    }

    public static class Presets
    {
        public static readonly Dictionary<string, sbyte[]> All = new Dictionary<string, sbyte[]>
        {
            { "ZXG", new sbyte[] { 2, 3, 2, -2, 0, 1, 0, 2, -3, 3 } },
            { "Flat", new sbyte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } },
            { "Deep Bass", new sbyte[] { 2, 5, 4, 1, -2, 0, 0, 0, -1, -1 } },
            { "EKSA Standard", new sbyte[] { -3, -3, -3, -3, -2, 0, 0, 0, 0, 10 } },
            { "Classic", new sbyte[] { 0, 0, 0, 0, 0, -2, -2, -2, -5, -8 } },
            { "Pop", new sbyte[] { 9, 6, 5, 2, -6, -6, -3, -3, 1, 2 } },
            { "Jazz", new sbyte[] { -1, -1, 2, 4, 4, 4, 4, 4, 2, 2 } },
            { "Pack&Roll", new sbyte[] { 9, 9, -3, 4, -2, 1, 5, 5, 9, 9 } }
        };

        public static readonly Dictionary<string, sbyte[]> SurroundCurves = new Dictionary<string, sbyte[]>
        {
            { "Deep Bass", new sbyte[] { 5, 4, 3, 3, 1, -1, -1, -1, -1, -1 } },
            { "EKSA Standard", new sbyte[] { -3, -3, -3, -3, -2, 0, 0, 0, 0, 0 } }
        };

        public static sbyte[] GetCurve(string presetName, bool surround)
        {
            if (string.IsNullOrEmpty(presetName)) return null;
            if (presetName == "ZXG (Custom)") presetName = "ZXG";

            sbyte[] gains;
            if (surround && SurroundCurves.TryGetValue(presetName, out gains))
                return (sbyte[])gains.Clone();

            if (All.TryGetValue(presetName, out gains))
                return (sbyte[])gains.Clone();

            return null;
        }

        public static void LoadOemPresets()
        {
            try
            {
                string oemDocPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "ToneTuningTool",
                    "config.json"
                );

                if (File.Exists(oemDocPath))
                {
                    string json = File.ReadAllText(oemDocPath);
                    // Match modes with name and parameter array
                    MatchCollection matches = Regex.Matches(json, "\"name\"\\s*:\\s*\"([^\"]+)\"[^\\}]*\"parameter\"\\s*:\\s*\\[([^\\]]+)\\]", RegexOptions.Singleline);
                    foreach (Match m in matches)
                    {
                        string name = m.Groups[1].Value.Trim();
                        // Ignore FPS and MOBA per user preference
                        if (string.Equals(name, "FPS", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "MOBA", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string paramStr = m.Groups[2].Value.Trim();
                        string[] parts = paramStr.Split(',');
                        if (parts.Length == 10)
                        {
                            sbyte[] bands = new sbyte[10];
                            bool ok = true;
                            for (int i = 0; i < 10; i++)
                            {
                                sbyte b;
                                if (sbyte.TryParse(parts[i].Trim(), out b))
                                    bands[i] = b;
                                else
                                    ok = false;
                            }
                            if (ok)
                            {
                                All[name] = bands;
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }
}
