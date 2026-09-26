using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// LIMISAW's quota engine, in-process.
//
// Every number here comes from a vendor's own client — its CLI, its cache, its
// journal. Nothing is estimated, no token is read, no auth file is parsed. The
// engine used to be a Python package next to the exe; it lives here so LIMISAW
// is one file with no runtime to install.
namespace Limisaw
{
    // в”Ђв”Ђ JSON access в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    // JavaScriptSerializer.DeserializeObject hands back Dictionary/object[]/
    // boxed numbers. Every read goes through these so a vendor changing a field
    // type is a null, never an exception in the middle of a sweep.
    static class J
    {
        // PERF-007/R030: every external/vendor JSON payload is BOUNDED. The old
        // code set MaxJsonLength = int.MaxValue and let any payload grow without
        // a ceiling; now the boundaries are narrow and documented: a Desktop
        // history file may be 8 MiB and no other local JSON is legitimately
        // anywhere near that, so a single hard ceiling at 8 MiB + generous slack
        // keeps the real fixtures green while rejecting pathological payloads.
        // J.Parse is the LAST guard — every caller that knows its input SHOULD
        // stat it before ReadAllText — but any oversized JSON beyond the ceiling
        // is refused here BEFORE the serializer sees it, so a valid prefix with
        // an oversized tail is never parsed as partial truth. Oversized never
        // means "parse the prefix we saw".
        public const int MaxJsonChars = 9 * 1024 * 1024;

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (text.Length > MaxJsonChars) return null;
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = MaxJsonChars;
            ser.RecursionLimit = 200;
            try { return ser.DeserializeObject(text); } catch { return null; }
        }

        public static string Write(object value)
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = Math.Min(int.MaxValue, MaxJsonChars);
            return ser.Serialize(value);
        }

        public static Dictionary<string, object> Obj(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static object Get(object value, string key)
        {
            var d = value as Dictionary<string, object>;
            if (d == null) return null;
            object found;
            return d.TryGetValue(key, out found) ? found : null;
        }

        public static IEnumerable Arr(object value)
        {
            if (value is object[]) return (object[])value;
            return value as IEnumerable == null || value is string ? new object[0] : (IEnumerable)value;
        }

        public static string Str(object value)
        {
            if (value == null) return null;
            string s = value as string;
            return s ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static double? Num(object value)
        {
            if (value == null || value is bool || value is string) return null;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public static bool Flag(object value)
        {
            return value is bool && (bool)value;
        }
    }

    // W2-005: a local-file read that bounds what is RETAINED, not what was
    // observed at one instant. These files are caches/config/history written by
    // other live tools, so they are opened with FileShare.ReadWrite and can grow
    // between any pre-stat and the read. The opened handle is the authority:
    // at most cap+1 bytes are read; if that +1 byte exists the WHOLE snapshot is
    // refused and no prefix is ever parsed. Metadata stays useful as an early
    // fast refusal, but it is never the only bound.
    static class BoundedFile
    {
        // Returns the decoded text, or null with `error` set to "too large" or
        // "unreadable". `bytesRead` is the number of bytes actually retained
        // (the cache identity length), never the pre-read FileInfo.Length.
        public static string ReadAllText(string path, long maxBytes, out long bytesRead, out string error)
        {
            bytesRead = 0;
            error = null;
            long cap = maxBytes < 0 ? 0 : maxBytes;
            int limit = cap >= int.MaxValue - 1 ? int.MaxValue - 1 : (int)cap + 1;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buffer = new byte[limit];
                    int total = 0;
                    while (total < limit)
                    {
                        int n = fs.Read(buffer, total, limit - total);
                        if (n <= 0) break;
                        total += n;
                    }
                    bytesRead = total;
                    if (total > cap) { error = "too large"; return null; }
                    return Encoding.UTF8.GetString(buffer, 0, total);
                }
            }
            catch { error = "unreadable"; return null; }
        }
    }

    // в”Ђв”Ђ timestamps в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    static class Stamp
    {
        static readonly DateTime Epoch1970 = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static double Now { get { return (DateTime.UtcNow - Epoch1970).TotalSeconds; } }

        // Epoch seconds from a vendor value: a number in seconds or
        // milliseconds, or an ISO-8601 string (Z / offset / naive local).
        public static double? Epoch(object value)
        {
            if (value is bool) return null;
            double? n = J.Num(value);
            if (n.HasValue)
            {
                double v = n.Value;
                if (v > 1e11) v /= 1000.0;          // milliseconds
                return (v > 1e9 && v < 1e11) ? v : (double?)null;
            }
            string s = J.Str(value);
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            // Some vendors write 9 fractional digits; .NET parses at most 7.
            s = Regex.Replace(s, @"(\.\d{6})\d+", "$1");
            DateTimeOffset off;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out off))
                return (off.UtcDateTime - Epoch1970).TotalSeconds;
            return null;
        }

        public static DateTime Local(double epoch)
        {
            return Epoch1970.AddSeconds(epoch).ToLocalTime();
        }

        // Epoch seconds of a UTC DateTime — file mtimes, mostly.
        public static double Of(DateTime utc)
        {
            return (utc - Epoch1970).TotalSeconds;
        }

        // The format LIMISAW's window rows carry: local, no zone, second
        // precision — exactly what DateTime.TryParse reads back as local.
        public static string Iso(double? epoch)
        {
            if (!epoch.HasValue || epoch.Value <= 0) return null;
            try { return Local(epoch.Value).ToString("yyyy-MM-ddTHH:mm:ss"); }
            catch { return null; }
        }

        // CORE-013: the invariant identity of a reset instant. Iso is
        // PRESENTATION — local, offset-less, exactly what DateTime.TryParse
        // reads back — and a DST fall-back renders two distinct instants to the
        // same wall clock, so a string rebuilt from it can collapse them. The
        // token is culture-invariant decimal UTC seconds, which two windows can
        // compare byte-for-byte and parse back losslessly; quota logic keys on
        // this, never on the rendering.
        public static string Token(double? epoch)
        {
            if (!epoch.HasValue || epoch.Value <= 0) return null;
            return epoch.Value.ToString("R", CultureInfo.InvariantCulture);
        }

        public static double? FromToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            double v;
            return double.TryParse(token, NumberStyles.Float,
                CultureInfo.InvariantCulture, out v) && v > 1e9 && v < 1e11
                ? v : (double?)null;
        }
    }

    // в”Ђв”Ђ domain в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    // One quota window as a provider reported it, before display resolution.
    class ProbeWindow
    {
        public string Key = "";          // five_hour / weekly / monthly / vendor-specific
        public string Group = "";        // independent quota pool, "" = the account's only one
        public string GroupLabel = "";
        public string Source = "";
        public bool Available;
        public bool AssumedFull;         // its own reset time passed; refill derived from the clock
        public double? Remaining;        // percent LEFT, never invented
        public double? ResetEpoch;
        public int? DurationMinutes;
        public string GatedBy;           // key of the longer window in this pool that is spent
        public bool IsExpiry;            // true if expiresAt, false if recurring resetsAt
        public bool Allocated = true;    // whether an active quota allocation exists

        public ProbeWindow Copy()
        {
            return new ProbeWindow
            {
                Key = Key, Group = Group, GroupLabel = GroupLabel, Source = Source,
                Available = Available, AssumedFull = AssumedFull, Remaining = Remaining,
                ResetEpoch = ResetEpoch, DurationMinutes = DurationMinutes, GatedBy = GatedBy,
                IsExpiry = IsExpiry, Allocated = Allocated,
            };
        }

        public static ProbeWindow Unavailable(string key)
        {
            return new ProbeWindow { Key = key, Available = false };
        }
    }

