using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using Limisaw;

public static class CodexReserveTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    public static int Main(string[] args)
    {
        Console.WriteLine("CodexReserveTest starting (Matrix A-N)...");

        TestA_RegularOnly();
        TestB_GenericReserveOnly();
        TestC_LunaReserveOnly();
        TestD_GenericZero_LunaPositive();
        TestE_LunaReserveZero();
        TestF_BothReservesPresent_NoCollision();
        TestG_ProductionAvailabilityBoundary();
        TestH_ResetExpiryDynamics();
        TestI_UnknownReserveType();
        TestJ_MissingReserveFields();
        TestK_MalformedReservePayload();
        TestL_RefreshUpdatesInPlace();
        TestM_RegularRequiresAllWindows();
        TestN_ReserveEligibilityIsExplicit();

        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine(string.Format("CodexReserveTest: {0} checks, {1} fails", checks, fails));
        return fails > 0 ? 1 : 0;
    }

    static object ParseJson(string json)
    {
        var ser = new JavaScriptSerializer();
        return ser.DeserializeObject(json);
    }

    // Test A: regular > 0, no reserve -> normal availability unchanged
    static void TestA_RegularOnly()
    {
        string json = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""planType"": ""plus"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 25, ""resetsAt"": 1790000000 }
            }
        }";
        string plan;
        List<ReservePool> reserves;
        List<ProbeWindow> windows = CodexSource.ParseWindows(ParseJson(json), out plan, out reserves);

        Check("TestA_WindowsCount", windows.Count == 1, "expected 1 window, got " + windows.Count);
        Check("TestA_ReservesCount", reserves.Count == 0, "expected 0 reserves, got " + reserves.Count);

        var pa = new ProbeAccount { Provider = "codex", Name = "A1", Windows = windows, Reserves = reserves, Ok = true };
        AccountData ad = Model.Flatten(pa, 1780000000);

        Check("TestA_RegularAvail", ad.Availability.RegularAvailable, "regular should be available");
        Check("TestA_GenericAvail", !ad.Availability.GenericReserveAvailable, "generic reserve should be false");
        Check("TestA_LunaAvail", !ad.Availability.LunaReserveAvailable, "luna reserve should be false");
        Check("TestA_EffLuna", ad.Availability.EffectiveLunaAvailable, "effective luna should be true (backed by regular)");
        Check("TestA_EffGpt", ad.Availability.EffectiveGptAvailable, "effective gpt should be true (backed by regular)");
        Check("TestA_Reason", ad.Availability.LunaStatusReason == "normal quota", "reason: " + ad.Availability.LunaStatusReason);
    }

    // Test B: regular = 0, generic GPT reserve > 0 -> generic reserve visible, only eligible model families usable (not Luna)
    static void TestB_GenericReserveOnly()
    {
        string json = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""planType"": ""plus"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
            },
            ""rateLimitsByLimitId"": {
                ""codex"": {
                    ""limitId"": ""codex"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
                },
                ""base_model_inference"": {
                    ""limitId"": ""base_model_inference"",
                    ""limitName"": ""gpt-reserve"",
                    ""normalModelSlug"": ""gpt-5"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 10, ""resetsAt"": 1790100000 }
                }
            }
        }";
        string plan;
        List<ReservePool> reserves;
        List<ProbeWindow> windows = CodexSource.ParseWindows(ParseJson(json), out plan, out reserves);

        Check("TestB_ReservesCount", reserves.Count == 1, "expected 1 reserve, got " + reserves.Count);
        Check("TestB_ReserveFamily", reserves[0].Family == "generic_gpt", "family: " + reserves[0].Family);
        Check("TestB_ReserveLabel", reserves[0].Label == "gpt-reserve", "label: " + reserves[0].Label);

        var pa = new ProbeAccount { Provider = "codex", Name = "B1", Windows = windows, Reserves = reserves, Ok = true };
        AccountData ad = Model.Flatten(pa, 1780000000);

        Check("TestB_RegularAvail", !ad.Availability.RegularAvailable, "regular should be exhausted");
        Check("TestB_GenericAvail", ad.Availability.GenericReserveAvailable, "generic reserve should be true");
        Check("TestB_LunaAvail", !ad.Availability.LunaReserveAvailable, "luna reserve should be false");
        Check("TestB_EffLuna", !ad.Availability.EffectiveLunaAvailable, "luna should be unavailable");
        Check("TestB_EffGpt", ad.Availability.EffectiveGptAvailable, "gpt should be available");
        // Eligibility is upstream evidence: the reserve's own normalModelSlug.
        // A family name is NOT evidence the reserve covers every gpt model.
        Check("TestB_SlugEligible", ad.Reserves[0].SupportsModel("gpt-5"), "declared slug eligible");
        Check("TestB_UnlistedNotEligible", !ad.Reserves[0].SupportsModel("gpt-4o"), "unlisted model is not inferred");
    }

    // Test C: regular = 0, Luna reserve > 0 -> Luna reserve visible, Luna considered available
    static void TestC_LunaReserveOnly()
    {
        string json = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""planType"": ""plus"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
            },
            ""rateLimitsByLimitId"": {
                ""codex"": {
                    ""limitId"": ""codex"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
                },
                ""base_model_inference"": {
                    ""limitId"": ""base_model_inference"",
                    ""limitName"": ""gpt-reserve"",
                    ""normalModelSlug"": ""gpt-5.6-luna"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 35, ""resetsAt"": 1790160000 }
                }
            }
        }";
        string plan;
        List<ReservePool> reserves;
        List<ProbeWindow> windows = CodexSource.ParseWindows(ParseJson(json), out plan, out reserves);

        Check("TestC_ReservesCount", reserves.Count == 1, "expected 1 reserve, got " + reserves.Count);
        Check("TestC_ReserveFamily", reserves[0].Family == "luna", "family: " + reserves[0].Family);
        Check("TestC_ReserveLabel", reserves[0].Label == "luna-reserve", "label: " + reserves[0].Label);

        var pa = new ProbeAccount { Provider = "codex", Name = "C1", Windows = windows, Reserves = reserves, Ok = true };
        AccountData ad = Model.Flatten(pa, 1780000000);

        Check("TestC_RegularAvail", !ad.Availability.RegularAvailable, "regular should be exhausted");
        Check("TestC_LunaAvail", ad.Availability.LunaReserveAvailable, "luna reserve should be true");
        Check("TestC_EffLuna", ad.Availability.EffectiveLunaAvailable, "luna should be available");
        Check("TestC_Reason", ad.Availability.LunaStatusReason == "reserve-backed (luna-reserve)", "reason: " + ad.Availability.LunaStatusReason);
        Check("TestC_SlugEligible", ad.Reserves[0].SupportsModel("gpt-5.6-luna"), "declared luna slug eligible");
    }

    // Test D: regular = 0, generic reserve = 0, Luna reserve > 0 -> Luna still available
    static void TestD_GenericZero_LunaPositive()
    {
        string json = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""planType"": ""plus"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
            },
            ""rateLimitsByLimitId"": {
                ""codex"": {
                    ""limitId"": ""codex"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
                },
                ""gen_pool"": {
                    ""limitId"": ""gen_pool"",
                    ""limitName"": ""gpt-reserve"",
                    ""normalModelSlug"": ""gpt-4"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790100000 }
                },
                ""base_model_inference"": {
                    ""limitId"": ""base_model_inference"",
                    ""limitName"": ""gpt-reserve"",
                    ""normalModelSlug"": ""gpt-5.6-luna"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 20, ""resetsAt"": 1790160000 }
                }
            }
        }";
        string plan;
        List<ReservePool> reserves;
        List<ProbeWindow> windows = CodexSource.ParseWindows(ParseJson(json), out plan, out reserves);

        var pa = new ProbeAccount { Provider = "codex", Name = "D1", Windows = windows, Reserves = reserves, Ok = true };
        AccountData ad = Model.Flatten(pa, 1780000000);

        Check("TestD_RegularAvail", !ad.Availability.RegularAvailable, "regular exhausted");
        Check("TestD_GenericAvail", !ad.Availability.GenericReserveAvailable, "generic reserve exhausted");
        Check("TestD_LunaAvail", ad.Availability.LunaReserveAvailable, "luna reserve active");
        Check("TestD_EffLuna", ad.Availability.EffectiveLunaAvailable, "effective luna should be true");
        Check("TestD_EffGpt", !ad.Availability.EffectiveGptAvailable, "effective gpt should be false");
    }

    // Test E: regular = 0, Luna reserve = 0 -> Luna unavailable
    static void TestE_LunaReserveZero()
    {
        string json = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""planType"": ""plus"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
            },
            ""rateLimitsByLimitId"": {
                ""codex"": {
                    ""limitId"": ""codex"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
                },
                ""base_model_inference"": {
                    ""limitId"": ""base_model_inference"",
                    ""limitName"": ""gpt-reserve"",
                    ""normalModelSlug"": ""gpt-5.6-luna"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790160374 }
                }
            }
        }";
        string plan;
        List<ReservePool> reserves;
        List<ProbeWindow> windows = CodexSource.ParseWindows(ParseJson(json), out plan, out reserves);

        var pa = new ProbeAccount { Provider = "codex", Name = "E1", Windows = windows, Reserves = reserves, Ok = true };
        AccountData ad = Model.Flatten(pa, 1780000000);

        Check("TestE_RegularAvail", !ad.Availability.RegularAvailable, "regular exhausted");
        Check("TestE_LunaAvail", !ad.Availability.LunaReserveAvailable, "luna reserve exhausted");
        Check("TestE_EffLuna", !ad.Availability.EffectiveLunaAvailable, "effective luna should be false");
        Check("TestE_Reason", ad.Availability.LunaStatusReason == "luna-reserve exhausted", "reason: " + ad.Availability.LunaStatusReason);

        WindowData lunaWin = ad.Windows.Find(w => w.GroupLabel == "luna-reserve");
        Check("TestE_LunaWindowExists", lunaWin != null, "luna-reserve window should be present");
        if (lunaWin != null)
        {
            Check("TestE_LunaWinRemZero", lunaWin.Rem == 0, "rem should be 0%");
            Check("TestE_LunaWinReset", lunaWin.ResetEpoch == 1790160374, "reset epoch preserved");
        }
    }

    // Test F: both generic and Luna reserve present -> two independent rows, no value collision
    static void TestF_BothReservesPresent_NoCollision()
    {
        string json = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""planType"": ""plus"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
            },
            ""rateLimitsByLimitId"": {
                ""codex"": {
                    ""limitId"": ""codex"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
                },
                ""gen_pool"": {
                    ""limitId"": ""gen_pool"",
                    ""limitName"": ""gpt-reserve"",
                    ""normalModelSlug"": ""gpt-4"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 10, ""resetsAt"": 1790100000 }
                },
                ""base_model_inference"": {
                    ""limitId"": ""base_model_inference"",
                    ""limitName"": ""gpt-reserve"",
                    ""normalModelSlug"": ""gpt-5.6-luna"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 40, ""resetsAt"": 1790160000 }
                }
            }
        }";
        string plan;
        List<ReservePool> reserves;
        List<ProbeWindow> windows = CodexSource.ParseWindows(ParseJson(json), out plan, out reserves);

        var pa = new ProbeAccount { Provider = "codex", Name = "F1", Windows = windows, Reserves = reserves, Ok = true };
        AccountData ad = Model.Flatten(pa, 1780000000);

        WindowData genWin = ad.Windows.Find(w => w.GroupLabel == "gpt-reserve");
        WindowData lunaWin = ad.Windows.Find(w => w.GroupLabel == "luna-reserve");

        Check("TestF_GenWinFound", genWin != null, "gpt-reserve window found");
        Check("TestF_LunaWinFound", lunaWin != null, "luna-reserve window found");
        if (genWin != null && lunaWin != null)
        {
            Check("TestF_KeysDifferent", genWin.Key != lunaWin.Key, "keys must be different: " + genWin.Key + " vs " + lunaWin.Key);
            Check("TestF_GenRem", genWin.Rem == 90, "gen rem should be 90%, got " + genWin.Rem);
            Check("TestF_LunaRem", lunaWin.Rem == 60, "luna rem should be 60%, got " + lunaWin.Rem);
        }
    }

    // Test G: the REAL production availability boundary, plus proof that the
    // speculative router is gone. LIMISAW has no production operation that
    // selects an account or a model (the user picks an account; Connections
    // starts the vendor's own sign-in), so account/model routing belongs to
    // whatever consumer performs dispatch -- it must not be faked here.
    static void TestG_ProductionAvailabilityBoundary()
    {
        var regular = new List<ProbeWindow>
        {
            new ProbeWindow { Key = Model.FIVE_HOUR, DurationMinutes = 300, Remaining = 0, Available = true },
            new ProbeWindow { Key = Model.WEEKLY, DurationMinutes = 10080, Remaining = 90, Available = true }
        };

        // G1: a spent 5h window gates luna regular use; the luna reserve is
        // what keeps luna genuinely usable, through the production boundary.
        var lunaReserve = new List<ReservePool>
        {
            new ReservePool { Family = "luna", ModelSlug = "gpt-5.6-luna", Remaining = 60.0, Available = true, Allocated = true }
        };
        AccountAvailability lunaAv = Model.ComputeAvailability(regular, lunaReserve);
        Check("TestG1_RegularGated", !lunaAv.RegularAvailable, "spent 5h gates regular luna use");
        Check("TestG1_LunaViaReserve", lunaAv.EffectiveLunaAvailable, "luna usable through its reserve");
        Check("TestG1_GenericNotUsable", !lunaAv.EffectiveGptAvailable, "a luna reserve does not make generic gpt usable");

        // G2: a generic reserve makes generic gpt usable but NOT luna.
        var gptReserve = new List<ReservePool>
        {
            new ReservePool { Family = "generic_gpt", ModelSlug = "gpt-5", Remaining = 40.0, Available = true, Allocated = true }
        };
        AccountAvailability gptAv = Model.ComputeAvailability(regular, gptReserve);
        Check("TestG2_GptViaReserve", gptAv.EffectiveGptAvailable, "generic gpt usable through its reserve");
        Check("TestG2_LunaNotUsable", !gptAv.EffectiveLunaAvailable, "a generic reserve does not make luna usable");

        // G3: the speculative router is gone from every production source, and
        // nothing in production references it.
        string root = Directory.GetCurrentDirectory();
        string[] prod = { "Probe.cs", "LIMISAW.cs", "ProbeClaude.cs", "ProbeAntigravity.cs",
                          "ProbeZcode.cs", "Assets.cs", "ChildSweeper.cs", "Connections.cs" };
        bool routerGone = true;
        foreach (string f in prod)
        {
            string path = Path.Combine(root, f);
            if (File.Exists(path) && File.ReadAllText(path).Contains("ModelRouter")) routerGone = false;
        }
        Check("TestG3_RouterRemoved", routerGone, "no production file declares or references ModelRouter");
    }

    // Test H: reserve reset/expiry passes -> effective availability changes dynamically
    static void TestH_ResetExpiryDynamics()
    {
        double nowBefore = 1790000000;
        double expireEpoch = 1790000100;
        double nowAfter = 1790000200;

        var poolRecurring = new ReservePool
        {
            Id = "rec", Family = "luna", Label = "luna-reserve",
            Remaining = 0.0, ResetEpoch = expireEpoch, IsExpiry = false, Available = true, Allocated = true
        };

        var poolFixedExpiry = new ReservePool
        {
            Id = "exp", Family = "luna", Label = "luna-reserve",
            Remaining = 50.0, ResetEpoch = expireEpoch, IsExpiry = true, Available = true, Allocated = true
        };

        var pa = new ProbeAccount
        {
            Provider = "codex", Name = "H1", Ok = true,
            Reserves = new List<ReservePool> { poolFixedExpiry }
        };

        // Before expiry: usable
        AccountData adBefore = Model.Flatten(pa, nowBefore);
        Check("TestH_BeforeExpiryUsable", adBefore.Availability.LunaReserveAvailable, "luna reserve should be usable before expiry");

        // After expiry: fixed expiry sets Remaining = 0 and GatedBy = 'expired'
        AccountData adAfter = Model.Flatten(pa, nowAfter);
        Check("TestH_AfterExpiryUnusable", !adAfter.Availability.LunaReserveAvailable, "luna reserve should be unusable after expiry");
        Check("TestH_GatedByExpired", adAfter.Reserves[0].GatedBy == "expired", "gatedBy: " + adAfter.Reserves[0].GatedBy);
    }

    // Test I: unknown reserve type -> visible in display, NOT treated as Luna for routing
    static void TestI_UnknownReserveType()
    {
        string json = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""planType"": ""plus"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
            },
            ""rateLimitsByLimitId"": {
                ""codex"": {
                    ""limitId"": ""codex"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
                },
                ""special_compute"": {
                    ""limitId"": ""special_compute"",
                    ""limitName"": ""special-bonus"",
                    ""normalModelSlug"": ""special-x"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 10, ""resetsAt"": 1790100000 }
                }
            }
        }";
        string plan;
        List<ReservePool> reserves;
        List<ProbeWindow> windows = CodexSource.ParseWindows(ParseJson(json), out plan, out reserves);

        Check("TestI_ReservesCount", reserves.Count == 1, "expected 1 reserve");
        Check("TestI_FamilyUnknown", reserves[0].Family == "unknown", "family should be unknown: " + reserves[0].Family);
        Check("TestI_LabelPreserved", reserves[0].Label == "special-bonus", "label: " + reserves[0].Label);

        var pa = new ProbeAccount { Provider = "codex", Name = "I1", Windows = windows, Reserves = reserves, Ok = true };
        AccountData ad = Model.Flatten(pa, 1780000000);

        Check("TestI_LunaNotAvailable", !ad.Availability.EffectiveLunaAvailable, "luna not available from unknown pool");
        Check("TestI_GptNotAvailable", !ad.Availability.EffectiveGptAvailable, "gpt not available from unknown pool");
        Check("TestI_WindowDisplayRow", ad.Windows.Exists(w => w.GroupLabel == "special-bonus"), "window display row exists for UI");
    }

    // Test J: missing reserve fields -> no regression for existing accounts
    static void TestJ_MissingReserveFields()
    {
        string json = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""planType"": ""plus"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 30, ""resetsAt"": 1790000000 }
            },
            ""rateLimitsByLimitId"": {
                ""empty_pool"": null,
                ""partial_pool"": {
                    ""limitId"": ""partial_pool"",
                    ""limitName"": ""partial""
                }
            }
        }";
        string plan;
        List<ReservePool> reserves;
        List<ProbeWindow> windows = CodexSource.ParseWindows(ParseJson(json), out plan, out reserves);

        Check("TestJ_RegularWindowIntact", windows.Count >= 1 && windows[0].Group.Length == 0, "regular window intact");
        Check("TestJ_PartialPoolHandled", reserves.Count == 1 && !reserves[0].Allocated, "partial pool marked not allocated");
    }

    // Test K: malformed reserve payload -> fail safely, regular meters remain intact
    static void TestK_MalformedReservePayload()
    {
        string json = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""planType"": ""plus"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 45, ""resetsAt"": 1790000000 }
            },
            ""rateLimitsByLimitId"": ""this is not a dictionary""
        }";
        string plan;
        List<ReservePool> reserves;
        List<ProbeWindow> windows = CodexSource.ParseWindows(ParseJson(json), out plan, out reserves);

        Check("TestK_NoCrash", true, "ParseWindows handled string rateLimitsByLimitId without throwing");
        Check("TestK_WindowCount", windows.Count == 1, "regular window preserved");
        Check("TestK_ReservesCount", reserves.Count == 0, "0 reserves parsed");
    }

    static void TestM_RegularRequiresAllWindows()
    {
        var windows = new List<ProbeWindow>
        {
            new ProbeWindow { Key = Model.FIVE_HOUR, DurationMinutes = 300, Remaining = 50, Available = true },
            new ProbeWindow { Key = Model.WEEKLY, DurationMinutes = 10080, Remaining = 70, Available = true }
        };
        Check("TestM_BothPositive", Model.ComputeAvailability(windows, null).RegularAvailable, "both regular windows usable");
        windows[0].Remaining = 0;
        Check("TestM_FiveHourExhausted", !Model.ComputeAvailability(windows, null).RegularAvailable, "short window gates regular use");
        windows[0].Remaining = 50;
        windows[1].Remaining = 0;
        Check("TestM_WeeklyExhausted", !Model.ComputeAvailability(windows, null).RegularAvailable, "weekly window gates regular use");
        windows[1].Remaining = null;
        Check("TestM_UnknownUnavailable", !Model.ComputeAvailability(windows, null).RegularAvailable, "unknown window is not capacity");
        windows[1].Remaining = 0;
        windows[1].ResetEpoch = Stamp.Now - 10;
        Check("TestM_RecurringReset", Model.ComputeAvailability(windows, null).RegularAvailable, "elapsed recurring reset restores capacity");
    }

    static void TestN_ReserveEligibilityIsExplicit()
    {
        var reserve = new ReservePool { Family = "generic_gpt", ModelSlug = "gpt-5", Remaining = 50, Available = true, Allocated = true };
        Check("TestN_SlugMatch", reserve.SupportsModel("gpt-5"), "declared slug is eligible");
        Check("TestN_UnknownSlugRejected", !reserve.SupportsModel("gpt-4o"), "unlisted model is not inferred eligible");
        reserve.Family = "unknown";
        Check("TestN_UnknownFamilyRejected", !reserve.SupportsModel("gpt-5"), "unknown family remains non-authoritative");
    }

    // Test L: refresh changes reserve percentage -> updates existing row rather than duplicating it
    static void TestL_RefreshUpdatesInPlace()
    {
        string json1 = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
            },
            ""rateLimitsByLimitId"": {
                ""base_model_inference"": {
                    ""limitId"": ""base_model_inference"",
                    ""limitName"": ""gpt-reserve"",
                    ""normalModelSlug"": ""gpt-5.6-luna"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 40, ""resetsAt"": 1790160000 }
                }
            }
        }";

        string json2 = @"
        {
            ""rateLimits"": {
                ""limitId"": ""codex"",
                ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 100, ""resetsAt"": 1790000000 }
            },
            ""rateLimitsByLimitId"": {
                ""base_model_inference"": {
                    ""limitId"": ""base_model_inference"",
                    ""limitName"": ""gpt-reserve"",
                    ""normalModelSlug"": ""gpt-5.6-luna"",
                    ""primary"": { ""windowDurationMins"": 10080, ""usedPercent"": 85, ""resetsAt"": 1790160000 }
                }
            }
        }";

        string plan;
        List<ReservePool> res1, res2;
        List<ProbeWindow> win1 = CodexSource.ParseWindows(ParseJson(json1), out plan, out res1);
        List<ProbeWindow> win2 = CodexSource.ParseWindows(ParseJson(json2), out plan, out res2);

        var pa1 = new ProbeAccount { Provider = "codex", Name = "L1", Windows = win1, Reserves = res1, Ok = true };
        AccountData ad1 = Model.Flatten(pa1, 1780000000);

        var pa2 = new ProbeAccount { Provider = "codex", Name = "L1", Windows = win2, Reserves = res2, Ok = true };
        AccountData ad2 = Model.Flatten(pa2, 1780000000);

        WindowData w1 = ad1.Windows.Find(w => w.GroupLabel == "luna-reserve");
        WindowData w2 = ad2.Windows.Find(w => w.GroupLabel == "luna-reserve");

        Check("TestL_KeysIdentical", w1.Key == w2.Key, "key stable across sweeps: " + w1.Key);
        Check("TestL_Val1", w1.Rem == 60, "sweep 1 rem 60%");
        Check("TestL_Val2", w2.Rem == 15, "sweep 2 rem 15%");
    }
}
