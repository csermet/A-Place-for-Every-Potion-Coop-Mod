using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppCodeBase.Common.Utilities;
using Il2CppCodeBase.Services.PersistentDataService;
using Il2CppCodeBase.Services.PotionsPersistenceService;
using Il2CppCodeBase.Services.SaveLoadService;
using Newtonsoft.Json.Linq;
using PotionCoop.Net;
using UnityEngine;

namespace PotionCoop.Game
{
    /// <summary>
    /// A guest's progress in someone else's lab. Each host gets its own profile folder on the guest's PC
    /// (CoopProfiles\&lt;host id&gt;) holding the guest's PlayerProgress for that host and a copy of the host's world.
    /// The game loads PlayerProgress once at boot and keeps it, so entering and leaving a profile rewrites that object
    /// in place (personal fields from the profile, playthrough fields from the host) and redirects its saves.
    /// </summary>
    internal static class Profiles
    {
        const string FolderName = "CoopProfiles";
        const string WorldTypeName = "PotionsPersistentData";
        const float ExitDelay = 1.5f;

        public static string ActiveHostId { get; private set; }
        public static bool Active => ActiveHostId != null;

        static float _exitAt = -1f;

        static string SaveDir => SaveLoadService.SaveDirectoryPath();

        public static string FolderOf(string hostId)
        {
            var safe = new string((hostId ?? "unknown").Where(char.IsLetterOrDigit).ToArray());
            return Path.Combine(SaveDir, FolderName, safe.Length > 0 ? safe : "unknown");
        }

        public static bool Exists(string hostId) => File.Exists(Path.Combine(FolderOf(hostId), SavePaths.ProgressKey));

        // ---------- reading what the game holds ----------

        public static NetPersonal ReadPersonal(PlayerProgress p) => new()
        {
            Xp = p.ExperiencePoints.Value,
            StartingXp = p.StartingExperience,
            Onboarding = p.OnboardingFlags.Value,
            Owned = ToArray(p.OwnedSkills),
            SwitchedOff = ToArray(p.SwitchedOffSkills),
        };

        public static NetWorldInfo ReadWorld(PlayerProgress p) => new()
        {
            GameStarted = p.GameStarted,
            LabCompleted = p.LabCompleted,
            LabSummaryShown = p.LabSummaryShown,
            LabManifestHash = p.LabManifestHash,
            DeclinedManifestHash = p.DeclinedManifestHash,
            ScoredShelves = ToArray(p.ScoredShelves),
            ScoredSections = ToArray(p.ScoredSections),
        };

