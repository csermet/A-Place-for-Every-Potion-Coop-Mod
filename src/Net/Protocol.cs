using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PotionCoop.Net
{
    /// <summary>
    /// Where a vessel is, in absolute terms both peers agree on
    /// (a local "in my hand" becomes HeldByHost or HeldByClient depending on who I am).
    /// </summary>
    internal enum NetLocation
    {
        Floor = 0,
        Slot = 2,
        Holder = 4,
        HeldByHost = 10,
        HeldByClient = 11,
    }

    internal sealed class NetPotion
    {
        [JsonProperty("u")] public int Uid;
        [JsonProperty("l")] public NetLocation Location;
        /// <summary>Slot uid for Slot, holder index for Holder.</summary>
        [JsonProperty("s")] public int Place;
        [JsonProperty("p", NullValueHandling = NullValueHandling.Ignore)] public float[] Pos;
        [JsonProperty("r", NullValueHandling = NullValueHandling.Ignore)] public float[] Rot;
        [JsonProperty("n")] public byte[] Units;
        /// <summary>Floor only: the bottle has just left the sender's hand and is still falling.</summary>
        [JsonProperty("f", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Falling;
        /// <summary>With Falling: Pos/Rot are where the throw will land (the game's own drop target).</summary>
        [JsonProperty("t", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Toss;
    }

    internal sealed class NetVat
    {
        [JsonProperty("u")] public int Uid;
        [JsonProperty("n")] public byte[] Units;
    }

    // ---- Messages (field "T" carries the type) ----

    internal sealed class HelloMsg
    {
        public string T = "hello";
        public string Mod;
        public string Game;
        public string Name;
    }

    /// <summary>The playthrough fields of the host's PlayerProgress: they belong to the shared world.</summary>
    internal sealed class NetWorldInfo
    {
        public bool GameStarted;
        public bool LabCompleted;
        public bool LabSummaryShown;
        public int LabManifestHash;
        public int DeclinedManifestHash;
        public int[] ScoredShelves;
        public int[] ScoredSections;

        /// <summary>The host has a lab to share (a fresh host has none until it enters one).</summary>
        [JsonIgnore] public bool Valid => GameStarted && LabManifestHash != 0;
    }

    /// <summary>A player's own progress: XP and skills (the level is computed from them).</summary>
    internal sealed class NetPersonal
    {
        public int Xp;
        public int StartingXp;
        public int Onboarding;
        public int[] Owned = new int[0];
        public int[] SwitchedOff = new int[0];
    }

    /// <summary>
    /// The host's Essence Separator rules. They come from the host's skills and progress but shape the shared
    /// separator, so a guest's separator follows them (unlocked, capacity, usable holders).
    /// </summary>
    internal sealed class NetSeparatorRules
    {
        public bool Unlocked;
        public int Capacity;
        public int LoopRanks;
        public int HolderRanks;
        public bool[] HolderUsable;
    }

    internal sealed class WelcomeMsg
    {
        public string T = "welcome";
        public string Name;
        public NetSeparatorRules Separator;
        /// <summary>Stable id of the host's install: the guest keeps one profile per host.</summary>
        public string HostId;
        public NetWorldInfo World;
        /// <summary>The host's own progress, for a guest who chooses to start at the host's level.</summary>
        public NetPersonal HostPersonal;
        /// <summary>The host's world save files (index = bucket tag 1..16), so the guest loads exactly this lab.</summary>
        public string[] Buckets;
    }

    /// <summary>Host entered (another) lab: the world the guest should load.</summary>
    internal sealed class WorldMsg
    {
        public string T = "world";
        public NetWorldInfo World;
        public string[] Buckets;
    }

    /// <summary>A vessel in a pour: a potion, a vat, or one of a vat's taps.</summary>
    internal sealed class VesselRef
    {
        public const int Potion = 0, Vat = 1, Tap = 2;
        public int K;
        public int U;
        /// <summary>Tap index in the vat (K = Tap).</summary>
        public int I;
    }

    /// <summary>A pour has started on the sender's side; the receiver plays it (the final states follow as usual).</summary>
    internal sealed class PourMsg
    {
        public string T = "pour";
        public VesselRef From;
        public VesselRef To;
        public int F;
        public int N;
        /// <summary>The sender's lab (see <see cref="StatesMsg.Lab"/>).</summary>
        public int L;
    }

    /// <summary>Guest → host: pour out of the bottle on this separator holder (only the host's separator runs).</summary>
    internal sealed class SeparatorPourMsg
    {
        public string T = "sep";
        public int U;
        public int H;
    }

    internal sealed class RejectMsg
    {
        public string T = "reject";
        public string Reason;
    }

    internal sealed class PlayerMsg
    {
        public string T = "p";
        public float[] Pos;
        public float Yaw;
        public float Pitch;
        public bool InMenu;
        public int Held;
    }

    /// <summary>Host's cat: pose plus animator parameters (by name hash).</summary>
    internal sealed class CatMsg
    {
        public string T = "cat";
        public float[] Pos;
        public float[] Rot;
        public Dictionary<int, float> F;
        public Dictionary<int, int> I;
        public Dictionary<int, bool> B;
    }

    /// <summary>Vessel/vat states. Sent for local changes and as answers to bucket requests.</summary>
    internal sealed class StatesMsg
    {
        public string T = "s";
        public List<NetPotion> Potions = new();
        public List<NetVat> Vats = new();
        /// <summary>True when this answers a resync request (receiver may re-check digests afterwards).</summary>
        public bool Resync;
        /// <summary>Sender's running number of states messages.</summary>
        [JsonProperty("q")] public int Seq;
        /// <summary>
        /// The last of the receiver's own states messages the sender had applied when it wrote this one. A vessel the
        /// receiver changed after that is newer on the receiver's side: this message's state of it is stale.
        /// </summary>
        [JsonProperty("k")] public int Ack;
        /// <summary>The sender's lab (manifest hash): states of another lab's bottles must not land on ours.</summary>
        [JsonProperty("l")] public int Lab;
        /// <summary>
        /// Host's answer to a guest message it turned down: these are the vessels as they are, contents included for
        /// bottles in the guest's own hands (normally the guest's to say).
        /// </summary>
        [JsonProperty("x", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Fix;
    }

    /// <summary>Host's per-bucket world hashes (index 0 = vats, 1..16 = potion buckets).</summary>
    internal sealed class DigestMsg
    {
        public string T = "d";
        public uint[] Buckets;
        /// <summary>Host's vat capacities (vat uid -> units). They depend on the host's skills and rule the shared vats.</summary>
        public Dictionary<int, int> VatCapacity;
        public NetSeparatorRules Separator;
        /// <summary>The host's lab (see <see cref="StatesMsg.Lab"/>).</summary>
        public int L;
    }

    /// <summary>Client asks the host for the full state of some buckets.</summary>
    internal sealed class NeedMsg
    {
        public string T = "need";
        public int[] Buckets;
    }

    internal sealed class PingMsg
    {
        public string T = "ping";
        public double Sent;
    }

    internal sealed class PongMsg
    {
        public string T = "pong";
        public double Sent;
    }

    internal static class Protocol
    {
        public const string ModVersion = "0.4.7";
        public const int BucketCount = 17;

        static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
        };

        public static string Encode(object msg) => JsonConvert.SerializeObject(msg, Settings);

        public static (string type, JObject body) Decode(string json)
        {
            var body = JObject.Parse(json);
            return ((string)body["T"], body);
        }

        public static int BucketOf(int uid)
        {
            var bucket = uid / 1_000_000;
            return bucket is >= 1 and < BucketCount ? bucket : 0;
        }
    }
}
