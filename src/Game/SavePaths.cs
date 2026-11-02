using System;
using System.IO;
using Il2CppCodeBase.Services.SaveLoadService;

namespace PotionCoop.Game
{
    /// <summary>
    /// On the client, the shared world is loaded from and saved to a separate "CoopSession" folder,
    /// so the friend's own solo world is never overwritten. Personal progress (XP, skills) keeps its usual file.
    /// </summary>
    internal static class SavePaths
    {
        public const string WorldKeyPrefix = "PotionsPersistentData";
        const string FolderName = "CoopSession";

        public static bool Active { get; private set; }

        static string SaveDir => SaveLoadService.SaveDirectoryPath();
        static string CoopDir => Path.Combine(SaveDir, FolderName);

        public static void Activate()
        {
            if (Active)
                return;
            try
            {
                Directory.CreateDirectory(CoopDir);
                // First co-op session on this PC: start from a copy of the solo world; the host's state is applied on top.
                if (Directory.GetFiles(CoopDir, WorldKeyPrefix + "*").Length == 0)
                {
                    foreach (var file in Directory.GetFiles(SaveDir))
                    {
                        var name = Path.GetFileName(file);
                        if (name.StartsWith(WorldKeyPrefix) || name.StartsWith("." + WorldKeyPrefix))
                            File.Copy(file, Path.Combine(CoopDir, name));
                    }
                }
                Active = true;
                CoopMod.Log.Msg($"world saves redirected to {CoopDir}");
            }
            catch (Exception e)
            {
                CoopMod.Log.Error($"could not prepare {CoopDir}: {e.Message}");
            }
        }

        /// <summary>Has this PC ever started a game (so the menu offers Continue)?</summary>
        public static bool HasStartedGame()
        {
            try
            {
                var progress = Path.Combine(SaveDir, "PlayerProgress");
                return File.Exists(progress) && File.ReadAllText(progress).Contains("\"GameStarted\":true");
            }
            catch (Exception)
            {
                return true;
            }
        }

        public static void Deactivate()
        {
            if (!Active)
                return;
            Active = false;
            CoopMod.Log.Msg("world saves back to the solo folder");
        }

        /// <summary>Rewrites a save-file path for a world key into the co-op folder.</summary>
        public static string Redirect(string key, string path)
        {
            if (!Active || key == null || path == null || !key.StartsWith(WorldKeyPrefix))
                return path;
            var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            if (cut < 0)
                return path;
            var dir = path.Substring(0, cut);
            if (dir.EndsWith(FolderName))
                return path;
            return dir + "/" + FolderName + path.Substring(cut);
        }
    }
}
