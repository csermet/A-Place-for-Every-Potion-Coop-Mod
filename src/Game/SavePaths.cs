using System;
using System.IO;

namespace PotionCoop.Game
{
    /// <summary>
    /// While a guest plays in someone's lab, the shared world and the guest's progress for that host are loaded from
    /// and saved to that host's profile folder (see <see cref="Profiles"/>), so the guest's own solo save is never
    /// touched. Every save path of those keys (file, staged temp file, backup) is rewritten into the folder.
    /// </summary>
    internal static class SavePaths
    {
        public const string WorldKeyPrefix = "PotionsPersistentData";
        public const string ProgressKey = "PlayerProgress";

        static string _folder;

        public static bool Active => _folder != null;

        public static void Activate(string folder)
        {
            Directory.CreateDirectory(folder);
            _folder = folder.Replace('\\', '/').TrimEnd('/');
            CoopMod.Log.Msg($"saves redirected to {_folder}");
        }

        public static void Deactivate()
        {
            if (!Active)
                return;
            _folder = null;
            CoopMod.Log.Msg("saves back to the solo folder");
        }

        static bool IsRedirected(string key) => key != null && (key.StartsWith(WorldKeyPrefix) || key == ProgressKey);

        /// <summary>Rewrites a save-file path ("…/Key", "…/.Key.tmp", "…/.Key.bak") into the active profile folder.</summary>
        public static string Redirect(string key, string path)
        {
            if (!Active || path == null || !IsRedirected(key))
                return path;
            var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            var name = cut >= 0 ? path.Substring(cut + 1) : path;
            var redirected = _folder + "/" + name;
            return string.Equals(path.Replace('\\', '/'), redirected, StringComparison.OrdinalIgnoreCase) ? path : redirected;
        }
    }
}
