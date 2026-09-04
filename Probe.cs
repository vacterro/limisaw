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
    // ── JSON access ──────────────────────────────────────────────────────────
    // JavaScriptSerializer.DeserializeObject hands back Dictionary/object[]/
    // boxed numbers. Every read goes through these so a vendor changing a field
    // type is a null, never an exception in the middle of a sweep.
    static class J
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            ser.RecursionLimit = 200;
            try { return ser.DeserializeObject(text); } catch { return null; }
        }

        public static string Write(object value)
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
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

    // ── timestamps ───────────────────────────────────────────────────────────
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
    }

    // ── domain ───────────────────────────────────────────────────────────────
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

        public ProbeWindow Copy()
        {
            return new ProbeWindow
            {
                Key = Key, Group = Group, GroupLabel = GroupLabel, Source = Source,
                Available = Available, AssumedFull = AssumedFull, Remaining = Remaining,
                ResetEpoch = ResetEpoch, DurationMinutes = DurationMinutes, GatedBy = GatedBy,
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
        public bool Ok, Quiet;
        public List<ProbeWindow> Windows = new List<ProbeWindow>();
        // Banked resets this account holds, or null. Not a window: no percentage,
        // nothing to fill — a count, an expiry and the vendor's own title.
        public ResetCredits Credits;
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

    class ProbeResult
    {
        public List<AccountData> Accounts = new List<AccountData>();
        public List<CliInfo> Clis = new List<CliInfo>();
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
        // the clock had already disproved.
        public static List<ProbeWindow> ApplyElapsedResets(List<ProbeWindow> windows, double now)
        {
            var outList = new List<ProbeWindow>();
            foreach (ProbeWindow w in windows)
            {
                if (!w.Available || !w.ResetEpoch.HasValue
                    || w.ResetEpoch.Value <= 0 || w.ResetEpoch.Value > now)
                { outList.Add(w); continue; }
                ProbeWindow full = w.Copy();
                full.Remaining = 100.0;
                full.ResetEpoch = null;     // "resets in -4m" is not a thing
                full.GatedBy = null;
                full.AssumedFull = true;
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

        // Flatten to what the window/tray draw with. `Rem` is ALWAYS remaining.
        public static AccountData Flatten(ProbeAccount acc, double now)
        {
            var ad = new AccountData
            {
                Provider = acc.Provider, ProviderLabel = acc.ProviderLabel, Name = acc.Name,
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
            foreach (ProbeWindow w in Resolve(acc.Windows, now))
            {
                int rem = 0;
                if (w.Remaining.HasValue)
                    rem = (int)Math.Round(Math.Max(0.0, Math.Min(100.0, w.Remaining.Value)));
                ad.Windows.Add(new WindowData
                {
                    Key = w.Key,
                    Base = Base(w.Key),
                    Label = Label(w.Key),
                    Group = w.Group ?? "",
                    GroupLabel = w.GroupLabel ?? "",
                    Available = w.Available,
                    Rem = rem,
                    Reset = Stamp.Iso(w.ResetEpoch),
                    GatedBy = w.GatedBy,
                    AssumedFull = w.AssumedFull,
                    DurationMinutes = w.DurationMinutes.HasValue ? w.DurationMinutes.Value : 0,
                });
            }
            return ad;
        }
    }

    // ── vendor CLI plumbing ──────────────────────────────────────────────────
    // Nothing here ever installs anything. `Tools` is data: the exact command,
    // its publisher and where it lands, so the UI can show it and let the user
    // decide. Piping a remote script into a shell must never be implicit.
    static class Cli
    {
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
        // reporting "not installed" until the app restarts.
        public static string Resolve(string key)
        {
            Tool tool = Find(key);
            string binary = tool != null ? tool.Binary : key;
            string[] exts = { ".exe", ".cmd", ".bat", "" };
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
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

        // Run a CLI under an absolute deadline. Never throws, never blocks past
        // the deadline, never opens a console window, never goes through a
        // shell (so no argument can be reinterpreted).
        public static Result Run(string exe, string[] args, double deadline, string cwd)
        {
            var res = new Result();
            double remaining = deadline - Stamp.Now;
            if (remaining <= 0.1) { res.Error = "deadline_exceeded"; return res; }
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in args) psi.Arguments += (psi.Arguments.Length > 0 ? " " : "") + Quote(a);
            if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
            Process proc = null;
            try
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                proc = Process.Start(psi);
                if (proc == null) { res.Error = "could not start " + Path.GetFileName(exe); return res; }
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
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
                res.Stdout = stdout.ToString();
                if (proc.ExitCode != 0)
                {
                    // stderr can carry an auth hint ("Not logged in"); that is a
                    // vendor message about the user's own account, never a secret.
                    string first = "";
                    foreach (string line in stderr.ToString().Split('\n'))
                        if (line.Trim().Length > 0) { first = line.Trim(); break; }
                    res.Error = first.Length > 0
                        ? (first.Length > 160 ? first.Substring(0, 160) : first)
                        : "exit " + proc.ExitCode;
                    return res;
                }
                res.Ok = true;
                return res;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name; return res; }
            finally { if (proc != null) proc.Dispose(); }
        }

        static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

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

    // ── Codex: structured usage over the app-server's JSON-RPC ───────────────
    // `codex app-server --stdio`, one child per CODEX_HOME, windows mapped by
    // their reported duration and never by primary/secondary position. A
    // missing bucket stays unavailable; it is never faked as zero.
    static class CodexSource
    {
        public static List<ProbeAccount> Sweep(double deadline, double perAccount)
        {
            var accounts = new List<ProbeAccount>();
            foreach (KeyValuePair<string, string> home in Homes())
            {
                double budget = Math.Min(perAccount, deadline - Stamp.Now);
                if (budget <= 0.2) break;
                accounts.Add(Probe(home.Key, home.Value, Stamp.Now + budget));
            }
            return accounts;
        }

        // Only *lists* candidate homes: fast, no subprocess. A directory
        // without auth.json is not a Codex home.
        public static List<KeyValuePair<string, string>> Homes()
        {
            var found = new List<KeyValuePair<string, string>>();
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
                found.Add(new KeyValuePair<string, string>(full, name));
            };

            add(Environment.GetEnvironmentVariable("CODEX_HOME"), "Codex");
            add(Path.Combine(profile, ".codex"), "Codex");
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

        static ProbeAccount Fail(string name, string code, string detail)
        {
            var acc = new ProbeAccount
            {
                Provider = "codex", ProviderLabel = "Codex", Name = name,
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

        static ProbeAccount Probe(string home, string name, double deadline)
        {
            string exe = Cli.Resolve("codex");
            if (exe.Length == 0) return Fail(name, "cli_not_installed", "Codex CLI not found on PATH");
            RpcSession session = null;
            try
            {
                session = RpcSession.Start(exe, home);
                if (session == null) return Fail(name, "spawn_failed", "codex app-server did not start");
                // Each call may use whatever is LEFT of the deadline. The first
                // app-server of a session pays a cold start (measured 14-19s),
                // so a fixed per-call cap threw away time the caller had
                // already granted and read as "this account is broken".
                object init = session.Call("initialize", new Dictionary<string, object> {
                    { "clientInfo", new Dictionary<string, object> { { "name", "limisaw" }, { "version", "1.0.0" } } },
                    { "capabilities", null },
                }, deadline);
                if (init == null) return Fail(name, "timeout", "codex app-server did not answer initialize");
                if (J.Get(init, "error") != null) return Fail(name, "initialize_error", J.Write(J.Get(init, "error")));
                session.Notify("initialized", null);
                object rl = session.Call("account/rateLimits/read", null, deadline);
                if (rl == null) return Fail(name, "timeout", "codex app-server did not answer rateLimits");
                if (J.Get(rl, "error") != null) return Fail(name, "rate_limits_error", J.Write(J.Get(rl, "error")));

                object result = J.Get(rl, "result") ?? new Dictionary<string, object>();
                string plan;
                List<ProbeWindow> windows = ParseWindows(result, out plan);
                var acc = new ProbeAccount
                {
                    Provider = "codex", ProviderLabel = "Codex", Name = name,
                    Status = Model.OK, Ok = true, Plan = plan,
                    // The same payload that carries the windows carries the
                    // banked resets; reading one and dropping the other is how
                    // "you have 1 reset available" stayed invisible here.
                    Credits = ParseResetCredits(result),
                };
                if (windows.Count == 0)
                {
                    windows.Add(ProbeWindow.Unavailable(Model.FIVE_HOUR));
                    windows.Add(ProbeWindow.Unavailable(Model.WEEKLY));
                }
                acc.Windows = windows;
                return acc;
            }
            catch (Exception ex) { return Fail(name, "probe_exception", ex.GetType().Name + ": " + ex.Message); }
            finally { if (session != null) session.Dispose(); }
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
            plan = null;
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

            Dictionary<string, object> byId = J.Obj(J.Get(result, "rateLimitsByLimitId"));
            if (byId != null)
                foreach (KeyValuePair<string, object> pool in byId)
                {
                    // The default pool appears here too, under its own id.
                    if (pool.Key == mainId) continue;
                    string label = (J.Str(J.Get(pool.Value, "limitName")) ?? "").Trim();
                    if (label.Length == 0) label = pool.Key;
                    string id = Regex.Replace(pool.Key.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
                    foreach (string side in new[] { "primary", "secondary" })
                        add(id, label, J.Get(pool.Value, side));
                }

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
                var win = new ProbeWindow
                {
                    Key = key,
                    Group = pool,
                    GroupLabel = label,
                    DurationMinutes = dur.HasValue ? (int)Math.Round(dur.Value) : (int?)null,
                    Available = true,
                    Remaining = used.HasValue ? Math.Max(0.0, Math.Min(100.0, 100.0 - used.Value)) : (double?)null,
                    ResetEpoch = Stamp.Epoch(J.Get(w, "resetsAt")),
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
        public static string ConsumeResetCredit(string accountName, double deadline)
        {
            string home = null;
            foreach (KeyValuePair<string, string> h in Homes())
                if (h.Value == accountName) { home = h.Key; break; }
            if (home == null) return "account " + accountName + " is no longer listed";
            string exe = Cli.Resolve("codex");
            if (exe.Length == 0) return "Codex CLI not found on PATH";
            RpcSession session = null;
            try
            {
                session = RpcSession.Start(exe, home);
                if (session == null) return "codex app-server did not start";
                object init = session.Call("initialize", new Dictionary<string, object> {
                    { "clientInfo", new Dictionary<string, object> { { "name", "limisaw" }, { "version", "1.0.0" } } },
                    { "capabilities", null },
                }, deadline);
                if (init == null) return "codex app-server did not answer initialize";
                if (J.Get(init, "error") != null) return Trim(J.Write(J.Get(init, "error")));
                session.Notify("initialized", null);
                object res = session.Call("account/rateLimitResetCredit/consume", null, deadline);
                if (res == null) return "the reset request timed out — check `codex` before trying again";
                object err = J.Get(res, "error");
                if (err != null)
                {
                    string message = J.Str(J.Get(err, "message"));
                    return Trim(string.IsNullOrEmpty(message) ? J.Write(err) : message);
                }
                return Outcome(J.Str(J.Get(J.Get(res, "result"), "outcome")));
            }
            catch (Exception ex) { return ex.GetType().Name; }
            finally { if (session != null) session.Dispose(); }
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

        // Minimal JSON-RPC 2.0 client over the child's stdio. A reader thread
        // owns stdout so a noisy child can never block the pipe, and the
        // caller's absolute deadline is the only timeout that matters.
        class RpcSession : IDisposable
        {
            Process P;
            readonly Dictionary<int, object> Responses = new Dictionary<int, object>();
            readonly object Gate = new object();
            int NextId = 1;

            public static RpcSession Start(string exe, string home)
            {
                var psi = new ProcessStartInfo(exe, "app-server --stdio")
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
                Process proc = Process.Start(psi);
                if (proc == null) return null;
                var session = new RpcSession { P = proc };
                proc.ErrorDataReceived += (s, e) => { };
                proc.BeginErrorReadLine();
                var reader = new Thread(session.ReadLoop);
                reader.IsBackground = true;
                reader.Start();
                return session;
            }

            void ReadLoop()
            {
                try
                {
                    string line;
                    while ((line = P.StandardOutput.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        object msg = J.Parse(line);
                        double? id = J.Num(J.Get(msg, "id"));
                        if (!id.HasValue) continue;
                        lock (Gate) Responses[(int)id.Value] = msg;
                    }
                }
                catch { }
            }

            public object Call(string method, object parameters, double deadline)
            {
                int id;
                lock (Gate) id = NextId++;
                var req = new Dictionary<string, object> {
                    { "jsonrpc", "2.0" }, { "id", id }, { "method", method },
                };
                if (parameters != null) req["params"] = parameters;
                if (!Send(req)) return null;
                while (Stamp.Now < deadline)
                {
                    lock (Gate)
                    {
                        object found;
                        if (Responses.TryGetValue(id, out found)) { Responses.Remove(id); return found; }
                    }
                    if (P.HasExited)
                    {
                        // One last look: the answer may have landed in the same
                        // instant the child closed its pipe.
                        Thread.Sleep(20);
                        lock (Gate)
                        {
                            object found;
                            if (Responses.TryGetValue(id, out found)) { Responses.Remove(id); return found; }
                        }
                        return null;
                    }
                    Thread.Sleep(20);
                }
                return null;
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
                    P.StandardInput.Write(J.Write(req) + "\n");
                    P.StandardInput.Flush();
                    return true;
                }
                catch { return false; }
            }

            public void Dispose()
            {
                try { if (P.StandardInput != null) P.StandardInput.Close(); } catch { }
                try { if (!P.WaitForExit(2000)) P.Kill(); } catch { }
                try { P.Dispose(); } catch { }
            }
        }
    }
}