class ProbeAccount
    {
        public string Provider = "", ProviderLabel = "", Name = "", Status = "", Plan, Error;
        // Stable identity for persistence and irreversible action routing.
        // Codex homes are discovery-based and can repeat a display name, so
        // the display label is NEVER identity: `SourceId` is derived from the
        // exact canonical home and `ResetHome` is that home's path, so a banked
        // reset spends on the account the user actually selected, not on
        // whichever duplicate "Codex" happens to sort first.
        public string SourceId = "";
        public string ResetHome = "";
        // Opaque digest of vendor-reported accountId. Never a home-routing key.
        public string RemoteAccountIdentity = "";
        public bool RemoteIdentityChecked;
        public bool Ok, Quiet;
        public List<ProbeWindow> Windows = new List<ProbeWindow>();
        // Normalized collection of explicitly identified reserve pools
        public List<ReservePool> Reserves = new List<ReservePool>();
        public AccountAvailability Availability;
        // W2-003: a refusal whose reset passed inside the grace is an EVENT,
        // not a reading — the window stays unreadable and this flag tells
        // Flatten the quota is unverified rather than carried-forward stale.
        public bool UnverifiedReset;
        // SRC-028: typed failure provenance for THIS sweep. `Ok == false` alone
        // cannot tell a transient hiccup (whose carried-forward numbers stay
        // conservatively selectable, because dropping them would make the tray
        // look healthier after a failure) from an explicit authentication
        // rejection, which is much stronger evidence: the vendor refused the
        // authenticated read, so a carried number is NOT a currently usable
        // quota. Set by the provider boundary, never inferred from UI strings.
        public bool AuthFailed;
        public CodexSource.RateLimitFailure AuthFailureClass;
        public string AuthFailureReason = "";
        // Banked resets this account holds, or null. Not a window: a count,
        // an expiry and the vendor's own title.
        public ResetCredits Credits;
        // Absolute balances this account reports (FreeBucks), or empty. NOT
        // windows: an absolute amount with no denominator, so it never enters
        // Windows and never feeds percentage logic (see BalanceData).
        public List<BalanceData> Balances = new List<BalanceData>();
    }

    // A normalized reserve pool representing a dedicated reserve bucket (e.g. Luna reserve, generic GPT reserve).
    class ReservePool
    {
        public string Id = "";                  // unique group key, e.g. "luna_reserve" or "base_model_inference"
        public string RawLimitId = "";          // upstream limitId, e.g. "base_model_inference"
        public string RawLimitName = "";        // upstream limitName, e.g. "gpt-reserve"
        public string Family = "";              // "luna", "generic_gpt", "unknown"
        public string Label = "";               // display label, e.g. "luna-reserve", "gpt-reserve"
        public string ModelSlug = "";           // upstream normalModelSlug, e.g. "gpt-5.6-luna"
        public List<string> EligibleModels = new List<string>();
        public double? Remaining;              // percent remaining (0.0 .. 100.0)
        public double? ResetEpoch;             // epoch seconds
        public bool IsExpiry;                  // true if expiresAt (fixed expiry), false if recurring cycle
        public int? DurationMinutes;           // e.g. 10080
        public bool Available;                 // whether readable
        public bool Allocated = true;          // whether active allocation exists vs eligible without allocation
        public string GatedBy;
        public string EvidenceQuality = "app-server-ratelimits";

        public bool IsUsable
        {
            get { return Available && Allocated && Remaining.HasValue && Remaining.Value > Model.ZeroRemaining && GatedBy == null; }
        }

        public bool SupportsModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            if (Family != "luna" && Family != "generic_gpt") return false;
            string m = model.Trim();
            if (EligibleModels != null)
                foreach (string em in EligibleModels)
                    if (em.Equals(m, StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrEmpty(ModelSlug) && ModelSlug.Equals(m, StringComparison.OrdinalIgnoreCase);
        }

        public ReservePool Copy()
        {
            var p = new ReservePool
            {
                Id = Id, RawLimitId = RawLimitId, RawLimitName = RawLimitName,
                Family = Family, Label = Label, ModelSlug = ModelSlug,
                Remaining = Remaining, ResetEpoch = ResetEpoch, IsExpiry = IsExpiry,
                DurationMinutes = DurationMinutes, Available = Available, Allocated = Allocated,
                GatedBy = GatedBy, EvidenceQuality = EvidenceQuality
            };
            if (EligibleModels != null) p.EligibleModels.AddRange(EligibleModels);
            return p;
        }
    }

    // Effective account-level availability taking both normal quota and reserve pools into account.
    class AccountAvailability
    {
        public bool RegularAvailable;           // normal quota usable
        public bool GenericReserveAvailable;    // generic gpt reserve usable
        public bool LunaReserveAvailable;       // luna reserve usable
        public bool EffectiveLunaAvailable;     // RegularAvailable || LunaReserveAvailable
        public bool EffectiveGptAvailable;      // RegularAvailable || GenericReserveAvailable
        public string LunaStatusReason = "";
        public List<string> Diagnostics = new List<string>();
    }

    // A one-off credit that refills a spent window on demand. `Id` is the
    // vendor's own handle for it, kept so redeeming names the exact credit
    // instead of "whatever is first".
    class ResetCredits
    {
        public int Available;
        public double? ExpiresEpoch;
        public string Title;
        public string Id;
    }

    // An ABSOLUTE balance, not a percentage. Some vendors meter in credits
    // ("FreeBucks 123.4"), and a credit count has no denominator: turning it
    // into a percent would be an invented number. It rides on the account
    // beside the windows, never INSIDE WindowData (whose Rem is always a
    // percent), so the tray's percentage modes and the low-quota alert cannot
    // mistake it for quota.
    class BalanceData
    {
        public string Id = "";              // stable key, e.g. "freebucks"
        public string Label = "";           // display label, e.g. "FreeBucks"
        public double? Value;               // absolute amount, vendor units
        public string Unit = "";            // display unit, e.g. "credits"
        public double? ResetEpoch;          // next cycle reset, when the vendor states one
        // Optional breakdown as the vendor actually sent it (free/paid/ad/...).
        // Null when the response carried none; never fabricated.
        public List<KeyValuePair<string, double>> Breakdown;
    }

    class ProbeResult
    {
        public List<AccountData> Accounts = new List<AccountData>();
        public List<CliInfo> Clis = new List<CliInfo>();
        public List<string> DuplicateCodexHomes = new List<string>();
        public List<string> UnverifiedCodexHomes = new List<string>();
    }

    static class Model
    {
        public const string OK = "OK";
        public const string STALE = "STALE";
        public const string UNAVAILABLE = "UNAVAILABLE";
        public const string ERROR = "ERROR";

        public const string FIVE_HOUR = "five_hour";
        public const string WEEKLY = "weekly";
        public const string MONTHLY = "monthly";

        // An account can hold several independent pools, each with its OWN
        // weekly/5h pair (Antigravity: Gemini models vs Claude & GPT models).
        // A window key owns an alert rule and its suppression state, so two
        // pools must not collide on the bare name.
        const char GroupSep = '@';

        // UNAVAILABLE with one of these is not a fault: the provider works as
        // designed and simply has nothing to state right now.
        static readonly string[] QuietCodes = { "no_refusal_recorded", "quota_unknown" };

        // An exhausted window reads 0%, not 0.0001%.
        public const double ZeroRemaining = 0.5;

        public static string Qualified(string key, string group)
        {
            return string.IsNullOrEmpty(group) ? key : key + GroupSep + group;
        }

        public static string Base(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int at = key.IndexOf(GroupSep);
            return at < 0 ? key : key.Substring(0, at);
        }

        public static bool Quiet(string code)
        {
            return Array.IndexOf(QuietCodes, code ?? "") >= 0;
        }

        public static int? KnownDuration(string key)
        {
            switch (Base(key))
            {
                case FIVE_HOUR: return 300;
                case WEEKLY: return 10080;
                case MONTHLY: return 43200;
            }
            return null;
        }

        public static string Label(string key)
        {
            string b = Base(key);
            if (b == FIVE_HOUR) return "5h";
            if (b == WEEKLY) return "week";
            if (b == MONTHLY) return "month";
            return b.Replace("_", " ");
        }

        static int Duration(ProbeWindow w)
        {
            if (w.DurationMinutes.HasValue && w.DurationMinutes.Value > 0) return w.DurationMinutes.Value;
            int? known = KnownDuration(w.Key);
            return known.HasValue ? known.Value : -1;
        }

        public static bool Exhausted(ProbeWindow w)
        {
            return w.Available && w.Remaining.HasValue && w.Remaining.Value <= ZeroRemaining;
        }

        // A window whose own reset time already passed is full: no probe is
        // needed to know that, and waiting for the next sweep showed a number
        // the clock had already disproved. A fixed expiry (IsExpiry == true)
        // depletes instead of refilling.
        public static List<ProbeWindow> ApplyElapsedResets(List<ProbeWindow> windows, double now)
        {
            var outList = new List<ProbeWindow>();
            foreach (ProbeWindow w in windows)
            {
                if (!w.Available || !w.ResetEpoch.HasValue
                    || w.ResetEpoch.Value <= 0 || w.ResetEpoch.Value > now)
                { outList.Add(w); continue; }
                ProbeWindow full = w.Copy();
                if (w.IsExpiry)
                {
                    full.Remaining = 0.0;
                    full.ResetEpoch = null;
                    full.GatedBy = "expired";
                    full.AssumedFull = false;
                }
                else
                {
                    full.Remaining = 100.0;
                    full.ResetEpoch = null;     // "resets in -4m" is not a thing
                    full.GatedBy = null;
                    full.AssumedFull = true;
                }
                outList.Add(full);
            }
            return outList;
        }

        // A longer window fully spent makes every shorter window in the SAME
        // pool unusable, whatever the vendor reports for it. Gating never
        // crosses pools: a spent Claude weekly must not zero a Gemini 5h
        // window the user can still spend.
        public static List<ProbeWindow> GateWindows(List<ProbeWindow> windows)
        {
            var outList = new List<ProbeWindow>(windows);
            var blocks = new Dictionary<string, List<KeyValuePair<int, string>>>();
            foreach (ProbeWindow w in outList)
            {
                if (!Exhausted(w)) continue;
                int dur = Duration(w);
                if (dur < 0) continue;
                if (!blocks.ContainsKey(w.Group)) blocks[w.Group] = new List<KeyValuePair<int, string>>();
                blocks[w.Group].Add(new KeyValuePair<int, string>(dur, w.Key));
            }
            if (blocks.Count == 0) return outList;
            foreach (var pool in blocks.Values)
                pool.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key)
                    : string.Compare(a.Value, b.Value, StringComparison.Ordinal));

            for (int i = 0; i < outList.Count; i++)
            {
                ProbeWindow w = outList[i];
                if (!w.Available) continue;
                int mine = Duration(w);
                if (mine < 0) continue;
                List<KeyValuePair<int, string>> pool;
                if (!blocks.TryGetValue(w.Group, out pool)) continue;
                foreach (var block in pool)
                {
                    if (block.Value == w.Key || block.Key <= mine) continue;
                    ProbeWindow gated = w.Copy();
                    gated.Remaining = 0.0;
                    gated.GatedBy = block.Value;
                    outList[i] = gated;
                    break;
                }
            }
            return outList;
        }

        // The one entry point every consumer reads through, so a window can
        // never be refilled in one view and gated in another. Order is
        // load-bearing: a weekly reset lifts its own gate in the same pass, and
        // the 5h window keeps its real number instead of the gate's zero.
        public static List<ProbeWindow> Resolve(List<ProbeWindow> windows, double now)
        {
            return GateWindows(ApplyElapsedResets(windows, now));
        }

        public static AccountAvailability ComputeAvailability(List<WindowData> windows, List<ReservePool> reserves)
        {
            var av = new AccountAvailability();
            if (windows != null)
            {
                bool foundRegular = false;
                av.RegularAvailable = true;
                foreach (WindowData w in windows)
                {
                    if (w.Group.Length != 0) continue;
                    foundRegular = true;
                    if (!w.Available || w.Rem <= 0 || w.GatedBy != null) av.RegularAvailable = false;
                }
                av.RegularAvailable = foundRegular && av.RegularAvailable;
            }
            if (reserves != null)
            {
                foreach (ReservePool r in reserves)
                {
                    if (r.Family == "generic_gpt" && r.IsUsable) av.GenericReserveAvailable = true;
                    if (r.Family == "luna" && r.IsUsable) av.LunaReserveAvailable = true;
                }
            }
            if (windows != null)
            {
                foreach (WindowData w in windows)
                {
                    if (w.GroupLabel == "gpt-reserve" && w.Available && w.Rem > 0 && w.GatedBy == null)
                        av.GenericReserveAvailable = true;
                    if (w.GroupLabel == "luna-reserve" && w.Available && w.Rem > 0 && w.GatedBy == null)
                        av.LunaReserveAvailable = true;
                }
            }
            av.EffectiveLunaAvailable = av.RegularAvailable || av.LunaReserveAvailable;
            av.EffectiveGptAvailable = av.RegularAvailable || av.GenericReserveAvailable;

            if (av.RegularAvailable) av.LunaStatusReason = "normal quota";
            else if (av.LunaReserveAvailable) av.LunaStatusReason = "reserve-backed (luna-reserve)";
            else
            {
                bool hasExhaustedLuna = false;
                if (reserves != null)
                    foreach (ReservePool r in reserves)
                        if (r.Family == "luna") { hasExhaustedLuna = true; break; }
                if (!hasExhaustedLuna && windows != null)
                    foreach (WindowData w in windows)
                        if (w.GroupLabel == "luna-reserve") { hasExhaustedLuna = true; break; }
                av.LunaStatusReason = hasExhaustedLuna ? "luna-reserve exhausted" : "exhausted";
            }
            return av;
        }

        public static AccountAvailability ComputeAvailability(List<ProbeWindow> windows, List<ReservePool> reserves)
        {
            var av = new AccountAvailability();
            if (windows != null)
            {
                bool foundRegular = false;
                av.RegularAvailable = true;
                foreach (ProbeWindow w in Resolve(windows, Stamp.Now))
                {
                    if (w.Group.Length != 0) continue;
                    foundRegular = true;
                    if (!w.Available || !w.Remaining.HasValue || w.Remaining.Value <= ZeroRemaining || w.GatedBy != null)
                        av.RegularAvailable = false;
                }
                av.RegularAvailable = foundRegular && av.RegularAvailable;
            }
            if (reserves != null)
            {
                foreach (ReservePool r in reserves)
                {
                    if (r.Family == "generic_gpt" && r.IsUsable) av.GenericReserveAvailable = true;
                    if (r.Family == "luna" && r.IsUsable) av.LunaReserveAvailable = true;
                }
            }
            if (windows != null)
            {
                foreach (ProbeWindow w in windows)
                {
                    if (w.GroupLabel == "gpt-reserve" && w.Available && w.Remaining.HasValue && w.Remaining.Value > ZeroRemaining && w.GatedBy == null)
                        av.GenericReserveAvailable = true;
                    if (w.GroupLabel == "luna-reserve" && w.Available && w.Remaining.HasValue && w.Remaining.Value > ZeroRemaining && w.GatedBy == null)
                        av.LunaReserveAvailable = true;
                }
            }
            av.EffectiveLunaAvailable = av.RegularAvailable || av.LunaReserveAvailable;
            av.EffectiveGptAvailable = av.RegularAvailable || av.GenericReserveAvailable;

            if (av.RegularAvailable) av.LunaStatusReason = "normal quota";
            else if (av.LunaReserveAvailable) av.LunaStatusReason = "reserve-backed (luna-reserve)";
            else
            {
                bool hasExhaustedLuna = false;
                if (reserves != null)
                    foreach (ReservePool r in reserves)
                        if (r.Family == "luna") { hasExhaustedLuna = true; break; }
                if (!hasExhaustedLuna && windows != null)
                    foreach (ProbeWindow w in windows)
                        if (w.GroupLabel == "luna-reserve") { hasExhaustedLuna = true; break; }
                av.LunaStatusReason = hasExhaustedLuna ? "luna-reserve exhausted" : "exhausted";
            }
            return av;
        }

        // Flatten to what the window/tray draw with. `Rem` is ALWAYS remaining.
        public static AccountData Flatten(ProbeAccount acc, double now)
        {
            var ad = new AccountData
            {
                Provider = acc.Provider, ProviderLabel = acc.ProviderLabel, Name = acc.Name,
                SourceId = acc.SourceId, ResetHome = acc.ResetHome,
                RemoteAccountIdentity = acc.RemoteAccountIdentity,
                RemoteIdentityChecked = acc.RemoteIdentityChecked,
                Status = acc.Status, Plan = acc.Plan, Error = acc.Error,
                Ok = acc.Ok, Quiet = acc.Quiet,
            };
            if (acc.Credits != null)
            {
                ad.ResetCredits = acc.Credits.Available;
                ad.ResetCreditTitle = acc.Credits.Title;
                ad.ResetCreditExpires = Stamp.Iso(acc.Credits.ExpiresEpoch);
                ad.ResetCreditId = acc.Credits.Id;
            }
            // Absolute balances ride the account untouched: an amount with no
            // denominator is copied as fact, never converted to a percent.
            if (acc.Balances != null)
                foreach (BalanceData b in acc.Balances)
                    ad.Balances.Add(b);
            // W2-003: an unverified reset (journal refusal inside the grace)
            // must not be carried forward as the stale blocked card — the
            // reset EVENT lives on this very snapshot.
            ad.ResetUnverified = acc.UnverifiedReset;
            // SRC-028: the typed auth provenance rides through unchanged. It is
            // the ONE thing that lets a consumer tell "the sweep hiccuped" from
            // "the vendor says you are not signed in".
            ad.AuthFailed = acc.AuthFailed;
            ad.AuthFailedClass = acc.AuthFailureClass;
            ad.AuthFailureReason = acc.AuthFailureReason;
            foreach (ProbeWindow w in Resolve(acc.Windows, now))
            {
                // CORE-002: a window that cannot state a number is NOT a window
                // at 0%. `Rem` is an int with no "unknown", so `Available` is
                // what carries the difference — the same shape
                // ProbeWindow.Unavailable already has. A vendor that answers
                // with a window but no percentage (Codex omitting usedPercent,
                // Zcode omitting both remaining and percentage) would otherwise
                // read as fully spent: drawn as 0%, counted as a reading so the
                // last good numbers are NOT carried forward, and low enough to
                // arm a low-quota alert on a number no vendor ever sent.
                bool readable = w.Remaining.HasValue;
                int rem = readable
                    ? (int)Math.Round(Math.Max(0.0, Math.Min(100.0, w.Remaining.Value))) : 0;
                ad.Windows.Add(new WindowData
                {
                    Key = w.Key,
                    Base = Base(w.Key),
                    Label = Label(w.Key),
                    Group = w.Group ?? "",
                    GroupLabel = w.GroupLabel ?? "",
                    Available = w.Available && readable,
                    Rem = rem,
                    // CORE-013: the absolute instant IS the authority and rides
                    // through Flatten untouched; the local string is display
                    // only. Cycle checks, countdowns and notification keys read
                    // the epoch, never a reparse of this rendering.
                    ResetEpoch = w.ResetEpoch,
                    Reset = Stamp.Iso(w.ResetEpoch),
                    GatedBy = w.GatedBy,
                    AssumedFull = w.AssumedFull,
                    DurationMinutes = w.DurationMinutes.HasValue ? w.DurationMinutes.Value : 0,
                });
            }
            if (acc.Reserves != null)
            {
                foreach (var r in acc.Reserves)
                {
                    var rc = r.Copy();
                    if (rc.IsExpiry && rc.ResetEpoch.HasValue && rc.ResetEpoch.Value <= now)
                    {
                        rc.Remaining = 0.0;
                        rc.GatedBy = "expired";
                    }
                    ad.Reserves.Add(rc);
                }
            }
            ad.Availability = ComputeAvailability(ad.Windows, ad.Reserves);
            return ad;
        }
    }

    // NOTE (SRC-009): reserve detection and presentation end here. A
    // reserve-aware MODEL/ACCOUNT ROUTER was drafted here and deleted: LIMISAW
    // has no production operation that selects an account or a model -- the
    // user picks an account, and Connections starts the vendor's own visible
    // sign-in. Routing belongs to whatever consumer actually performs
    // dispatch; inventing a caller here would make dead code look live.
    // `ReservePool.SupportsModel` is the upstream-evidence eligibility check
    // that consumer should use.

    // в”Ђв”Ђ vendor CLI plumbing в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    // Nothing here ever installs anything. `Tools` is data: the exact command,
    // its publisher and where it lands, so the UI can show it and let the user
    // decide. Piping a remote script into a shell must never be implicit.
    static class Cli
    {
        // PERF-007/R030: bounded CLI output. A status-line CLI's output is a
        // few kilobytes; these caps give generous headroom. The stderr bound
        // counts OBSERVED bytes (the retained diagnostic is the first line
        // only), so a pathological child cannot grow the retained buffer.
        public const int ClaudeCliMaxStdout = 2 * 1024 * 1024;
        public const int ClaudeCliMaxStderr = 512 * 1024;
        // PERF-007/R030: bounded retention. The child may emit anything; the
        // parent retains a bounded window and keeps draining the pipe even
        // after the cap is exceeded (stopping is how a parent deadlocks a
        // child on a full buffer). stdout retains up to its cap of lines and
        // marks itself Oversized past that; stderr retains only the FIRST
        // meaningful line (the 160-char user diagnostic) plus a total-bytes
        // counter that turns Oversized past its own bound. Both are locked,
        // because the async stdout/stderr callbacks race.
        class BoundedCapture
        {
            readonly int cap;
            readonly bool firstLineOnly;
            readonly object gate = new object();
            readonly StringBuilder buf = new StringBuilder();
            long observed;
            bool firstLineTaken;
            public bool Oversized { get; private set; }
            public string Text { get { lock (gate) return buf.ToString(); } }

            public BoundedCapture(int capChars, bool firstLineOnly)
            { this.cap = capChars; this.firstLineOnly = firstLineOnly; }

            public void Append(string line)
            {
                if (line == null) return;
                lock (gate)
                {
                    observed += line.Length + 1;
                    if (firstLineOnly)
                    {
                        if (!firstLineTaken && line.Trim().Length > 0)
                        {
                            firstLineTaken = true;
                            string first = line.Trim();
                            buf.Append(first.Length > 160 ? first.Substring(0, 160) : first);
                        }
                        if (observed > cap) Oversized = true;
                        return;                    // continue draining: the callback returns, the pipe stays open
                    }
                    if (Oversized) return;         // drain, never retain more
                    if (buf.Length + line.Length + 1 > cap) { Oversized = true; return; }
                    buf.AppendLine(line);
                }
            }
        }

        public class Tool
        {
            public string Key = "", Binary = "", Label = "";
            public string[] Fallbacks = new string[0];
            public string Command = "", Source = "", Target = "";
        }

        public static readonly Tool[] Tools =
        {
            new Tool {
                Key = "claude", Binary = "claude", Label = "Claude Code CLI",
                Fallbacks = new[] { @"%USERPROFILE%\.local\bin\claude.exe" },
                Command = "irm https://claude.ai/install.ps1 | iex",
                Source = "claude.ai (Anthropic)",
                Target = @"%USERPROFILE%\.local\bin\claude.exe",
            },
            new Tool {
                Key = "antigravity", Binary = "agy", Label = "Antigravity CLI",
                Fallbacks = new[] { @"%LOCALAPPDATA%\agy\bin\agy.exe" },
                Command = "irm https://antigravity.google/cli/install.ps1 | iex",
                Source = "antigravity.google (Google)",
                Target = @"%LOCALAPPDATA%\agy\bin\agy.exe",
            },
            new Tool {
                Key = "codex", Binary = "codex", Label = "Codex CLI",
                Fallbacks = new[] { @"%USERPROFILE%\.local\bin\codex.exe" },
                Command = "powershell -ExecutionPolicy ByPass -c \"irm https://chatgpt.com/codex/install.ps1 | iex\"",
                Source = "chatgpt.com/codex (OpenAI)",
                Target = "on PATH (installer-chosen directory)",
            },
        };

        public static Tool Find(string key)
        {
            foreach (Tool t in Tools) if (t.Key == key) return t;
            return null;
        }

        // PATH first, then the installer's own directory: a CLI installed while
        // LIMISAW runs is NOT on this process's PATH (Windows hands every
        // process a snapshot at launch), so a PATH-only lookup would keep
        // Test seam: the PATH the resolver walks. Production reads the process
        // environment (declared null-shaped default below); a harness replaces
        // it so CLI discovery is deterministic without touching this process's
        // real environment. The connection adapters resolve the LOGIN/VERIFY
        // binary through this same seam.
        internal static Func<string> ResolvePathOverride = null;

        // reporting "not installed" until the app restarts.
        public static string Resolve(string key)
        {
            Tool tool = Find(key);
            // PERF-003 (SRC-006:R020): inside a built discovery generation the
            // executable comes from the snapshot — provider probes, Cli.Status
            // and the projection share ONE resolution per generation.
            if (ResolvePathOverride == null)
            {
                string snap = ExecutableDiscovery.SnapshotResolved(key);
                if (snap != null) return snap;
            }
            ExecutableDiscovery.ResolveWalks++;
            string binary = tool != null ? tool.Binary : key;
            string[] exts = { ".exe", ".cmd", ".bat", "" };
            string path = ResolvePathOverride != null ? ResolvePathOverride() : Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                foreach (string ext in exts)
                {
                    string candidate;
                    try { candidate = Path.Combine(dir.Trim('"'), binary + ext); }
                    catch { continue; }
                    if (File.Exists(candidate)) return candidate;
                }
            }
            if (tool != null)
                foreach (string raw in tool.Fallbacks)
                {
                    string candidate = Environment.ExpandEnvironmentVariables(raw);
                    if (File.Exists(candidate)) return candidate;
                }
            return "";
        }

        public class Result
        {
            public bool Ok;
            public string Stdout = "", Error = "";
        }

        internal static bool IsBatchShim(string exe)
        {
            string ext = Path.GetExtension(exe ?? "");
            return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        }

        internal static string BatchShellArgs(string shim, string inner)
        {
            return "/d /s /c \" " + Quote(shim) + (inner.Length > 0 ? " " + inner : "") + " \"";
        }

        // Run a CLI under an absolute deadline. Never throws, never blocks past
        // the deadline, never opens a console window. Batch shims (.cmd/.bat)
        // are routed through ComSpec so npm-style launchers are launchable.
        public static Result Run(string exe, string[] args, double deadline, string cwd)
        {
            return Run(exe, args, deadline, cwd, null);
        }

        // `env` scopes the CHILD to one account: a value sets the variable, a
        // null value removes it. This process's own environment is never
        // touched, so two homes can be probed by two children - including at the
        // same time - without either one seeing the other's identity.
        public static Result Run(string exe, string[] args, double deadline, string cwd,
            IDictionary<string, string> env)
        {
            var res = new Result();
            double remaining = deadline - Stamp.Now;
            if (remaining <= 0.1) { res.Error = "deadline_exceeded"; return res; }
            string inner = "";
            foreach (string a in args) inner += (inner.Length > 0 ? " " : "") + Quote(a);
            bool batch = IsBatchShim(exe);
            var psi = new ProcessStartInfo(batch ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") : exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                Arguments = batch ? BatchShellArgs(exe, inner) : inner,
            };
            if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
            if (env != null)
                foreach (KeyValuePair<string, string> pair in env)
                {
                    if (pair.Value == null) psi.EnvironmentVariables.Remove(pair.Key);
                    else psi.EnvironmentVariables[pair.Key] = pair.Value;
                }
            Process proc = null;
            // One job per invocation: proc.Kill() ends this process only, so a
            // vendor helper or grandchild would outlive the timeout and stack up
            // across retries. Disposing the scope ends the whole tree, and its
            // kill-on-close handle does the same if the app dies mid-sweep.
            ChildSweeper.Scope scope = ChildSweeper.Open();
            try
            {
                // PERF-007/R030: retained output is BOUNDED — stdout keeps up
                // to its cap of retained lines, stderr keeps only the first
                // diagnostic line, and BOTH keep draining the pipe after the
                // cap is exceeded (stopping is the one way to deadlock the
                // child on a full buffer).
                var stdout = new BoundedCapture(ClaudeCliMaxStdout, false);
                var stderr = new BoundedCapture(ClaudeCliMaxStderr, true);
                proc = Process.Start(psi);
                if (proc == null) { res.Error = "could not start " + Path.GetFileName(exe); return res; }
                scope.Adopt(proc);
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.Append(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.Append(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                int budget = (int)Math.Max(100, Math.Min(remaining, 60.0) * 1000);
                if (!proc.WaitForExit(budget))
                {
                    try { proc.Kill(); } catch { }
                    res.Error = "timeout";
                    return res;
                }
                proc.WaitForExit();     // flush the async readers
                // R030: the cap exists BEYOND the diagnostic truncation.
                if (stdout.Oversized || stderr.Oversized) { res.Error = "response_too_large"; return res; }
                res.Stdout = stdout.Text;
                if (proc.ExitCode != 0)
                {
                    // stderr can carry an auth hint ("Not logged in"); that is a
                    // vendor message about the user's own account, never a secret.
                    // The bounded stderr capture already owns the first line, so
                    // there is no need to split and search.
                    res.Error = stderr.Text.Length > 0 ? stderr.Text : "exit " + proc.ExitCode;
                    return res;
                }
                res.Ok = true;
                return res;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name; return res; }
            finally
            {
                if (proc != null) proc.Dispose();
                scope.Dispose();   // ends the tree; a clean exit already left it empty
            }
        }

        internal static string Quote(string arg)
        {
            // Quoting also arms the cmd interpreter for the batch-shim route: a
            // bare `&`/`|`/`<`/`>`/`^`/`(`/`)` would be a command operator there,
            // never an argument. Quoting them is harmless for direct .exe starts.
            if (arg.Length > 0 && arg.IndexOfAny(QuoteChars) < 0) return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        internal static readonly char[] QuoteChars =
            { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', '%' };

        // The pipeline that goes INSIDE one PowerShell session: OpenAI publishes
        // its command already wrapped in `powershell -c "..."`, and the UI must
        // never nest one shell in another.
        static readonly Regex Inner = new Regex("-c(?:ommand)?\\s+\"(.+)\"\\s*$", RegexOptions.IgnoreCase);

        public static List<CliInfo> Status()
        {
            var outList = new List<CliInfo>();
            foreach (Tool tool in Tools)
            {
                string path = Resolve(tool.Key);
                Match m = Inner.Match(tool.Command);
                outList.Add(new CliInfo
                {
                    Key = tool.Key, Label = tool.Label, Installed = path.Length > 0, Path = path,
                    Command = tool.Command,
                    PowerShell = m.Success ? m.Groups[1].Value : tool.Command,
                    Source = tool.Source, Target = tool.Target,
                });
            }
            return outList;
        }
    }

    // в”Ђв”Ђ Codex: structured usage over the app-server's JSON-RPC в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    // `codex app-server --stdio`, one child per CODEX_HOME, windows mapped by
    // their reported duration and never by primary/secondary position. A
    // missing bucket stays unavailable; it is never faked as zero.
    static class CodexSource
    {
        static readonly object IdentityGate = new object();
        static readonly Dictionary<string, string> KnownRemote = new Dictionary<string, string>();
        static readonly HashSet<string> PendingDistinct = new HashSet<string>();
        static readonly Dictionary<string, string> PendingAuthRevision = new Dictionary<string, string>();
        static readonly HashSet<string> KnownDuplicateHomes = new HashSet<string>();
        static readonly Dictionary<string, string> AuthRevisions = new Dictionary<string, string>();

        internal static void AwaitDistinctAccount(string homeId, string home)
        {
            string revision = AuthRevision(home);
            lock (IdentityGate)
            {
                PendingDistinct.Add(homeId);
                PendingAuthRevision[homeId] = revision;
            }
        }
        internal static void CancelDistinctAccount(string homeId)
        {
            lock (IdentityGate)
            {
                PendingDistinct.Remove(homeId);
                PendingAuthRevision.Remove(homeId);
            }
        }
        internal static bool LoginChangedAuth(string homeId, string home)
        {
            string current = AuthRevision(home);
            lock (IdentityGate)
            {
                string before;
                return !PendingAuthRevision.TryGetValue(homeId, out before) || current != before;
            }
        }
        internal static bool IsKnownDuplicateHome(string homeId)
        { lock (IdentityGate) return KnownDuplicateHomes.Contains(homeId); }

        // A duplicate found by interactive verification, before the next sweep
        // recomputes the set. Without this the retry path would have no home to
        // reuse until a refresh happened to run.
        internal static void MarkDuplicateHome(string homeId)
        { lock (IdentityGate) { if (!string.IsNullOrEmpty(homeId)) KnownDuplicateHomes.Add(homeId); } }

        // A home that just verified as a DISTINCT remote account is not a
        // duplicate any more, whatever an earlier attempt in the same home was.
        internal static void ClearDuplicateHome(string homeId)
        { lock (IdentityGate) KnownDuplicateHomes.Remove(homeId); }

        internal static string KnownRemoteIdentity(string homeId)
        { lock (IdentityGate) { string value; return KnownRemote.TryGetValue(homeId, out value) ? value : ""; } }

        // The installed app-server v2 schema describes accountId as the
        // backend account associated with this usage snapshot. A nullable ID
        // is not evidence that two homes differ. account/read's ChatGPT email
        // is a lower-strength fallback; only its hash crosses this boundary.
        internal static string RemoteIdentity(object rateLimitResult, object accountReadResult)
        {
            string id = J.Str(J.Get(rateLimitResult, "accountId"));
            if (!string.IsNullOrWhiteSpace(id)) return IdentityDigest("account:", id.Trim());
            object account = J.Get(accountReadResult, "account");
            if (J.Str(J.Get(account, "type")) != "chatgpt") return "";
            string email = J.Str(J.Get(account, "email"));
            if (string.IsNullOrWhiteSpace(email)) return "";
            return IdentityDigest("email:", email.Trim().ToLowerInvariant());
        }

        static string IdentityDigest(string kind, string value)
        {
            byte[] hash = System.Security.Cryptography.SHA256.Create()
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(kind + value));
            var sb = new System.Text.StringBuilder(kind.Length + 64);
            sb.Append(kind);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        internal static List<ProbeAccount> DistinctRemoteAccounts(List<ProbeAccount> homes,
            List<string> duplicates, List<string> unverified)
        {
            var owners = new HashSet<string>(StringComparer.Ordinal);
            var ownerKinds = new HashSet<string>(StringComparer.Ordinal);
            var usable = new List<ProbeAccount>();
            bool codexPublished = false, publishedUnknown = false;
            lock (IdentityGate)
            {
                KnownDuplicateHomes.Clear();
                foreach (ProbeAccount home in homes)
                {
                    if (home.Provider != "codex") { usable.Add(home); continue; }
                    string remote = home.RemoteAccountIdentity ?? "";
                    if (remote.Length > 0 && owners.Contains(remote))
                    {
                        duplicates.Add(home.SourceId);
                        KnownDuplicateHomes.Add(home.SourceId);
                        continue;
                    }
                    if (PendingDistinct.Contains(home.SourceId)
                        && !LoginChangedAuth(home.SourceId, home.ResetHome))
                    {
                        unverified.Add(home.SourceId);
                        continue;
                    }
                    if (remote.Length == 0)
                    {
                        unverified.Add(home.SourceId);
                        // Keep one local home visible with an explicit
                        // unverified note. A second unreadable home cannot be
                        // asserted as an independent remote account.
                        if (codexPublished || PendingDistinct.Contains(home.SourceId)) continue;
                        publishedUnknown = true;
                    }
                    else
                    {
                        string kind = remote.Substring(0, remote.IndexOf(':'));
                        if (publishedUnknown || (ownerKinds.Count > 0 && !ownerKinds.Contains(kind)))
                        {
                            // An accountId and an email hash cannot be
                            // compared. Keep the home local, report it as
                            // unverified, and avoid claiming a second card.
                            unverified.Add(home.SourceId);
                            continue;
                        }
                        owners.Add(remote);
                        ownerKinds.Add(kind);
                        CancelDistinctAccount(home.SourceId);
                    }
                    usable.Add(home);
                    codexPublished = true;
                }
            }
            return usable;
        }

        internal static void RememberRemoteIdentity(string homeId, string remote)
        {
            lock (IdentityGate)
            {
                if (!string.IsNullOrEmpty(remote)) KnownRemote[homeId] = remote;
                else KnownRemote.Remove(homeId);
            }
        }

        // A re-login can replace auth.json while an app-server is warm. The
        // file's metadata, never its contents, invalidates that home session
        // so the next read cannot report the previous browser identity.
        internal static void RefreshAuthSession(string home)
        {
            string id = HomeId(home);
            string revision = AuthRevision(home);
            bool changed = false;
            lock (IdentityGate)
            {
                string old;
                if (AuthRevisions.TryGetValue(id, out old) && old != revision)
                {
                    changed = true;
                    KnownRemote.Remove(id);
                }
                AuthRevisions[id] = revision;
            }
            if (changed) Pool.EvictHome(home);
        }

        static string AuthRevision(string home)
        {
            try
            {
                var file = new FileInfo(Path.Combine(home, "auth.json"));
                if (file.Exists) return file.Length.ToString() + ":" + file.LastWriteTimeUtc.Ticks.ToString();
            }
            catch { }
            return "missing";
        }

        // A newly authenticated home is publishable only after every other
        // authenticated home has a comparable vendor identity. Unknown is not
        // "distinct". The caller distinguishes duplicate from still waiting.
        internal static bool IsDistinctFromKnownHomes(string homeId, string remote, out bool duplicate)
        {
            duplicate = false;
            if (string.IsNullOrEmpty(remote)) return false;
            bool unknown = false;
            List<CodexHome> homes = Homes();
            foreach (CodexHome home in homes)
                if (home.Id != homeId) RefreshAuthSession(home.Path);
            lock (IdentityGate)
            {
                foreach (CodexHome home in homes)
                {
                    if (home.Id == homeId) continue;
                    string other;
                    if (!KnownRemote.TryGetValue(home.Id, out other) || other.Length == 0)
                    { unknown = true; continue; }
                    if (other == remote) duplicate = true;
                    else if (other.Substring(0, other.IndexOf(':')) != remote.Substring(0, remote.IndexOf(':'))) unknown = true;
                }
            }
            return !duplicate && !unknown;
        }
        // PERF-001: a cold `codex app-server` answers `initialize` only after a
        // measured 14-19 s (ProbeAntigravity records the observation), so a
        // cold attempt granted less than the LOW end of that range is a
        // scheduled failure, not a fast one. 14.0 is the proven floor, not the
        // observed maximum — a home that cannot receive a viable slice is
        // returned as stale instead of being handed a doomed budget.
        public const double ColdStartFloorSeconds = 14.0;

        // PERF-001 cross-sweep cold fairness: the index of the cold home that
        // receives the first viable attempt of the next sweep. Advancing by
        // the number of homes served is what rotates the queue: two viable
        // starts per sweep over five cold homes reaches every one of them in
        // three sweeps, and no cold home monopolizes the budget.
        internal static int ColdCursor;

        // PERF-001 scheduling clock seam. The scheduler must read the same
        // clock the fake session harness advances, so a fake cold start
        // consumes virtual seconds no real timer can produce deterministically.
        // Production leaves it null and reads the real Stamp.Now.
        internal static Func<double> Clock = null;
        static double NowS() { Func<double> c = Clock; return c != null ? c() : Stamp.Now; }

        // PERF-001: reset only by tests; production state is the process.
        internal static void ResetScheduling() { ColdCursor = 0; }

        public static List<ProbeAccount> Sweep(double deadline, double perAccount)
        {
            // CORE-003: discovery is separated from probing. The full target
            // set is established FIRST so the available time is shared across
            // every discovered home instead of whoever sorts early eating the
            // whole provider budget. A home that gets no probing time is still
            // a SLOT (stale, unreadable), so CarryForward can keep its last
            // good windows instead of the card disappearing for a sweep.
            List<CodexHome> homes = Homes();
            DisambiguateLabels(homes);
            // PERF-001: a session belongs to an exact home. A home that is no
            // longer discovered would keep its child process alive forever, so
            // discovery is also the pool's eviction list.
            var liveKeys = new List<string>();
            foreach (CodexHome h in homes) liveKeys.Add(SessionPool.Key(h.Path));
            Pool.RetainOnly(liveKeys);

            // PERF-001: warm and cold homes are different jobs. A warm home
            // needs one cheap rateLimits read; a cold home needs at least
            // ColdStartFloorSeconds before `initialize` can possibly answer.
            // Equal subdivision gives every home a slice below that floor the
            // moment there are several homes — every attempt doomed, no
            // account initialized. Warm work goes first (cheap, responsive,
            // and it may leave MORE time for the cold queue), then each cold
            // home receives a full viable slice only while the provider's own
            // deadline still affords one; the rest are returned as stale slots
            // without launching a doomed process. The cursor starts where the
            // previous sweep's served count left the queue, so the cold homes
            // that missed out are first in line next time.
            // PERF-001: warm and cold homes are different jobs. A warm home
            // needs one cheap rateLimits read; a cold home needs at least
            // ColdStartFloorSeconds before `initialize` can possibly answer.
            // Equal subdivision gives every home a slice below that floor the
            // moment there are several homes — every attempt doomed, no
            // account initialized. Warm work goes first (cheap, responsive,
            // and it may leave MORE time for the cold queue), then each cold
            // home receives a full viable slice only while the provider's own
            // deadline still affords one; the rest are returned as stale slots
            // without launching a doomed process. The cursor starts where the
            // previous sweep's served count left the queue, so the cold homes
            // that missed out are first in line next time.
            var warm = new List<CodexHome>();
            var cold = new List<CodexHome>();
            foreach (CodexHome h in homes)
                if (Pool.IsWarm(h.Path)) warm.Add(h); else cold.Add(h);
            if (cold.Count == 0) ColdCursor = 0;
            else if (ColdCursor >= cold.Count) ColdCursor %= cold.Count;

            var results = new Dictionary<string, ProbeAccount>(homes.Count);

            foreach (CodexHome home in warm)
            {
                double start = NowS();
                if (start >= deadline)
                {
                    results[home.Id] = Unreadable(home, "deadline_exceeded",
                        "sweep time ran out before this home could be probed");
                    continue;
                }
                results[home.Id] = Probe(home.Path, home.Name, home.Id,
                    start + Math.Min(perAccount, deadline - start));
            }

            int served = 0;
            for (int i = 0; i < cold.Count; i++)
            {
                CodexHome home = cold[(ColdCursor + i) % cold.Count];
                double start = NowS();
                double left = deadline - start;
                if (left < ColdStartFloorSeconds)
                {
                    // Not a failure of this home: a launch the remaining time
                    // cannot carry would burn the budget and still read as
                    // timeout. Stale keeps the last good windows visible.
                    results[home.Id] = Unreadable(home, "cold_start_underfunded",
                        "not enough sweep time left for a viable cold start — "
                        + "later sweeps rotate this home back in");
                    continue;
                }
                // A cold attempt gets the FULL viable slice: the measured
                // floor plus whatever the per-account cap allows, never a
                // fraction of what initialization alone already costs.
                double budget = Math.Min(perAccount, left);
                double end = start + budget;
                results[home.Id] = Probe(home.Path, home.Name, home.Id, end);
                served++;
                // The virtual clock the fake harness advances consumed the
                // budget; the real clock did the same by waiting.
                start = NowS();
                if (start >= end) continue;
            }
            ColdCursor = cold.Count > 0 ? (ColdCursor + served) % cold.Count : 0;

            // Core discovery order, so card ordering never depends on how the
            // scheduler classified the homes.
            var accounts = new List<ProbeAccount>(homes.Count);
            foreach (CodexHome h in homes) accounts.Add(results[h.Id]);
            return accounts;
        }

        // A discovered home that the budget never reached. Not an error card —
        // it carries no reading, so CarryForward attaches the previous sweep's
        // windows to it. An account that genuinely disappears still vanishes,
        // because only a missing discoverable home is omitted.
        static ProbeAccount Unreadable(CodexHome home, string code, string detail)
        {
            var acc = new ProbeAccount
            {
                Provider = "codex", ProviderLabel = "Codex", Name = home.Name,
                SourceId = home.Id, ResetHome = home.Path,
                Status = Model.STALE, Ok = false, Error = Trim(detail ?? code),
            };
            return acc;
        }

        // A discovered home with a stable id. `Path` is the exact canonical
        // home, `Name` is display text only. Two distinct homes may share a name
        // (CODEX_HOME -> "Codex" and the default .codex -> "Codex"); only `Id`
        // distinguishes them.
        public class CodexHome
        {
            public string Path, Name, Id;
            public CodexHome(string path, string name, string id)
            { Path = path; Name = name; Id = id; }
        }

        // Two "Codex" cards are unusable and look broken. The label is NOT the
        // identity — this is display-only, applied uniformly to every duplicate
        // so it never reads as "the first one is the real Codex".
        static void DisambiguateLabels(List<CodexHome> homes)
        {
            for (int i = 0; i < homes.Count; i++)
            {
                int matches = 0;
                for (int j = 0; j < homes.Count; j++)
                    if (homes[j].Name == homes[i].Name) matches++;
                if (matches < 2) continue;
                string trailing = " В· " + Path.GetFileName(homes[i].Path.TrimEnd('\\', '/'));
                if (!homes[i].Name.EndsWith(trailing))
                    homes[i].Name = homes[i].Name + trailing;
            }
        }

        // Only *lists* candidate homes: fast, no subprocess. A directory
        // without auth.json is not a Codex home.
        public static List<CodexHome> Homes()
        {
            var found = new List<CodexHome>();
            var seen = new List<string>();
            string profile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(profile)) return found;

            Action<string, string> add = (dir, name) =>
            {
                if (string.IsNullOrEmpty(dir)) return;
                string full;
                try { full = Path.GetFullPath(dir); } catch { return; }
                string norm = full.TrimEnd('\\', '/').ToLowerInvariant();
                if (seen.Contains(norm)) return;
                if (!Directory.Exists(full) || !File.Exists(Path.Combine(full, "auth.json"))) return;
                seen.Add(norm);
                found.Add(new CodexHome(full, name, HomeId(full)));
            };

            add(Path.Combine(profile, ".codex"), "Codex");
            // The default home is the established account when an explicit
            // CODEX_HOME happens to point at a later duplicate. Connection
            // targeting still honours CODEX_HOME independently.
            add(Environment.GetEnvironmentVariable("CODEX_HOME"), "Codex");
            string[] siblings;
            try { siblings = Directory.GetDirectories(profile, ".codex-*"); }
            catch { siblings = new string[0]; }
            Array.Sort(siblings, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in siblings)
            {
                string label = Path.GetFileName(dir).Replace(".codex-", "").Replace("_", " ");
                add(dir, label.Length > 0 ? TitleCase(label) : "Codex");
            }
            return found;
        }

        // A 64-bit digest of the canonical (case-insensitive, separator-trimmed)
        // home path. Identical on every run; distinct for distinct homes; never
        // derived from the display name.
        static string HomeId(string full)
        {
            string norm = full.TrimEnd('\\', '/').ToLowerInvariant();
            byte[] bytes = System.Security.Cryptography.SHA256.Create()
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(norm));
            var sb = new System.Text.StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        // An account's display name is half of its tray-item id, so the casing
        // rule is load-bearing: change it and every saved tray selection loses
        // the account it pointed at. This is the rule the ids were minted with —
        // the first letter after any non-letter is capitalised, so
        // ".codex-account3free" is "Account3Free".
        static string TitleCase(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            bool boundary = true;
            foreach (char c in text)
            {
                sb.Append(boundary ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                boundary = !char.IsLetter(c);
            }
            return sb.ToString();
        }

        static ProbeAccount Fail(string name, string id, string home, string code, string detail)
        {
            var acc = new ProbeAccount
            {
                Provider = "codex", ProviderLabel = "Codex", Name = name,
                SourceId = id, ResetHome = home,
                Status = Model.ERROR, Ok = false, Error = Trim(detail ?? code),
            };
            acc.Windows.Add(ProbeWindow.Unavailable(Model.FIVE_HOUR));
            acc.Windows.Add(ProbeWindow.Unavailable(Model.WEEKLY));
            return acc;
        }

        static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length > 160 ? s.Substring(0, 160) : s;
        }

        // ── SRC-028: ONE deterministic classification of a structured Codex
        // `account/rateLimits/read` error ─────────────────────────────────────
        // The observed failure is a JSON-RPC error object whose OUTER code is
        // -32603 and whose nested message reports the rate-limit fetch failing
        // with HTTP 401 Unauthorized against chatgpt.com/backend-api/wham/usage.
        // -32603 is only a wrapper — the app-server uses it for quota-unavailable,
        // transient and auth failures alike — so the outer code is NEVER on its
        // own evidence of anything. Everything below reads the nested
        // message/details, which is the only place the actionable half lives.
        public enum RateLimitFailure
        {
            None = 0,         // no structured error at all
            AuthRejected,     // explicit 401/403, unauthorized, not-logged-in
            AuthExpired,      // the same, with explicit expiry evidence too
            QuotaUnavailable, // authenticated and accepted, no quota surface
            Transient,        // service/network/temporary failure
            Protocol,         // malformed or unrecognized error shape
        }

        // Credential shapes that must never reach a Reason, a card or a log.
        // Sanitizing at the boundary is the only way to be sure: the error text
        // is vendor-controlled, so no consumer can be trusted to scrub it.
        static readonly Regex[] CredentialShapes =
        {
            new Regex(@"(?i)bearer\s+[A-Za-z0-9._\-]{4,}", RegexOptions.Compiled),
            new Regex(@"(?i)\b(access_token|refresh_token|id_token|api_key|apikey|authorization|password|secret|credential)\b\s*[""']?\s*[:=]\s*[""']?[^\s""',;]+", RegexOptions.Compiled),
            new Regex(@"\bsk-[A-Za-z0-9_\-]{4,}", RegexOptions.Compiled),
            new Regex(@"\beyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{2,}\.[A-Za-z0-9_\-]{2,}", RegexOptions.Compiled),
        };

        // Never emits a credential, and never grows unbounded: the sanitized
        // form is what every downstream Reason, card note and diagnostic gets.
        public static string SanitizeErrorText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text;
            foreach (Regex shape in CredentialShapes) s = shape.Replace(s, "<redacted>");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > 240 ? s.Substring(0, 240) : s;
        }

        // An HTTP status TOKEN, never a digit run inside an id or a reset stamp:
        // the word boundaries are what keep "-32603" and "4500003601" out.
        static readonly Regex AuthStatusShape = new Regex(@"\b(401|403)\b", RegexOptions.Compiled);

        // Ordered, documented, and pure: the same error object always yields the
        // same class. Expiry is decided BEFORE generic rejection so the more
        // specific verdict wins, and an expiry marker WITHOUT an auth marker
        // never becomes an auth verdict (a quota window can "expire" too).
        public static RateLimitFailure ClassifyRateLimitError(object error)
        {
            if (error == null) return RateLimitFailure.None;
            string text = SanitizeErrorText(J.Write(error));
            if (text.Length == 0) return RateLimitFailure.Protocol;
            string t = text.ToLowerInvariant();

            // 1. AUTHENTICATION. An explicit transport refusal of the
            //    authenticated read. This is the ONLY evidence that may turn a
            //    carried-forward reading into "not currently usable".
            bool authEvidence =
                   AuthStatusShape.IsMatch(t)
                || t.Contains("unauthorized")
                || t.Contains("unauthenticated")
                || t.Contains("not logged in")
                || t.Contains("not signed in")
                || t.Contains("login required")
                || t.Contains("sign in required")
                || t.Contains("sign-in required")
                || t.Contains("authentication required")
                || t.Contains("auth required")
                || t.Contains("authentication failed")
                || t.Contains("auth rejected")
                || t.Contains("reauthenticate")
                || t.Contains("re-authenticate")
                || t.Contains("invalid token")
                || t.Contains("token rejected")
                || t.Contains("credential rejected")
                || t.Contains("forbidden");
            if (authEvidence)
            {
                bool expiry = t.Contains("expired") || t.Contains("expiry")
                    || t.Contains("no longer valid") || t.Contains("revoked")
                    || t.Contains("has elapsed");
                return expiry ? RateLimitFailure.AuthExpired : RateLimitFailure.AuthRejected;
            }

            // 2. QUOTA UNAVAILABLE / UNSUPPORTED. The authenticated request was
            //    ACCEPTED and simply exposes no quota surface. This is what
            //    every structured rate-limit error used to be assumed to be,
            //    and it is still the right answer when there is no auth refusal.
            if (t.Contains("no quota") || t.Contains("quota not") || t.Contains("quota unavailable")
                || t.Contains("quota_unavailable") || t.Contains("not exposed")
                || t.Contains("no subscription") || t.Contains("subscription quota")
                || t.Contains("unsupported") || t.Contains("not supported")
                || t.Contains("no rate limits") || t.Contains("rate limits not")
                || t.Contains("no usage") || t.Contains("usage not"))
                return RateLimitFailure.QuotaUnavailable;

            // 3. TRANSIENT / SERVICE / NETWORK. Temporary by construction, so a
            //    caller may retry without changing anything about the account.
            if (t.Contains("timeout") || t.Contains("timed out") || t.Contains("deadline")
                || t.Contains("temporarily") || t.Contains("try again")
                || t.Contains("connection reset") || t.Contains("connection refused")
                || t.Contains("econnreset") || t.Contains("econnrefused")
                || t.Contains("etimedout") || t.Contains("enotfound")
                || t.Contains("socket") || t.Contains("network")
                || t.Contains("internal error") || t.Contains("internal_server_error")
                || t.Contains("overloaded") || t.Contains("rate limited")
                || t.Contains("too many requests") || t.Contains("service unavailable")
                || t.Contains("bad gateway") || t.Contains("gateway timeout"))
                return RateLimitFailure.Transient;

            // 4. Anything else is an unrecognized shape. Never guessed into a
            //    stronger verdict than the evidence supports.
            return RateLimitFailure.Protocol;
        }

        public static bool IsAuthFailure(RateLimitFailure cls)
        {
            return cls == RateLimitFailure.AuthRejected || cls == RateLimitFailure.AuthExpired;
        }

        // The sanitized, actionable, human sentence for an auth verdict. Names
        // the transport status only when the vendor actually supplied one.
        public static string AuthFailureReason(RateLimitFailure cls, object error)
        {
            string text = SanitizeErrorText(J.Write(error));
            Match m = AuthStatusShape.Match(text ?? "");
            string status = m.Success ? m.Value : "";
            string head = cls == RateLimitFailure.AuthExpired
                ? "authentication expired" : "authentication required";
            if (status == "401") return head + " (401 Unauthorized)";
            if (status.Length > 0) return head + " (HTTP " + status + ")";
            return head;
        }

        static ProbeAccount Probe(string home, string name, string id, double deadline)
        {
            RefreshAuthSession(home);
            string exe = ResolveExe("codex");
            if (exe.Length == 0) return Fail(name, id, home, "cli_not_installed", "Codex CLI not found on PATH");
            // PERF-001: the app-server of a stable home is REUSED across sweeps.
            // Starting it costs a measured 14-19s cold start, so a sweep that
            // starts a fresh child per home per run paid that tax every time.
            // The pool hands back the live session for this exact canonical
            // home (already initialized, so a warm sweep is one read), starts
            // a replacement only when the child died, and is the seam the
            // session harness drives without any real `codex` on the machine.
            SessionPool.SessionLease lease = Pool.Checkout(home, exe, deadline);
            if (lease == null) return Fail(name, id, home, "spawn_failed", "codex app-server did not start");
            using (lease)
            {
                SessionPool.Entry entry = lease.Entry;
                try
                {
                    if (!entry.InitDone)
                    {
                        // Each call may use whatever is LEFT of the deadline. The
                        // first app-server of a session pays a cold start (measured
                        // 14-19s), so a fixed per-call cap threw away time the
                        // caller had already granted and read as "this account is
                        // broken".
                        object init = entry.Link.Call("initialize", new Dictionary<string, object> {
                            { "clientInfo", new Dictionary<string, object> { { "name", "limisaw" }, { "version", "1.0.0" } } },
                            { "capabilities", null },
                        }, deadline);
                        if (init == null) { lease.Retire(); return Fail(name, id, home, "timeout", "codex app-server did not answer initialize"); }
                        if (J.Get(init, "error") != null) { lease.Retire(); return Fail(name, id, home, "initialize_error", J.Write(J.Get(init, "error"))); }
                        entry.Link.Notify("initialized", null);
                        entry.InitDone = true;
                    }
                    object rl = entry.Link.Call("account/rateLimits/read", null, deadline);
                    if (rl == null) { lease.Retire(); return Fail(name, id, home, "timeout", "codex app-server did not answer rateLimits"); }
                    object rlErr = J.Get(rl, "error");
                    if (rlErr != null)
                    {
                        // A structured error is a HEALTHY session answering for an
                        // account in a state we do not like; the child stays warm.
                        // SRC-028: the text is sanitized before it can reach an
                        // Error, a card or a log, and an explicit authentication
                        // rejection is carried as TYPED provenance instead of
                        // being flattened into "the read failed".
                        RateLimitFailure cls = ClassifyRateLimitError(rlErr);
                        ProbeAccount failed = Fail(name, id, home, "rate_limits_error",
                            SanitizeErrorText(J.Write(rlErr)));
                        if (IsAuthFailure(cls))
                        {
                            failed.AuthFailed = true;
                            failed.AuthFailureClass = cls;
                            failed.AuthFailureReason = AuthFailureReason(cls, rlErr);
                            failed.Error = failed.AuthFailureReason;
                        }
                        return failed;
                    }

                    object result = J.Get(rl, "result") ?? new Dictionary<string, object>();
                    object accountRead = null;
                    if (string.IsNullOrWhiteSpace(J.Str(J.Get(result, "accountId"))) && Stamp.Now < deadline)
                    {
                        object reply = entry.Link.Call("account/read", new Dictionary<string, object>(), deadline);
                        if (reply != null && J.Get(reply, "error") == null)
                            accountRead = J.Get(reply, "result");
                    }
                    string remoteIdentity = RemoteIdentity(result, accountRead);
                    RememberRemoteIdentity(id, remoteIdentity);
                    string plan;
                    List<ReservePool> reserves;
                    List<ProbeWindow> windows = ParseWindows(result, out plan, out reserves);
                    var acc = new ProbeAccount
                    {
                        Provider = "codex", ProviderLabel = "Codex", Name = name,
                        SourceId = id, ResetHome = home,
                        RemoteAccountIdentity = remoteIdentity,
                        RemoteIdentityChecked = true,
                        Status = Model.OK, Ok = true, Plan = plan,
                        // The same payload that carries the windows carries the
                        // banked resets; reading one and dropping the other is how
                        // "you have 1 reset available" stayed invisible here.
                        Credits = ParseResetCredits(result),
                        Reserves = reserves,
                    };
                    if (windows.Count == 0)
                    {
                        windows.Add(ProbeWindow.Unavailable(Model.FIVE_HOUR));
                        windows.Add(ProbeWindow.Unavailable(Model.WEEKLY));
                    }
                    acc.Windows = windows;
                    acc.Availability = Model.ComputeAvailability(windows, reserves);
                    return acc;
                }
                catch (Exception ex) { lease.Retire(); return Fail(name, id, home, "probe_exception", ex.GetType().Name + ": " + ex.Message); }
            }
        }

        // Codex varies the window set by plan: Plus reports 300 (5h) + 10080
        // (weekly), Free a single 43200 (30-day). An unrecognised duration is
        // still real quota, so it keeps a generic key instead of vanishing.
        public static string DurationLabel(double? minutes)
        {
            if (!minutes.HasValue) return null;
            int n = (int)Math.Round(minutes.Value);
            if (n <= 0) return null;
            if (n == 300) return Model.FIVE_HOUR;
            if (n == 10080) return Model.WEEKLY;
            if (n == 43200) return Model.MONTHLY;
            return "window_" + n + "m";
        }

        // Every window the server reported, shortest first.
        //
        // Windows are grouped by POOL, not merged by duration. `rateLimits` is
        // the default pool; `rateLimitsByLimitId` names the rest, and a plan can
        // hold more than one — measured live: `codex` (5h + weekly) alongside
        // `base_model_inference` ("gpt-reserve", its own weekly with its own
        // reset). Keying only on duration made the second weekly collide with the
        // first and lose to first-match-wins, so a whole reserve pool was
        // silently discarded. Pool-qualified keys are the same mechanism
        // Antigravity's two model pools already use.
        //
        // Windows this plan does not have are dropped as soon as a real one
        // exists — otherwise a Free plan drew two dead bars next to its only
        // live window.
        public static List<ProbeWindow> ParseWindows(object result, out string plan)
        {
            List<ReservePool> reserves;
            return ParseWindows(result, out plan, out reserves);
        }

        public static List<ProbeWindow> ParseWindows(object result, out string plan, out List<ReservePool> reserves)
        {
            plan = null;
            reserves = new List<ReservePool>();
            var byKey = new Dictionary<string, ProbeWindow>();
            var order = new List<string>();
            object snap = J.Get(result, "rateLimits") ?? new Dictionary<string, object>();
            plan = J.Str(J.Get(snap, "planType"));

            // (pool id, pool label, window) — the default pool first, so it keeps
            // the bare `five_hour` / `weekly` keys a saved tray selection points
            // at. A pool that only repeats the default's own numbers adds
            // nothing, so it is skipped by id.
            string mainId = (J.Str(J.Get(snap, "limitId")) ?? "").Trim();
            var candidates = new List<KeyValuePair<KeyValuePair<string, string>, object>>();
            Action<string, string, object> add = (id, label, w) =>
            {
                if (w == null) return;
                candidates.Add(new KeyValuePair<KeyValuePair<string, string>, object>(
                    new KeyValuePair<string, string>(id, label), w));
            };
            foreach (string side in new[] { "primary", "secondary" })
                add("", "", J.Get(snap, side));

            try
            {
                Dictionary<string, object> byId = J.Obj(J.Get(result, "rateLimitsByLimitId"));
                if (byId != null)
                {
                    foreach (KeyValuePair<string, object> pool in byId)
                    {
                        if (pool.Value == null) continue;
                        string poolLimitId = (J.Str(J.Get(pool.Value, "limitId")) ?? pool.Key).Trim();
                        // The default pool appears here too, under its own id.
                        if (poolLimitId == mainId || pool.Key == mainId) continue;

                        string rawLimitName = (J.Str(J.Get(pool.Value, "limitName")) ?? "").Trim();
                        string normalModelSlug = (J.Str(J.Get(pool.Value, "normalModelSlug")) ?? "").Trim();

                        bool isLuna = normalModelSlug.IndexOf("luna", StringComparison.OrdinalIgnoreCase) >= 0
                                   || rawLimitName.IndexOf("luna", StringComparison.OrdinalIgnoreCase) >= 0
                                   || poolLimitId.IndexOf("luna", StringComparison.OrdinalIgnoreCase) >= 0;

                        bool isGenericGpt = !isLuna && (rawLimitName.IndexOf("gpt-reserve", StringComparison.OrdinalIgnoreCase) >= 0
                                                    || poolLimitId.Equals("base_model_inference", StringComparison.OrdinalIgnoreCase)
                                                    || rawLimitName.IndexOf("gpt", StringComparison.OrdinalIgnoreCase) >= 0);

                        string family;
                        string label;
                        string groupId;

                        if (isLuna)
                        {
                            family = "luna";
                            label = "luna-reserve";
                            groupId = "luna_reserve";
                        }
                        else if (isGenericGpt)
                        {
                            family = "generic_gpt";
                            label = rawLimitName.Length > 0 ? rawLimitName : "gpt-reserve";
                            groupId = "base_model_inference";
                        }
                        else
                        {
                            family = "unknown";
                            label = rawLimitName.Length > 0 ? rawLimitName : pool.Key;
                            groupId = Regex.Replace(pool.Key.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
                            if (groupId.Length == 0) groupId = "unknown_reserve";
                        }

                        object primary = J.Get(pool.Value, "primary");
                        object secondary = J.Get(pool.Value, "secondary");

                        double? resets = primary != null ? Stamp.Epoch(J.Get(primary, "resetsAt")) : (double?)null;
                        double? expires = primary != null ? Stamp.Epoch(J.Get(primary, "expiresAt")) : (double?)null;
                        bool isExpiry = expires.HasValue && !resets.HasValue;
                        double? effectiveReset = resets ?? expires;

                        double? dur = primary != null ? J.Num(J.Get(primary, "windowDurationMins")) : (double?)null;
                        double? used = primary != null ? J.Num(J.Get(primary, "usedPercent")) : (double?)null;
                        double? remaining = used.HasValue ? Math.Max(0.0, Math.Min(100.0, 100.0 - used.Value)) : (double?)null;

                        bool allocated = primary != null && dur.HasValue;
                        bool available = allocated && used.HasValue;

                        var resPool = new ReservePool
                        {
                            Id = groupId,
                            RawLimitId = poolLimitId,
                            RawLimitName = rawLimitName,
                            Family = family,
                            Label = label,
                            ModelSlug = normalModelSlug,
                            DurationMinutes = dur.HasValue ? (int)Math.Round(dur.Value) : (int?)null,
                            Remaining = remaining,
                            ResetEpoch = effectiveReset,
                            IsExpiry = isExpiry,
                            Allocated = allocated,
                            Available = available,
                            EvidenceQuality = "app-server-ratelimits"
                        };
                        // Eligibility is upstream evidence only: the pool's own
                        // normalModelSlug (plus any eligible-model metadata the
                        // vendor actually sent). A family NAME is not evidence
                        // that a reserve covers every model containing "gpt" or
                        // "luna", so no list is fabricated here. Unknown reserve
                        // types stay visible in the UI but are non-authoritative
                        // for eligibility (ReservePool.SupportsModel returns
                        // false for them).
                        reserves.Add(resPool);

                        if (allocated)
                        {
                            add(groupId, label, primary);
                            if (secondary != null) add(groupId, label, secondary);
                        }
                        else
                        {
                            // Eligible without current allocation: produce an inactive row so UI shows `--`
                            string unallocKey = Model.Qualified(Model.WEEKLY, groupId);
                            var unallocWin = new ProbeWindow
                            {
                                Key = unallocKey,
                                Group = groupId,
                                GroupLabel = label,
                                DurationMinutes = 10080,
                                Available = false,
                                Allocated = false,
                                Remaining = null,
                                ResetEpoch = effectiveReset,
                                IsExpiry = isExpiry,
                                Source = "app-server"
                            };
                            if (!byKey.ContainsKey(unallocKey))
                            {
                                byKey[unallocKey] = unallocWin;
                                order.Add(unallocKey);
                            }
                        }
                    }
                }
            }
            catch { /* schema drift / malformed payload safety: regular windows remain intact */ }

            foreach (var candidate in candidates)
            {
                string pool = candidate.Key.Key, label = candidate.Key.Value;
                object w = candidate.Value;
                double? dur = J.Num(J.Get(w, "windowDurationMins"));
                string baseKey = DurationLabel(dur);
                if (baseKey == null) continue;
                string key = Model.Qualified(baseKey, pool);
                if (byKey.ContainsKey(key)) continue;     // first match wins
                double? used = J.Num(J.Get(w, "usedPercent"));
                double? resets = Stamp.Epoch(J.Get(w, "resetsAt"));
                double? expires = Stamp.Epoch(J.Get(w, "expiresAt"));
                bool isExp = expires.HasValue && !resets.HasValue;
                var win = new ProbeWindow
                {
                    Key = key,
                    Group = pool,
                    GroupLabel = label,
                    DurationMinutes = dur.HasValue ? (int)Math.Round(dur.Value) : (int?)null,
                    Available = used.HasValue,
                    Allocated = true,
                    Remaining = used.HasValue ? Math.Max(0.0, Math.Min(100.0, 100.0 - used.Value)) : (double?)null,
                    ResetEpoch = resets ?? expires,
                    IsExpiry = isExp,
                    Source = "app-server",
                };
                byKey[key] = win;
                order.Add(key);
            }

            var windows = new List<ProbeWindow>();
            foreach (string key in order) windows.Add(byKey[key]);
            // Default pool first, then by duration: gating is per pool anyway
            // (Model.GateWindows), and a reserve pool's weekly must not sort
            // between the main pool's own two windows.
            windows.Sort((a, b) =>
            {
                int ga = a.Group.Length == 0 ? 0 : 1, gb = b.Group.Length == 0 ? 0 : 1;
                if (ga != gb) return ga.CompareTo(gb);
                int pool = string.Compare(a.Group, b.Group, StringComparison.Ordinal);
                if (pool != 0) return pool;
                int da = a.DurationMinutes.HasValue ? a.DurationMinutes.Value : int.MaxValue;
                int db = b.DurationMinutes.HasValue ? b.DurationMinutes.Value : int.MaxValue;
                return da != db ? da.CompareTo(db) : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            return windows;
        }

        // Banked resets: a one-off credit that refills a spent window on demand.
        //
        // Codex grants these ("You have 1 usage limit reset available", which its
        // own CLI prints on startup) and reports them in the SAME payload as the
        // windows, under `rateLimitResetCredits`. LIMISAW read the windows and
        // threw the credits away, so the one thing that could get a blocked user
        // working again was invisible here.
        //
        // A credit is NOT a window: it has no percentage and nothing to fill, so
        // it is a count and an expiry on the account rather than a reading.
        public static ResetCredits ParseResetCredits(object result)
        {
            object block = J.Get(result, "rateLimitResetCredits");
            if (block == null) return null;
            var credits = new ResetCredits();
            double? count = J.Num(J.Get(block, "availableCount"));
            foreach (object credit in J.Arr(J.Get(block, "credits")))
            {
                // Only what is usable now. A spent or expired credit is history,
                // and showing it as available is the one wrong answer here.
                string status = (J.Str(J.Get(credit, "status")) ?? "").Trim().ToLowerInvariant();
                if (status != "available") continue;
                credits.Available++;
                double? expires = Stamp.Epoch(J.Get(credit, "expiresAt"));
                if (expires.HasValue && (!credits.ExpiresEpoch.HasValue || expires.Value < credits.ExpiresEpoch.Value))
                    credits.ExpiresEpoch = expires;
                string title = (J.Str(J.Get(credit, "title")) ?? "").Trim();
                if (title.Length > 0 && credits.Title == null) credits.Title = title;
                string id = (J.Str(J.Get(credit, "id")) ?? "").Trim();
                if (id.Length > 0 && credits.Id == null) credits.Id = id;
            }
            // The server's own count is authoritative when it disagrees with the
            // list: the list may be trimmed, and under-reporting a credit the
            // user has is worse than over-reporting a detail about it.
            if (count.HasValue && (int)count.Value > credits.Available)
                credits.Available = (int)count.Value;
            return credits.Available > 0 ? credits : null;
        }

        // Spend one banked reset on the named account. Returns a sentence for the
        // user, always — this is the only call in LIMISAW that CHANGES anything
        // at a vendor, so "it did nothing and said nothing" is not an option.
        //
        // The vendor's own outcomes are `reset`, `nothingToReset`, `noCredit` and
        // `alreadyRedeemed`; they are reported verbatim-ish rather than collapsed
        // into ok/failed, because "you had nothing to reset" and "you have no
        // credit" send the user to different places.
        public static string ConsumeResetCredit(string homePath, double deadline)
        {
            // `homePath` is the exact canonical home the selected card carries
            // (AccountData.ResetHome). It is re-verified against the CURRENT
            // discovery list — a home can be removed since the sweep — but never
            // re-resolved by display name: with two "Codex" homes, name-only
            // routing would spend a one-off credit on whichever sorts first.
            string home = null;
            foreach (CodexHome h in Homes())
                if (string.Equals(h.Path, homePath, StringComparison.OrdinalIgnoreCase))
                { home = h.Path; break; }
            if (home == null) return "account " + homePath + " is no longer listed";
            string exe = ResolveExe("codex");
            if (exe.Length == 0) return "Codex CLI not found on PATH";
            // The reset rides the SAME pooled session the sweep uses — starting
            // a second child to spend the credit would pay the cold start just
            // to answer faster than the vendor needs. W2-001: the irreversible
            // consume holds the exclusive home lease from before initialization
            // through final response classification, so no sweep, verify or
            // eviction can kill this session mid-transaction.
            SessionPool.SessionLease lease = Pool.Checkout(home, exe, deadline);
            if (lease == null) return "codex app-server did not start";
            using (lease)
            {
                SessionPool.Entry entry = lease.Entry;
                try
                {
                    if (!entry.InitDone)
                    {
                        object init = entry.Link.Call("initialize", new Dictionary<string, object> {
                            { "clientInfo", new Dictionary<string, object> { { "name", "limisaw" }, { "version", "1.0.0" } } },
                            { "capabilities", null },
                        }, deadline);
                        if (init == null) { lease.Retire(); return "codex app-server did not answer initialize"; }
                        if (J.Get(init, "error") != null) { lease.Retire(); return Trim(J.Write(J.Get(init, "error"))); }
                        entry.Link.Notify("initialized", null);
                        entry.InitDone = true;
                    }
                    object res = entry.Link.Call("account/rateLimitResetCredit/consume", null, deadline);
                    if (res == null)
                    {
                        // The request may have reached Codex; the outcome is
                        // unknowable. Retire through the lease so no other
                        // operation interleaves while this one is unwinding.
                        lease.Retire();
                        return "the reset request timed out — check `codex` before trying again";
                    }
                    object err = J.Get(res, "error");
                    if (err != null)
                    {
                        string message = J.Str(J.Get(err, "message"));
                        return Trim(string.IsNullOrEmpty(message) ? J.Write(err) : message);
                    }
                    return Outcome(J.Str(J.Get(J.Get(res, "result"), "outcome")));
                }
                catch (Exception ex) { lease.Retire(); return ex.GetType().Name; }
            }
        }

        static string Outcome(string outcome)
        {
            switch ((outcome ?? "").Trim())
            {
                case "reset": return "done — the limit was reset";
                case "nothingToReset": return "nothing to reset; the credit was NOT spent";
                case "noCredit": return "no banked reset on this account any more";
                case "alreadyRedeemed": return "that credit was already used";
                case "": return "the vendor gave no outcome";
            }
            return outcome;
        }

        // PERF-001 seams. `ResolveExe` and `StartSession` exist so the session
        // harness can run the whole pool without a real `codex` install: the
        // production defaults are the only writers of real children, and no
        // test path can reach them once the hooks are replaced.
        internal static Func<string, string> ResolveExe = exe => Cli.Resolve(exe);
        internal static Func<string, string, RpcLink> StartSession = null; // null = the real RpcSession below

        internal static readonly SessionPool Pool = new SessionPool();

        // The handle the pool stores: the same surface RpcSession exposes,
        // expressed as delegates so a scripted fake can stand in for a child.
        internal class RpcLink
        {
            public Func<string, object, double, object> Call;
            public Action<string, object> Notify;
            public Func<bool> Alive = () => true;
            // PERF-007/R030: an oversized protocol line poisons the session; the
            // reader flags it unhealthy and the pool replaces it. A test stub
            // defaults to healthy.
            public Func<bool> Healthy = () => true;
            public Action Drop = () => { };
        }

// One session per exact canonical home, for the life of the process.
            // An entry that dies is replaced on next checkout; a home that leaves
            // discovery is evicted by RetainOnly; the kill-on-close job from the
            // startup sweep means a pooled child can never outlive LIMISAW.
            //
            // W2-001: an entry is not just reusable, it is OWNED while in use.
            // One exclusive logical-operation lease per entry covers the whole
            // app-server transaction (initialize + read / consume / verify).
            // Checkout hands back a scoped SessionLease; the caller owns the
            // LEASE, the pool owns the ENTRY. Retirement while leased defers
            // Link.Drop to the lease release; Link.Drop happens at most once.
            // The pool's Gate protects map membership and bookkeeping only —
            // never vendor I/O, never a lease wait, never Link.Drop.
            internal class SessionPool
            {
                public class Entry
                {
                    public string HomeKey;
                    public RpcLink Link;
                    public bool InitDone; // read/written as authority only under the lease
                    // Per-entry state protected by StateGate:
                    // - Leased: true while a SessionLease owns this entry
                    // - Retiring: entry removed from future eligibility; Link.Drop deferred
                    // - Dropped: terminal; Link.Drop already performed (exactly once)
                    internal readonly object StateGate = new object();
                    internal bool Leased;
                    internal bool Retiring;
                    internal bool Dropped;
                    // Per-entry exclusive lease. SemaphoreSlim(1,1) — waitable
                    // exclusion with a real timeout, no polling, no Gate held.
                    internal readonly SemaphoreSlim Lease = new SemaphoreSlim(1, 1);
                }

                // Scoped ownership token for one logical app-server operation.
                // Dispose releases the exclusive lease exactly once and, when
                // the entry was marked retiring, performs the deferred
                // Link.Drop — still exactly once, still outside the Gate.
                // Retire() marks the entry for destruction at release: the
                // caller does not Drop a link behind its own lease.
                public sealed class SessionLease : IDisposable
                {
                    public readonly Entry Entry;
                    public RpcLink Link { get { return Entry.Link; } }
                    int Disposed;
                    internal SessionLease(Entry entry) { Entry = entry; }
                    public void Retire()
                    {
                        MarkRetiring(Entry);
                    }
                    public void Dispose()
                    {
                        if (Interlocked.Exchange(ref Disposed, 1) != 0) return;
                        bool retire;
                        lock (Entry.StateGate)
                        {
                            retire = Entry.Retiring;
                            Entry.Leased = false;
                            Entry.Lease.Release(); // waiter wakes AFTER we exit this lock
                        }
                        if (retire) DropEntry(Entry);
                    }
                }

                readonly object Gate = new object();
                readonly Dictionary<string, Entry> ByHome = new Dictionary<string, Entry>();

                // The single coherent destruction transition. Idempotent:
                // whichever path first observes a retiring-or-removed entry
                // with no active lease performs the one Link.Drop; every later
                // path sees Dropped and does nothing. Never called under Gate.
                static void DropEntry(Entry e)
                {
                    bool dropNow = false;
                    lock (e.StateGate)
                    {
                        if (e.Dropped) return;
                        if (e.Leased) return; // still owned; its Dispose will drop
                        e.Dropped = true;
                        dropNow = true;
                    }
                    if (dropNow) try { e.Link.Drop(); } catch { }
                }

                // в”Ђв”Ђ ONE STATE ACCESS POLICY (W2-001 reinspection) в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
                // Entry lifecycle state (Leased / Retiring / Dropped) is read and
                // written ONLY under Entry.StateGate — never under SessionPool.Gate
                // alone. SessionPool.Gate owns ByHome membership and mapping
                // replacement/removal only. These tiny helpers are the whole state
                // machine; lock order where both locks meet: Gate THEN StateGate.

                static bool IsRetiringOrDropped(Entry e)
                {
                    lock (e.StateGate) return e.Retiring || e.Dropped;
                }

                static bool IsIdle(Entry e)
                {
                    lock (e.StateGate) return !e.Leased;
                }

                static void MarkRetiring(Entry e)
                {
                    lock (e.StateGate) e.Retiring = true;
                }

                // TryClaim: under StateGate, an entry not retiring/dropped/leased
                // becomes Leased. False means the caller saw the physical permit
                // but may NOT keep the entry — it must Release() the permit.
                static bool TryClaim(Entry e)
                {
                    lock (e.StateGate)
                    {
                        if (e.Retiring || e.Dropped || e.Leased) return false;
                        e.Leased = true;
                        return true;
                    }
                }

                // Test seam (W2-001 L): invoked between stale validation and
                // the logical claim, while the checkout holds the raw permit.
                // Null in production. Lets a deterministic race force
                // "permit acquired, then the entry becomes Leased (or
                // Retiring) before the claim" — the exact window where the
                // raw permit used to leak.
                internal static Action<Entry> BeforeClaim = null;

            public static string Key(string home)
            {
                return (home ?? "").TrimEnd('\\', '/').ToLowerInvariant();
            }

            // PERF-001: the scheduler's WARM/COLD classification. True when a
            // usable (alive, healthy, initialized) app-server session already
            // exists for this exact canonical home. Pure metadata: it never
            // starts a process, never checks in or out, never touches the
            // child — the operational distinction alone, with account
            // identity and quota truth untouched. A retiring entry is not warm.
            public bool IsWarm(string home)
            {
                Entry entry;
                lock (Gate)
                {
                    if (!ByHome.TryGetValue(Key(home), out entry)) return false;
                }
                // Lifecycle state is StateGate's; Alive/Healthy the link's own.
                return !IsRetiringOrDropped(entry)
                    && entry.Link.Alive() && entry.Link.Healthy()
                    && entry.InitDone;
            }

            // Acquire the exclusive logical-operation lease for this home's
            // entry, bounded by the caller's EXISTING absolute deadline. The
            // wait happens on the entry's own SemaphoreSlim — never under the
            // pool Gate, never with a fresh invented timeout. Returns a scoped
            // SessionLease; null when the deadline ran out first.
            //
            // PERMIT OWNERSHIP INVARIANT: once entry.Lease.Wait returns true,
            // this Checkout OWNS one raw SemaphoreSlim permit. Exactly one of
            // two things happens before any retry or return — the permit is
            // handed to a returned SessionLease, or it is Released here. There
            // is no third path: DropEntry never releases a permit, so any
            // abandon path MUST release before continuing. Wait is never
            // issued while SessionPool.Gate is held.
            public SessionLease Checkout(string home, string exe, double deadline)
            {
                return Checkout(home, exe, deadline, true);
            }

            // Lease the EXISTING session for this home, or nothing. Never
            // starts an app-server child. This is what a cleanup path uses:
            // cancelling a dead vendor login must not resurrect the vendor.
            public SessionLease CheckoutExisting(string home, double deadline)
            {
                return Checkout(home, null, deadline, false);
            }

            SessionLease Checkout(string home, string exe, double deadline, bool allowStart)
            {
                string key = Key(home);
                while (true)
                {
                    // 1. under Gate: observe the current candidate only.
                    Entry candidate = null; Entry dead = null;
                    lock (Gate)
                    {
                        Entry cur;
                        if (ByHome.TryGetValue(key, out cur))
                        {
                            if (IsRetiringOrDropped(cur) || !cur.Link.Alive() || !cur.Link.Healthy())
                            {
                                // Not eligible. If no lease is active it can be
                                // destroyed now; a leased one is left to its
                                // owner (its release performs the drop).
                                ByHome.Remove(key);
                                MarkRetiring(cur);
                                if (IsIdle(cur)) dead = cur;
                            }
                            else candidate = cur;
                        }
                    }
                    if (dead != null) DropEntry(dead);
                    // 2. Gate released: wait for the lease until the deadline.
                    if (candidate != null)
                    {
                        double remain = deadline - NowS();
                        if (remain <= 0) return null;
                        if (!candidate.Lease.Wait(TimeSpan.FromSeconds(Math.Min(remain, 2147483))))
                        {
                            // Bounded by the caller's own deadline, not a new one.
                            return null;
                        }
                        // The raw permit is now owned by THIS checkout. Exactly
                        // one of two things happens before any retry/return: a
                        // returned SessionLease takes ownership, or the finally
                        // below Releases it. No third path — DropEntry never
                        // releases a permit.
                        bool permitHeld = true;
                        try
                        {
                            // 3. re-enter briefly: validate still current.
                            if (IsRetiringOrDropped(candidate))
                            {
                                // STALE AFTER WAIT (Bug A): the owner released
                                // to retire between our Wait and validation.
                                // Abandon: retire, drop if idle, retry — the
                                // finally Releases our raw permit first.
                                MarkRetiring(candidate);
                                DropEntry(candidate);
                                continue;
                            }
                            // 4. claim the lease atomically under StateGate.
                            Action<Entry> seam = BeforeClaim;
                            if (seam != null) seam(candidate); // test seam only
                            if (!TryClaim(candidate))
                            {
                                // FAILED CLAIM (Bug B): the entry was claimed
                                // elsewhere between validation and claim.
                                // Abandon: retire, drop if idle, retry — the
                                // finally Releases our raw permit first.
                                MarkRetiring(candidate);
                                DropEntry(candidate);
                                continue;
                            }
                            SessionLease result = new SessionLease(candidate);
                            permitHeld = false; // SessionLease now owns release
                            return result;
                        }
                        finally
                        {
                            if (permitHeld) candidate.Lease.Release();
                        }
                    }
                    // 5. no candidate: start a fresh session OUTSIDE Gate.
                    // A no-start checkout stops here: the exact session this
                    // caller wanted is gone, and inventing a new one would
                    // answer for a process that never knew the operation.
                    if (!allowStart) return null;
                    RpcLink link = StartSession != null ? StartSession(exe, home) : RealSession(exe, home);
                    if (link == null) return null;
                    var fresh = new Entry { HomeKey = key, Link = link };
                    // The creator claims the permit BEFORE publication: a brand
                    // new entry cannot be contended, so this is nonblocking and
                    // must never invent a 5-second budget. Failure is an
                    // internal invariant violation, not a deadline signal.
                    if (!fresh.Lease.Wait(0))
                    {
                        try { link.Drop(); } catch { }
                        continue;
                    }
                    lock (fresh.StateGate) fresh.Leased = true; // claim before publish
                    // 6. publish: exactly one winner; the loser is disposed
                    // outside Gate and never handed to anyone. A REPLACED old
                    // mapping is tracked explicitly — an idle one would
                    // otherwise escape DropEntry entirely (Bug C).
                    Entry loser = null; Entry winner = null; Entry replaced = null;
                    lock (Gate)
                    {
                        Entry cur;
                        if (ByHome.TryGetValue(key, out cur)
                            && !IsRetiringOrDropped(cur) && cur.Link.Alive() && cur.Link.Healthy())
                        {
                            winner = cur; loser = fresh;
                        }
                        else
                        {
                            if (cur != null)
                            {
                                ByHome.Remove(key);
                                MarkRetiring(cur);
                                replaced = cur;
                            }
                            ByHome[key] = fresh; winner = fresh;
                        }
                    }
                    if (winner != fresh)
                    {
                        // We lost publication. Our fresh session was never
                        // handed to anyone: release its private lease so
                        // DropEntry can destroy it exactly once.
                        lock (fresh.StateGate) { fresh.Leased = false; fresh.Retiring = true; fresh.Lease.Release(); }
                        DropEntry(fresh);
                        // Retry acquisition of the winner — OUTSIDE Gate,
                        // bounded by the caller's own deadline. Same permit
                        // ownership invariant as above.
                        double remain2 = deadline - NowS();
                        if (remain2 <= 0) return null;
                        if (!winner.Lease.Wait(TimeSpan.FromSeconds(Math.Min(remain2, 2147483)))) return null;
                        bool permitHeld2 = true;
                        try
                        {
                            if (IsRetiringOrDropped(winner)) continue;
                            if (!TryClaim(winner)) continue;
                            SessionLease result = new SessionLease(winner);
                            permitHeld2 = false; // SessionLease now owns release
                            return result;
                        }
                        finally
                        {
                            if (permitHeld2) winner.Lease.Release();
                        }
                    }
                    // Drop loser AND the replaced old mapping outside Gate
                    // (both were retired under Gate; idle ones are destroyed
                    // now, leased ones at their owner's release). Idempotent,
                    // never under Gate.
                    if (replaced != null) DropEntry(replaced);
                    if (loser != null) DropEntry(loser);
                    return new SessionLease(fresh);
                }
            }

            // External eviction by discovery: RetainOnly never owns the active
            // operation lease. It only removes future eligibility and marks
            // retiring; an idle entry is dropped now, a leased one is left to
            // its owner's release.
            public void RetainOnly(List<string> keys)
            {
                var idle = new List<Entry>();
                lock (Gate)
                {
                    var gone = new List<Entry>();
                    foreach (KeyValuePair<string, Entry> kv in ByHome)
                        if (!keys.Contains(kv.Key)) gone.Add(kv.Value);
                    foreach (Entry e in gone)
                    {
                        ByHome.Remove(e.HomeKey);
                        MarkRetiring(e);
                        if (IsIdle(e)) idle.Add(e);
                    }
                }
                foreach (Entry e in idle) DropEntry(e);
            }

            public void EvictHome(string home)
            {
                Entry old = null;
                lock (Gate)
                {
                    string key = Key(home);
                    if (ByHome.TryGetValue(key, out old))
                    {
                        ByHome.Remove(key);
                        MarkRetiring(old);
                    }
                }
                if (old != null) DropEntry(old);
            }

            public int Count
            {
                get
                {
                    lock (Gate)
                    {
                        int n = 0;
                        foreach (KeyValuePair<string, Entry> kv in ByHome)
                            if (!IsRetiringOrDropped(kv.Value)) n++;
                        return n;
                    }
                }
            }

            // Test hook only: drop every entry. Same deferred-retirement rule.
            public void Reset()
            {
                var idle = new List<Entry>();
                lock (Gate)
                {
                    var all = new List<Entry>(ByHome.Values);
                    ByHome.Clear();
                    foreach (Entry e in all)
                    {
                        MarkRetiring(e);
                        if (IsIdle(e)) idle.Add(e);
                    }
                }
                foreach (Entry e in idle) DropEntry(e);
            }

            RpcLink RealSession(string exe, string home)
            {
                RpcSession session = RpcSession.Start(exe, home);
                return session == null ? null : session.Link();
            }
        }

        // Minimal JSON-RPC 2.0 client over the child's stdio. A reader thread
        // owns stdout so a noisy child can never block the pipe, and the
        // caller's absolute deadline is the only timeout that matters.
        internal class RpcSession : IDisposable
        {
            internal static Func<ProcessStartInfo, Process> TestProcessStarter = null;
            internal static Action TestHookAfterAdopt = null;
            Process P;
            // The session's own containment. A pooled app-server lives for the
            // life of the process, so its tree must end when the session is
            // dropped (dead child, eviction, timeout) rather than at app exit —
            // and must end even if the app is killed mid-sweep.
            ChildSweeper.Scope Scope;
            // PERF-001: explicit pending-request OWNERSHIP instead of a reply
            // map plus polling plus tombstone set. Exactly one slot per
            // in-flight Call, registered BEFORE the frame is written and removed
            // by exactly one of its three owners: the answer, the deadline, or
            // the session's close. ReadLoop publishes only into a slot that
            // already exists, so an unsolicited or unmatched id can never grow
            // this map. There is no tombstone set to age out: a reply for a
            // request nobody owns is simply dropped. Ids are handed out
            // monotonically and never reused, so a late reply can only ever be
            // matched against the slot of the very request that asked for it —
            // which is exactly what the old Abandoned tombstone was faking.
            sealed class PendingSlot
            {
                internal object Reply;
                internal bool Done;
            }

            readonly Dictionary<int, PendingSlot> PendingSlots = new Dictionary<int, PendingSlot>();
            readonly object Gate = new object();
            // Set once the response pipe is finished (child exited, killed, or
            // poisoned). Every waiter is pulsed and must stop waiting rather
            // than burn its whole deadline on a session that can never answer.
            bool Closed;
            int NextId = 1;

            public static RpcSession Start(string exe, string home)
            {
                bool batch = Cli.IsBatchShim(exe);
                string inner = batch ? "\"app-server\" \"--stdio\"" : "app-server --stdio";
                var psi = new ProcessStartInfo(batch ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") : exe,
                    batch ? Cli.BatchShellArgs(exe, inner) : "app-server --stdio")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                psi.EnvironmentVariables["CODEX_HOME"] = home;
                // A quota read must use the account's own credentials, never an
                // API key that happens to sit in this process's environment.
                foreach (string k in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" })
                    psi.EnvironmentVariables.Remove(k);
                ChildSweeper.Scope scope = null;
                Process proc = null;
                RpcSession session = null;
                bool transferred = false;
                try
                {
                    scope = ChildSweeper.Open();
                    proc = TestProcessStarter != null ? TestProcessStarter(psi) : Process.Start(psi);
                    if (proc == null) return null;
                    scope.Adopt(proc);
                    if (TestHookAfterAdopt != null) TestHookAfterAdopt();
                    session = new RpcSession { P = proc, Scope = scope };
                    proc.ErrorDataReceived += (s, e) => { };
                    proc.BeginErrorReadLine();
                    var reader = new Thread(session.ReadLoop);
                    reader.IsBackground = true;
                    reader.Start();
                    transferred = true;
                    return session;
                }
                catch { return null; }
                finally
                {
                    if (!transferred)
                    {
                        if (proc != null)
                        {
                            try { if (proc.StandardInput != null) proc.StandardInput.Close(); } catch { }
                            try { if (!proc.HasExited) proc.Kill(); } catch { }
                            try { proc.Dispose(); } catch { }
                        }
                        if (scope != null) try { scope.Dispose(); } catch { }
                    }
                }
            }

            public bool Alive
            {
                get { try { return !P.HasExited; } catch { return false; } }
            }

            internal int PendingResponses { get { lock (Gate) return PendingSlots.Count; } }
            internal bool Healthy { get { return !Unhealthy; } }
            bool Unhealthy;
            // W2-001 secondary protocol safety: Call and Notify share one
            // dedicated write lock around StandardInput Write+Flush, so two
            // concurrent writers cannot interleave half a request frame. The
            // response-map Gate is never held while writing; the reader loop
            // keeps independent access. This is frame safety only — it does
            // NOT replace the higher-level per-home logical-operation lease.
            readonly object WriteLock = new object();

            void ReadLoop()
            {
                try
                {
                    string line;
                    while ((line = ReadBoundedLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        object msg = J.Parse(line);
                        double? id = J.Num(J.Get(msg, "id"));
                        if (!id.HasValue) continue;
                        lock (Gate)
                        {
                            PendingSlot slot;
                            // Publish ONLY into an owned slot. An id nobody is
                            // waiting for (unsolicited push, a reply to a call
                            // that already timed out, a protocol stray) is
                            // dropped here instead of being retained until
                            // shutdown.
                            if (!PendingSlots.TryGetValue((int)id.Value, out slot)) continue;
                            slot.Reply = msg;
                            slot.Done = true;
                            Monitor.PulseAll(Gate);
                        }
                    }
                }
                catch { }
                // The pipe is finished. Everything the child did send was parsed
                // and published above, so a finished answer still beats this
                // close — but nobody may keep waiting on a session that cannot
                // answer any more.
                lock (Gate) { Closed = true; Monitor.PulseAll(Gate); }
            }

            // PERF-007/R030: ReadBoundedLine — retain only up to the protocol
            // ceiling, then discard until the newline. An oversized line is
            // never fully retained, never J.Parsed, never added to Responses;
            // the offending session is marked unhealthy and terminated so the
            // SessionPool replaces it with a fresh app-server (a poisoned
            // connection is not a connection).
            internal string Continuation { get; private set; }
            const int RpcMaxLine = 512 * 1024;

            string ReadBoundedLine()
            {
                var sb = new StringBuilder(1024);
                int ch;
                bool oversized = false;
                while ((ch = P.StandardOutput.Read()) != -1)
                {
                    if (ch == '\n')
                    {
                        if (oversized)
                        {
                            // Poisoned connection: terminate it AND wake the
                            // in-flight callers, so they fail fast instead of
                            // waiting out a deadline for a reply that is never
                            // parsed. Nothing from the oversized line is stored.
                            lock (Gate) { Unhealthy = true; Closed = true; Monitor.PulseAll(Gate); }
                            try { P.Kill(); } catch { }
                            return null;
                        }
                        string line = sb.ToString().TrimEnd('\r');
                        sb.Length = 0;
                        return line;
                    }
                    if (!oversized)
                    {
                        if (sb.Length < RpcMaxLine) sb.Append((char)ch);
                        else oversized = true;            // keep draining, never grow the retained string
                    }
                }
                if (oversized)
                {
                    lock (Gate) Unhealthy = true;
                    try { P.Kill(); } catch { }
                }
                return sb.Length > 0 ? sb.ToString() : null;
            }

            public object Call(string method, object parameters, double deadline)
            {
                var slot = new PendingSlot();
                int id;
                lock (Gate)
                {
                    id = NextId++;
                    // Ownership is taken BEFORE the frame is written, so the
                    // reply can never arrive before the slot that receives it.
                    PendingSlots[id] = slot;
                }
                var req = new Dictionary<string, object> {
                    { "jsonrpc", "2.0" }, { "id", id }, { "method", method },
                };
                if (parameters != null) req["params"] = parameters;
                if (!Send(req))
                {
                    // Nothing was written: release the slot immediately rather
                    // than leaving it for the deadline to reap.
                    lock (Gate) PendingSlots.Remove(id);
                    return null;
                }
                lock (Gate)
                {
                    // Event-driven wait: the reader pulses the answer, the pipe
                    // close pulses the failure, and the deadline bounds it. No
                    // 20ms polling and no sleep-and-recheck.
                    while (!slot.Done)
                    {
                        if (Closed) break;
                        double left = deadline - Stamp.Now;
                        if (left <= 0) break;
                        // A pulse is the normal path; this floor only guarantees
                        // that a missed pulse cannot turn into an unbounded wait.
                        int ms = (int)Math.Min(250.0, Math.Ceiling(left * 1000.0));
                        if (ms < 1) ms = 1;
                        Monitor.Wait(Gate, ms);
                    }
                    // The deadline or the close may have raced the answer: a
                    // finished reply always wins, a deadline never eats it. And
                    // a call that gives up leaves NO slot behind, so a late
                    // reply has nowhere to park and cannot resurface as the
                    // answer to a later request.
                    PendingSlots.Remove(id);
                    return slot.Done ? slot.Reply : null;
                }
            }

            public void Notify(string method, object parameters)
            {
                var req = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "method", method } };
                if (parameters != null) req["params"] = parameters;
                Send(req);
            }

            bool Send(Dictionary<string, object> req)
            {
                try
                {
                    lock (WriteLock)
                    {
                        P.StandardInput.Write(J.Write(req) + "\n");
                        P.StandardInput.Flush();
                    }
                    return true;
                }
                catch { return false; }
            }

            public RpcLink Link()
            {
                return new RpcLink
                {
                    Call = (method, parameters, deadline) => Call(method, parameters, deadline),
                    Notify = (method, parameters) => Notify(method, parameters),
                    Alive = () => Alive,
                    Drop = Dispose,
                };
            }

            public void Dispose()
            {
                // Wake any in-flight caller first: the session is going away and
                // nobody may keep waiting on it.
                lock (Gate) { Closed = true; Monitor.PulseAll(Gate); }
                try { if (P.StandardInput != null) P.StandardInput.Close(); } catch { }
                try { if (!P.WaitForExit(2000)) P.Kill(); } catch { }
                try { P.Dispose(); } catch { }
                // Last: the child is asked to leave politely first, then the job
                // takes whatever it started with it.
                try { if (Scope != null) Scope.Dispose(); } catch { }
            }
        }
    }
}
