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

    internal sealed class WelcomeMsg
    {
        public string T = "welcome";
        public string Name;
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
    }

    /// <summary>Host's per-bucket world hashes (index 0 = vats, 1..16 = potion buckets).</summary>
    internal sealed class DigestMsg
    {
        public string T = "d";
        public uint[] Buckets;
        /// <summary>Host's vat capacities (vat uid -> units). They depend on the host's skills and rule the shared vats.</summary>
        public Dictionary<int, int> VatCapacity;
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
        public const string ModVersion = "0.3.1";
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