        static int[] ToArray(ReactiveList<eSkill> list)
        {
            var result = new int[list.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = (int)list[i];
            return result;
        }

        static int[] ToArray(ReactiveList<int> list)
        {
            var result = new int[list.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = list[i];
            return result;
        }

        /// <summary>Host: the world save files as the game holds them now (index = bucket tag).</summary>
        public static string[] ReadWorldBuckets()
        {
            var buckets = new string[Protocol.BucketCount];
            var data = GameFlow.Data;
            if (data == null)
                return buckets;
            for (var tag = 1; tag < Protocol.BucketCount; tag++)
            {
                try
                {
                    var bucket = data.Get<PotionsPersistentData>(tag);
                    buckets[tag] = bucket != null ? JsonUtility.ToJson(bucket) : null;
                }
                catch (Exception e)
                {
                    CoopMod.Log.Warning($"reading world bucket {tag}: {e.Message}");
                }
            }
            return buckets;
        }

        // ---------- profile files (same JSON the game writes) ----------

        static (NetPersonal personal, NetWorldInfo world) ParseProgress(string json)
        {
            var o = JObject.Parse(json);
            int Int(string a, string b = null) => (int?)(b == null ? o[a] : o[a]?[b]) ?? 0;
            int[] List(string a) => o[a]?["_innerList"]?.Select(t => (int)t).ToArray() ?? new int[0];
            var personal = new NetPersonal
            {
                Xp = Int("ExperiencePoints", "_value"),
                StartingXp = Int("StartingExperience"),
                Onboarding = Int("OnboardingFlags", "_value"),
                Owned = List("OwnedSkills"),
                SwitchedOff = List("SwitchedOffSkills"),
            };
            var world = new NetWorldInfo
            {
                GameStarted = (bool?)o["GameStarted"] ?? false,
                LabCompleted = (bool?)o["LabCompleted"] ?? false,
                LabSummaryShown = (bool?)o["LabSummaryShown"] ?? false,
                LabManifestHash = Int("LabManifestHash"),
                DeclinedManifestHash = Int("DeclinedManifestHash"),
                ScoredShelves = List("ScoredShelves"),
                ScoredSections = List("ScoredSections"),
            };
            return (personal, world);
        }

        /// <summary>A brand-new player: no XP, no skills (tutorial flags stay this PC's own).</summary>
        public static NetPersonal Fresh(PlayerProgress current) => new() { Onboarding = current.OnboardingFlags.Value };

        // ---------- writing into the object the game holds ----------

        static void ApplyPersonal(PlayerProgress p, NetPersonal personal)
        {
            p.StartingExperience = personal.StartingXp;
            p.OnboardingFlags.Value = personal.Onboarding;
            SetList(p.OwnedSkills, personal.Owned);
            SetList(p.SwitchedOffSkills, personal.SwitchedOff);
            p.ExperiencePoints.Value = personal.Xp;
        }

        static void ApplyWorld(PlayerProgress p, NetWorldInfo world)
        {
            p.GameStarted = world.GameStarted;
            p.LabCompleted = world.LabCompleted;
            p.LabSummaryShown = world.LabSummaryShown;
            p.LabManifestHash = world.LabManifestHash;
            p.DeclinedManifestHash = world.DeclinedManifestHash;
            SetList(p.ScoredShelves, world.ScoredShelves);
            SetList(p.ScoredSections, world.ScoredSections);
        }

        static void SetList(ReactiveList<eSkill> list, int[] values)
        {
            list.Clear();
            foreach (var value in values ?? new int[0])
                list.Add((eSkill)value);
        }

        static void SetList(ReactiveList<int> list, int[] values)
        {
            list.Clear();
            foreach (var value in values ?? new int[0])
                list.Add(value);
        }

        /// <summary>The cached world buckets are the old world: drop them, the next read loads the active folder.</summary>
        static void ForgetCachedWorld()
        {
            var service = GameFlow.Data?.TryCast<PersistentDataService>();
            if (service != null)
            {
                var stale = new List<Il2CppSystem.ValueTuple<Il2CppSystem.Type, int>>();
                foreach (var pair in service._taggedData)
                    if (pair.Key.Item1 != null && pair.Key.Item1.Name == WorldTypeName)
                        stale.Add(pair.Key);
                foreach (var key in stale)
                    service._taggedData.Remove(key);
            }
            var persistence = GameFlow.Persistence?.TryCast<PotionsPersistence>();
            if (persistence != null)
            {
                persistence._validatedBuckets?.Clear();
                persistence._activeManifest = null;
                persistence._activeManifestResolved = false;
            }
        }

        static void Remirror()
        {
            try { GameFlow.Mediator?.Skills?.Remirror(); }
            catch (Exception e) { CoopMod.Log.Warning($"skills refresh: {e.Message}"); }
        }

        // ---------- entering and leaving ----------

        /// <summary>
        /// Guest, in the main menu: switch to this host's profile. <paramref name="seed"/> creates the profile
        /// (fresh or the host's level); null uses the saved one.
        /// </summary>
        public static bool Enter(string hostId, NetWorldInfo world, string[] buckets, NetPersonal seed)
        {
            var data = GameFlow.Data;
            var progress = data?.Get<PlayerProgress>();
            if (progress == null)
            {
                CoopMod.Log.Error("profile: the game's progress is not available");
                return false;
            }
            if (Active)
                ExitNow();
            _exitAt = -1f;

            var folder = FolderOf(hostId);
            Directory.CreateDirectory(folder);
            NetPersonal personal = seed;
            if (personal == null)
            {
                try { personal = ParseProgress(File.ReadAllText(Path.Combine(folder, SavePaths.ProgressKey))).personal; }
                catch (Exception e)
                {
                    CoopMod.Log.Warning($"profile could not be read ({e.Message}); starting fresh");
                    personal = Fresh(progress);
                }
            }

            // The solo state to its own file before anything else is written.
            data.Save<PlayerProgress>(true);

            // The host's world exactly as it is now, so Continue loads the same lab.
            if (buckets != null)
                for (var tag = 1; tag < buckets.Length; tag++)
                    if (buckets[tag] != null)
                        File.WriteAllText(Path.Combine(folder, $"{WorldTypeName}_{tag}"), buckets[tag]);

            SavePaths.Activate(folder);
            ActiveHostId = hostId;
            ApplyWorld(progress, world);
            ApplyPersonal(progress, personal);
            ForgetCachedWorld();
            Remirror();
            data.Save<PlayerProgress>(true);
            CoopMod.Log.Msg($"profile for host {hostId}: {personal.Xp} XP, {personal.Owned.Length} skill(s)");
            return true;
        }

        /// <summary>Leave the profile once the menu is reached and pending saves have gone to the profile folder.</summary>
        public static void ExitSoon()
        {
            if (!Active)
                return;
            GameFlow.SaveNow();
            if (_exitAt < 0f)
                _exitAt = Time.realtimeSinceStartup + ExitDelay;
        }

        public static void Tick()
        {
            if (_exitAt < 0f || Time.realtimeSinceStartup < _exitAt)
                return;
            if (GameRefs.State is not (eGameState.Lobby or eGameState.None))
            {
                _exitAt = Time.realtimeSinceStartup + 0.5f; // still on the way to the menu
                return;
            }
            ExitNow();
        }

        /// <summary>Back to the solo player: their progress values from the solo file (untouched while away).</summary>
        static void ExitNow()
        {
            _exitAt = -1f;
            if (!Active)
                return;
            var data = GameFlow.Data;
            var progress = data?.Get<PlayerProgress>();
            if (progress != null)
            {
                data.Save<PlayerProgress>(true); // the guest's latest, to the profile
                var soloFile = Path.Combine(SaveDir, SavePaths.ProgressKey);
                (NetPersonal personal, NetWorldInfo world) solo;
                try
                {
                    solo = File.Exists(soloFile)
                        ? ParseProgress(File.ReadAllText(soloFile))
                        : (new NetPersonal { Onboarding = progress.OnboardingFlags.Value }, new NetWorldInfo());
                }
                catch (Exception e)
                {
                    CoopMod.Log.Error($"solo progress unreadable ({e.Message}); keeping the profile's values in memory");
                    solo = (ReadPersonal(progress), new NetWorldInfo());
                }
                // Values first, then the paths: a save made after this point is the solo state, to the solo file.
                ApplyWorld(progress, solo.world);
                ApplyPersonal(progress, solo.personal);
            }
            SavePaths.Deactivate();
            ActiveHostId = null;
            ForgetCachedWorld();
            Remirror();
            CoopMod.Log.Msg("back to the solo profile");
        }
    }
}
