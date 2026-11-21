using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Other mods' transpilers on a <c>Hook</c> method HookSkip filters, judged by the code they produce ("Foreign transpilers judged
/// by IL" in DESIGN). HookSkip's transpiler runs last (<c>Priority.Last</c>): the instructions Harmony hands it are the game's code
/// with every other mod's transpiler applied, at each rebuild of the method. They are kept (<see cref="Capture"/>) and confirmed
/// once that rebuild ends without an exception (<see cref="RebuildEnded"/>, from HarmonyWatch's finalizer on Harmony's
/// <c>UpdateWrapper</c>): no other mod's transpiler is ever run again here. The game's own code comes from Harmony's reader
/// (<c>PatchProcessor.GetOriginalInstructions</c>: no transpiler runs, factories refused first).
///
/// Accepted only when the result is the game's code, every instruction kept in order (same opcode, operand, branch targets, labels
/// and exception blocks), plus inserted runs that provably change nothing HookSkip relies on:
/// <list type="bullet">
/// <item>A run is straight code: no label, no exception block, no branch, no store of any kind, no address taken; only constants,
/// the hook's own arguments it never writes (static hooks), the current listener (below), <c>dup</c> of the current listener, <c>pop</c>
/// of its own values, and <c>call</c>s of static methods outside the game's assembly and this mod's code; it leaves the stack as it
/// found it and consumes nothing of it.</item>
/// <item>Outside every listener iteration (an abstract interpretation of the game's code: the listener walk's enumerator followed
/// through locals and the state machine's fields, async resumes modelled by the state field's constants), a run runs exactly
/// as often, with the same values, with this mod's filter as without it: HookSkip's premise is that the dropped listeners'
/// iterations change nothing the rest of the hook reads (classify.py's patterns), so the rest of the hook runs the same path.</item>
/// <item>Inside the iteration of exactly one walk loop, a run is one call taking the current listener of that loop (and only
/// constants or unwritten arguments otherwise) whose method provably does nothing unless the listener is of some types (the
/// type-gate shape: from its first instruction only <c>isinst</c> tests of that parameter, then a no-op exit); those types then keep
/// their iterations (treated as overriders, as for TypeGated defaults): a dropped listener's call would have done nothing.</item>
/// <item>The identity bridge (owner-approved premise of 2026-11-17, DESIGN "Owner-approved premises": Downfall's and Watcher's
/// <c>ModifyDamageInternal</c> shape): placed right after the game's own <c>stloc X</c> inside one listener iteration, a run
/// <c>ldloc &lt;listener&gt;; ldloc X; [constants, unwritten arguments, locals the game read before in that straight code];
/// call Bridge; stloc X</c>, whose bridge is gated on the listener's type and otherwise returns the argument fed by
/// <c>ldloc X</c> as it is (<see cref="IdentityGatesOn"/>): for a listener of none of the gate types it stores back the very
/// value the game stored, so a dropped iteration stays a no-op; the gate types keep their iterations.</item>
/// </list>
/// Anything else (a store, a branch, a call into the game, a reordered or changed instruction, a handler changed, an iteration
/// observer without a gate, a transpiler Harmony sorts after this mod's, a factory) refuses all of them: the hook stays vanilla.
/// </summary>
internal static class TranspilerJudge
{
    // ---- Instructions as compared: labels and branch targets as label ids (one ILGenerator per list) ----

    internal readonly record struct Block(ExceptionBlockType Kind, Type? Catch);

    internal sealed class Ins
    {
        public readonly OpCode Op;
        /// <summary>The operand; a branch's label id (int), a switch's label ids (int[]).</summary>
        public readonly object? Operand;
        public readonly int[] Labels;
        public readonly Block[] Blocks;

        public Ins(OpCode op, object? operand, int[] labels, Block[] blocks)
        {
            Op = op;
            Operand = operand;
            Labels = labels;
            Blocks = blocks;
        }

        public static Ins From(CodeInstruction code)
        {
            object? operand = code.operand;
            if (operand is Label label)
            {
                operand = label.GetHashCode();
            }
            else if (operand is Label[] labels)
            {
                operand = labels.Select(l => l.GetHashCode()).ToArray();
            }
            return new Ins(code.opcode, operand, code.labels.Select(l => l.GetHashCode()).ToArray(),
                code.blocks.Select(b => new Block(b.blockType, b.catchType)).ToArray());
        }

        public bool IsBranch => Op.OperandType is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget;
    }

    // ---- Capture: what HookSkip's transpiler was handed at the last rebuild that ended well ----

    private static readonly ConcurrentDictionary<(Module, int), Ins[]> _pending = new();
    /// <summary>Per method, the confirmed capture and its sequence number (in the verdicts' keys).</summary>
    private static readonly ConcurrentDictionary<(Module, int), (Ins[] Code, int Seq)> _captured = new();
    private static int _captureVersion;

    /// <summary>Changes at each confirmed capture (a cache built before is stale).</summary>
    public static int CaptureVersion => System.Threading.Volatile.Read(ref _captureVersion);

    private static (Module, int) Key(MethodBase method) => (method.Module, method.MetadataToken);

    /// <summary>From HookSkip's transpiler: the instructions it was handed (never throws: it runs inside another mod's Patch call too).</summary>
    internal static void Capture(MethodBase body, List<CodeInstruction> input)
    {
        try
        {
            _pending[Key(body)] = input.Select(Ins.From).ToArray();
        }
        catch
        {
            _pending.TryRemove(Key(body), out _);
        }
    }

    /// <summary>
    /// From HarmonyWatch's finalizer of <c>UpdateWrapper</c>: the rebuild whose transpilers produced the pending capture ended.
    /// Confirmed when it succeeded (Harmony stores the patch list it used right after), dropped otherwise (the method may have
    /// no detour now; HarmonyWatch faults what relied on it). Never throws.
    /// </summary>
    internal static void RebuildEnded(MethodBase original, bool succeeded)
    {
        try
        {
            (Module, int) key = Key(original);
            if (!_pending.TryRemove(key, out Ins[]? code))
            {
                if (!succeeded)
                {
                    _captured.TryRemove(key, out _);
                }
                return;
            }
            int seq = System.Threading.Interlocked.Increment(ref _captureVersion);
            if (succeeded)
            {
                _captured[key] = (code, seq);
            }
            else
            {
                _captured.TryRemove(key, out _);
            }
        }
        catch
        {
            // A capture not confirmed only keeps the hook vanilla.
        }
    }

    // ---- Verdicts ----

    internal sealed class Verdict
    {
        public bool Accepted;
        public string Why = "";
        public string Owners = "";
        /// <summary>Every other mod's transpiler on the method (allowed together when accepted).</summary>
        public MethodInfo[] Transpilers = Array.Empty<MethodInfo>();
        /// <summary>Types the gated insertions test: they keep their iterations.</summary>
        public Type[] Gates = Type.EmptyTypes;
        /// <summary>The gated methods called (their own patches checked at each recheck).</summary>
        public MethodInfo[] GatedCallees = Array.Empty<MethodInfo>();
        public int Runs;
        /// <summary>Of the runs, the identity bridges (<see cref="IdentityGatesOn"/>): the premise's kept note names them.</summary>
        public int Bridges;
        public string Key = "";
    }

    private static readonly ConcurrentDictionary<(Module, int), Verdict> _verdicts = new();
    private static readonly object _judging = new();

#if BENCH
    /// <summary>Hook bodies whose other mods' transpilers were accepted, and refused (the foreign tests' report).</summary>
    public static IReadOnlyList<string> Report() => _verdicts.Values.Where(v => v.Owners.Length > 0)
        .Select(v => $"{v.Key.Split('#')[0]} {(v.Accepted ? "accepted" : "refused")} ({v.Owners}): {(v.Accepted ? $"{v.Runs} insertion(s)" + (v.Gates.Length > 0 ? ", gates " + string.Join("|", v.Gates.Select(g => g.Name)) : "") : v.Why)}")
        .OrderBy(s => s, StringComparer.Ordinal).ToList();
#endif

    /// <summary>
    /// The verdict on every other mod's transpiler on <paramref name="body"/> (judged together: the capture holds their combined
    /// result), or null when there is none beyond <paramref name="allowed"/>. <paramref name="own"/>:
    /// HookSkip's transpiler (it must be Harmony's last); <paramref name="isWalk"/>: the listener walk calls HookSkip redirects.
    /// Cached per method while its transpiler list and capture stay the same (the gated methods' own patches asked again each
    /// time). Never throws: a question it can't answer refuses.
    /// </summary>
    internal static Verdict? Judge(MethodBase body, MethodInfo own, Func<MethodInfo, bool> isWalk, string hookName, MethodInfo[] allowed)
    {
        try
        {
            HarmonyLib.Patches? info = Harmony.GetPatchInfo(body);
            Patch[] transpilers = info?.Transpilers.ToArray() ?? Array.Empty<Patch>();
            Patch[] foreign = transpilers.Where(p => !PatchGuard.IsOurs(p.owner)).ToArray();
            // None, or only transpilers the caller already lets stand (BaseLib's, by fingerprint): nothing to judge.
            if (foreign.All(p => PatchSafety.PatchMethodOf(p) is { } m && allowed.Any(a => a.MethodHandle == m.MethodHandle)))
            {
                return null;
            }
            (Module, int) key = Key(body);
            string text = hookName + "#" + string.Join(";", transpilers.Select(p => $"{p.owner}|{p.index}|{p.priority}|{PatchSafety.PatchMethodOf(p)?.MetadataToken}|{PatchSafety.PatchMethodOf(p)?.Module.ModuleVersionId}"))
                + "#" + (_captured.TryGetValue(key, out var capturedNow) ? capturedNow.Seq : 0);
            if (_verdicts.TryGetValue(key, out Verdict? known) && known.Key == text)
            {
                return known.Accepted && known.GatedCallees.Any(Patched) ? Refused(text, "a gated method is itself patched", foreign) : known;
            }
            lock (_judging)
            {
                Verdict verdict = Decide(body, own, isWalk, transpilers, foreign, text);
                bool waiting = verdict.Why.StartsWith("no confirmed capture", StringComparison.Ordinal);
                if (!waiting && !(verdict.Accepted && verdict.GatedCallees.Any(Patched)))
                {
                    Log.Info($"[{ModEntry.Id}] {HookSkip.Name}: Hook.{hookName}: other mods' transpilers judged by their IL ({verdict.Owners}): "
                        + (verdict.Accepted ? $"{verdict.Runs} insertion(s) that change nothing this mod relies on"
                            + (verdict.Bridges > 0 ? $" ({verdict.Bridges} storing their result back into the iteration's own: identity bridges, a stated premise)" : "")
                            + (verdict.Gates.Length > 0 ? $", gated on {string.Join(", ", verdict.Gates.Select(g => g.Name))} (kept)" : "") + ": kept"
                            : $"refused ({verdict.Why})"));
                }
                if (!waiting)
                {
                    NoteBridges(hookName, body, verdict.Accepted && !verdict.GatedCallees.Any(Patched) && verdict.Bridges > 0 ? verdict : null);
                }
                // A verdict missing its capture (the rebuild still under way) is asked again next time, not kept.
                if (!waiting)
                {
                    _verdicts[key] = verdict;
                }
                return verdict.Accepted && verdict.GatedCallees.Any(Patched) ? Refused(text, "a gated method is itself patched", foreign) : verdict;
            }
        }
        catch (Exception e)
        {
            return new Verdict { Why = "not judged: " + e.Message, Owners = "?" };
        }
    }

    private static bool Patched(MethodInfo method) => Harmony.GetPatchInfo(method) is { } info && PatchSafety.AllPatches(info).Any();

    /// <summary>Per hook with accepted identity bridges: the mods (the kept note's text).</summary>
    private static readonly ConcurrentDictionary<string, string[]> _bridged = new(StringComparer.Ordinal);

    /// <summary>
    /// The identity-bridge premise's kept note (bdopt why, the menu's hover): the hooks whose accepted transpilers store a gated
    /// bridge's result back into the iteration's own local, and their mods; the hook dropped from it when its verdict has none.
    /// </summary>
    private static void NoteBridges(string hook, MethodBase body, Verdict? verdict)
    {
        try
        {
            if (verdict == null)
            {
                _bridged.TryRemove(hook, out _);
            }
            else
            {
                _bridged[hook] = PatchSafety.OtherModsPatching(new MethodBase?[] { body });
            }
            if (_bridged.IsEmpty)
            {
                PatchSafety.ClearKept(HookSkip.Name, "identity_bridges");
                return;
            }
            string hooks = string.Join(", ", _bridged.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => "Hook." + k));
            PatchSafety.Kept(HookSkip.Name, "identity_bridges", $"other mods' code passing each listener's result through their own method ({hooks}) "
                + "is judged from its code to give the game's value back for the listeners of other types: those listeners are still skipped "
                + "(a stated premise: the values it reads at a kept listener's turn are the game's own)", _bridged.Values.SelectMany(m => m).ToArray());
        }
        catch
        {
            // Display only.
        }
    }

    private static Verdict Refused(string key, string why, Patch[] foreign) =>
        new() { Why = why, Key = key, Owners = string.Join(", ", foreign.Select(p => p.owner).Distinct()) };

    private static Verdict Decide(MethodBase body, MethodInfo own, Func<MethodInfo, bool> isWalk, Patch[] transpilers, Patch[] foreign, string key)
    {
        string owners = string.Join(", ", foreign.Select(p => p.owner).Distinct());
        Verdict Refuse(string why) => new() { Why = why, Key = key, Owners = owners };
        // Harmony's order: this mod's transpiler last (the capture then holds every other one's output), once.
        var methods = new MethodInfo[transpilers.Length];
        for (int i = 0; i < transpilers.Length; i++)
        {
            MethodInfo? method = PatchSafety.PatchMethodOf(transpilers[i]);
            if (method == null)
            {
                return Refuse("a transpiler's method not found");
            }
            // Harmony's sort (and its reader) would call a factory: never run another mod's code here.
            if (PatchSafety.IsFactory(method))
            {
                return Refuse("a transpiler made by a factory method");
            }
            methods[i] = method;
        }
        if (transpilers.Count(p => PatchGuard.IsOurs(p.owner)) != 1 || !methods.Any(m => m.MethodHandle == own.MethodHandle))
        {
            return Refuse("this mod's transpiler isn't the only one of this mod there");
        }
        for (int i = 0; i < transpilers.Length; i++)
        {
            transpilers[i].PatchMethod = methods[i];
        }
        List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(body, transpilers);
        if (sorted.Count == 0 || sorted[^1].MethodHandle != own.MethodHandle)
        {
            return Refuse("another mod's transpiler runs after this mod's (it would see the redirected walk)");
        }
        if (!_captured.TryGetValue(Key(body), out var captured))
        {
            return Refuse("no confirmed capture of the transpiled code yet");
        }
        Ins[] result = captured.Code;
        Ins[] original = PatchProcessor.GetOriginalInstructions(body).Select(Ins.From).ToArray();
        string? why = JudgeCode(body, original, result, isWalk, out Type[] gates, out MethodInfo[] callees, out int runs, out int bridges);
        if (why != null)
        {
            return Refuse(why);
        }
        return new Verdict
        {
            Accepted = true, Key = key, Owners = owners, Gates = gates, GatedCallees = callees, Runs = runs, Bridges = bridges,
            Transpilers = foreign.Select(p => PatchSafety.PatchMethodOf(p)!).ToArray(),
        };
    }

    /// <summary>
    /// The judgement itself, on two instruction lists of <paramref name="body"/> (the game's, and another mod's result): null when
    /// accepted (with the gate types, gated methods and number of inserted runs), else why not. Also the test tooling's entry.
    /// </summary>
    internal static string? JudgeCode(MethodBase body, IReadOnlyList<Ins> original, IReadOnlyList<Ins> result, Func<MethodInfo, bool> isWalk,
        out Type[] gates, out MethodInfo[] callees, out int runs) => JudgeCode(body, original, result, isWalk, out gates, out callees, out runs, out _);

    /// <summary><see cref="JudgeCode(MethodBase, IReadOnlyList{Ins}, IReadOnlyList{Ins}, Func{MethodInfo, bool}, out Type[], out MethodInfo[], out int)"/>, with the identity bridges counted.</summary>
    internal static string? JudgeCode(MethodBase body, IReadOnlyList<Ins> original, IReadOnlyList<Ins> result, Func<MethodInfo, bool> isWalk,
        out Type[] gates, out MethodInfo[] callees, out int runs, out int bridges)
    {
        bridges = 0;
        gates = Type.EmptyTypes;
        callees = Array.Empty<MethodInfo>();
        runs = 0;
        int[]? match = Align(original, result, out string? alignWhy);
        if (match == null)
        {
            return alignWhy;
        }
        if (CheckStructure(original, result, match) is { } structural)
        {
            return structural;
        }
        // The inserted runs, each before the original instruction it precedes.
        var inserted = new List<(int Gap, List<Ins> Run)>();
        int next = 0;
        for (int i = 0; i <= original.Count; i++)
        {
            int until = i < original.Count ? match[i] : result.Count;
            if (until > next)
            {
                inserted.Add((i, result.Skip(next).Take(until - next).ToList()));
            }
            next = until + 1;
        }
        if (inserted.Count == 0)
        {
            return null; // The game's code unchanged (an identity transpiler).
        }
        var analysis = new Analysis(body, original, isWalk);
        if (analysis.Run() is { } failed)
        {
            return "the game's code not understood: " + failed;
        }
        var gateList = new List<Type>();
        var calleeList = new List<MethodInfo>();
        foreach ((int found, List<Ins> foundRun) in inserted)
        {
            (int gap, List<Ins> run) = AfterStore(original, found, foundRun);
            if (gap >= original.Count)
            {
                return "code appended after the method's last instruction";
            }
            if (gap > 0 && Unconditional(original[gap - 1].Op))
            {
                return "code inserted after a jump (only reached through a label)";
            }
            if (analysis.Gap(gap) is not { } state)
            {
                return "code inserted where the game's code never goes";
            }
            if (CheckRun(body, original, run, gap, state, analysis, gateList, calleeList, ref bridges) is { } why)
            {
                return why + $" (inserted before instruction {gap}, {original[gap].Op})";
            }
        }
        gates = gateList.Distinct().ToArray();
        callees = calleeList.Distinct().ToArray();
        runs = inserted.Count;
        return null;
    }

    /// <summary>
    /// The same code read the other way (the identity bridge's form, shape rules on): a run inserted before the game's
    /// <c>stloc X</c> that starts with that same <c>stloc X</c> (the alignment matched the game's store with the run's last one) is the
    /// run that follows the game's store: <c>stloc X; A</c> before <c>stloc X</c> is <c>A; stloc X</c> after it (neither carries a
    /// label or an exception mark). Any other run as found.
    /// </summary>
    private static (int Gap, List<Ins> Run) AfterStore(IReadOnlyList<Ins> original, int gap, List<Ins> run)
    {
        if (!PatchSafety.ShapeRules || gap >= original.Count - 1 || run.Count < 2 || LocalIndex(original[gap].Op, original[gap].Operand, load: false) is not int
            || !Same(run[0], original[gap]) || run[0].Labels.Length > 0 || run[0].Blocks.Length > 0 || original[gap].Labels.Length > 0
            || original[gap].Blocks.Length > 0)
        {
            return (gap, run);
        }
        var rotated = run.Skip(1).ToList();
        rotated.Add(original[gap]);
        return (gap + 1, rotated);
    }

    /// <summary>No fall-through after it (br, leave, ret, throw, rethrow, endfinally).</summary>
    private static bool Unconditional(OpCode op) => op.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw;

    // ---- Alignment: every original instruction matched in order, the fewest inserted runs ----

    private const int MaxCells = 1_500_000;

    private static int[]? Align(IReadOnlyList<Ins> o, IReadOnlyList<Ins> f, out string? why)
    {
        why = null;
        int n = o.Count, m = f.Count;
        if (m < n)
        {
            why = "instructions of the game's code removed";
            return null;
        }
        if ((long)(n + 1) * (m + 1) > MaxCells)
        {
            why = "too large to judge";
            return null;
        }
        // Unchanged: the usual case for an identity transpiler, no table.
        if (n == m && Enumerable.Range(0, n).All(i => Same(o[i], f[i])))
        {
            return Enumerable.Range(0, n).ToArray();
        }
        const int Inf = int.MaxValue / 4;
        int width = m + 1;
        // Costs (number of inserted runs) ending in a match (a) or an insertion (b), two rows; the choices kept for the walk back.
        var aPrev = new int[width];
        var bPrev = new int[width];
        var aCur = new int[width];
        var bCur = new int[width];
        var fromA = new byte[(n + 1) * width]; // For a match at (i, j): 1 when the cell before was a match.
        var fromB = new byte[(n + 1) * width]; // For an insertion at (i, j): 1 when the cell before was a match.
        for (int j = 0; j <= m; j++)
        {
            aPrev[j] = j == 0 ? 0 : Inf;
            bPrev[j] = j == 0 ? Inf : 1;
            if (j > 0)
            {
                fromB[j] = (byte)(j == 1 ? 1 : 0);
            }
        }
        for (int i = 1; i <= n; i++)
        {
            aCur[0] = Inf;
            bCur[0] = Inf;
            for (int j = 1; j <= m; j++)
            {
                int a = Inf;
                if (Same(o[i - 1], f[j - 1]))
                {
                    int viaA = aPrev[j - 1], viaB = bPrev[j - 1];
                    a = Math.Min(viaA, viaB);
                    fromA[i * width + j] = (byte)(viaA <= viaB ? 1 : 0);
                }
                aCur[j] = a;
                int startRun = aCur[j - 1] >= Inf ? Inf : aCur[j - 1] + 1, keepRun = bCur[j - 1];
                bCur[j] = Math.Min(startRun, keepRun);
                fromB[i * width + j] = (byte)(startRun <= keepRun ? 1 : 0);
            }
            (aPrev, aCur) = (aCur, aPrev);
            (bPrev, bCur) = (bCur, bPrev);
        }
        if (aPrev[m] >= Inf && bPrev[m] >= Inf)
        {
            why = "the game's code not found in order in the result (an instruction changed or moved)";
            return null;
        }
        var match = new int[n];
        int ii = n, jj = m;
        bool inMatch = aPrev[m] <= bPrev[m];
        while (jj > 0)
        {
            if (inMatch)
            {
                if (ii == 0)
                {
                    why = "alignment failed";
                    return null;
                }
                match[ii - 1] = jj - 1;
                inMatch = fromA[ii * width + jj] == 1;
                ii--;
                jj--;
            }
            else
            {
                inMatch = fromB[ii * width + jj] == 1;
                jj--;
            }
        }
        if (ii != 0)
        {
            why = "alignment failed";
            return null;
        }
        return match;
    }

    /// <summary>The same instruction for the alignment (labels and branch targets checked afterwards, structurally).</summary>
    private static bool Same(Ins a, Ins b)
    {
        if (a.Op != b.Op)
        {
            return false;
        }
        object? x = a.Operand, y = b.Operand;
        if (a.IsBranch)
        {
            return x is int && y is int;
        }
        if (x is int[] xs && y is int[] ys)
        {
            return xs.Length == ys.Length;
        }
        return SameOperand(x, y);
    }

    private static bool SameOperand(object? x, object? y)
    {
        if (x == null || y == null)
        {
            return x == null && y == null;
        }
        if (x is LocalVariableInfo lx)
        {
            return y is LocalVariableInfo ly ? lx.LocalIndex == ly.LocalIndex : IsIntegral(y) && Convert.ToInt64(y) == lx.LocalIndex;
        }
        if (y is LocalVariableInfo)
        {
            return SameOperand(y, x);
        }
        if (IsIntegral(x) && IsIntegral(y))
        {
            return Convert.ToInt64(x) == Convert.ToInt64(y);
        }
        if (x is MemberInfo mx && y is MemberInfo my)
        {
            if (mx.Equals(my))
            {
                return true;
            }
            if (mx.MetadataToken != my.MetadataToken || mx.Module != my.Module || mx.DeclaringType != my.DeclaringType)
            {
                return false;
            }
            return mx is not MethodInfo { IsGenericMethod: true } gx
                || (my is MethodInfo { IsGenericMethod: true } gy && gx.GetGenericArguments().SequenceEqual(gy.GetGenericArguments()));
        }
        return x.Equals(y);
    }

    private static bool IsIntegral(object value) => value is sbyte or byte or short or ushort or int or uint or long;

    /// <summary>Labels, branch targets and exception blocks: the game's structure unchanged, nothing inserted carrying any.</summary>
    private static string? CheckStructure(IReadOnlyList<Ins> o, IReadOnlyList<Ins> f, int[] match)
    {
        var matched = new bool[f.Count];
        foreach (int j in match)
        {
            matched[j] = true;
        }
        var labelO = new Dictionary<int, int>();
        for (int i = 0; i < o.Count; i++)
        {
            foreach (int label in o[i].Labels)
            {
                labelO[label] = i;
            }
        }
        var labelF = new Dictionary<int, int>();
        for (int j = 0; j < f.Count; j++)
        {
            foreach (int label in f[j].Labels)
            {
                if (!labelF.TryAdd(label, j))
                {
                    return "a label on two instructions";
                }
            }
            if (!matched[j] && (f[j].Labels.Length > 0 || f[j].Blocks.Length > 0))
            {
                return f[j].Labels.Length > 0 ? "an inserted instruction carries a label (a branch may reach it)" : "an inserted instruction carries an exception block";
            }
        }
        for (int i = 0; i < o.Count; i++)
        {
            Ins a = o[i], b = f[match[i]];
            if (a.Blocks.Length != b.Blocks.Length || !a.Blocks.SequenceEqual(b.Blocks))
            {
                return $"exception blocks changed at instruction {i}";
            }
            if (a.Operand is int[] targetsA && b.Operand is int[] targetsB)
            {
                for (int k = 0; k < targetsA.Length; k++)
                {
                    if (!SameTarget(targetsA[k], targetsB[k]))
                    {
                        return $"switch target changed at instruction {i}";
                    }
                }
            }
            else if (a.IsBranch && !SameTarget((int)a.Operand!, (int)b.Operand!))
            {
                return $"branch target changed at instruction {i}";
            }
        }
        return null;

        bool SameTarget(int la, int lb) => labelO.TryGetValue(la, out int ta) && labelF.TryGetValue(lb, out int tb) && match[ta] == tb;
    }

    /// <summary>
    /// Null when <paramref name="result"/> is <paramref name="original"/> exactly (<see cref="UnchangedTranspilers"/>' rule): as many
    /// instructions, each with the same opcode and operand (<see cref="Same"/>), every branch, <c>leave</c> and switch target at the
    /// same instruction, the same exception blocks on each, no label on two instructions; else why not. Labels no branch reaches
    /// may differ (nothing jumps there). Never throws.
    /// </summary>
    internal static string? Unchanged(IReadOnlyList<Ins> original, IReadOnlyList<Ins> result)
    {
        try
        {
            if (original.Count != result.Count)
            {
                return $"{result.Count} instructions where the game has {original.Count}";
            }
            for (int i = 0; i < original.Count; i++)
            {
                if (!Same(original[i], result[i]))
                {
                    return $"instruction {i} changed ({original[i].Op} {original[i].Operand} -> {result[i].Op} {result[i].Operand})";
                }
            }
            return CheckStructure(original, result, Enumerable.Range(0, original.Count).ToArray());
        }
        catch (Exception e)
        {
            return "not judged: " + e.Message;
        }
    }

    /// <summary>The confirmed capture of <paramref name="body"/>'s transpiled code (the last rebuild that ended well), if any.</summary>
    internal static bool TryCaptured(MethodBase body, out Ins[] code)
    {
        if (_captured.TryGetValue(Key(body), out var captured))
        {
            code = captured.Code;
            return true;
        }
        code = Array.Empty<Ins>();
        return false;
    }

    /// <summary>The code takes the address of the local ("local n") or field <paramref name="key"/> names.</summary>
    private static bool AddressTaken(IReadOnlyList<Ins> code, object key) => code.Any(ins =>
        key is string local
            ? (ins.Op == OpCodes.Ldloca || ins.Op == OpCodes.Ldloca_S) && "local " + OperandIndex(ins.Operand) == local
            : (ins.Op == OpCodes.Ldflda || ins.Op == OpCodes.Ldsflda) && ins.Operand is FieldInfo field && SameOperand(field, key));

    // ---- The inserted runs ----

    /// <summary>
    /// A run value: the game's stack (Entry), a constant or unwritten value (Pure), the current listener, a gated call's result, the
    /// iteration's own local X the game just stored (Stored: only an identity bridge's argument).
    /// </summary>
    private enum RunKind : byte { Entry, Pure, Listener, Result, Stored }

    private readonly record struct RunValue(RunKind Kind, int Index);

    private static string? CheckRun(MethodBase body, IReadOnlyList<Ins> original, List<Ins> run, int gapIndex, State gap, Analysis analysis,
        List<Type> gateList, List<MethodInfo> calleeList, ref int bridges)
    {
        var stack = new List<RunValue>();
        for (int i = 0; i < gap.Stack.Count; i++)
        {
            stack.Add(new RunValue(RunKind.Entry, i));
        }
        int plainCalls = 0, gatedCalls = 0, gatedLoop = -1, identityCalls = 0;
        // The identity bridge's form (the premise, shape rules on): the run ends with stloc X, right after the game's own stloc X.
        int last = run.FindLastIndex(r => r.Op != OpCodes.Nop);
        int? bridged = IdentityStore(body, original, run, gapIndex, last);
        if (bridged != null)
        {
            // Several mods' bridges after the same store (Downfall's and Watcher's): one segment each, every one ending with its own
            // stloc X and judged alone; each reads the value the one before stored back (the game's own, for a listener none gates).
            var ends = Enumerable.Range(0, run.Count).Where(k => LocalIndex(run[k].Op, run[k].Operand, load: false) == bridged).ToList();
            if (ends.Count > 1)
            {
                int from = 0;
                foreach (int end in ends)
                {
                    if (CheckRun(body, original, run.GetRange(from, end - from + 1), gapIndex, gap, analysis, gateList, calleeList, ref bridges) is { } segment)
                    {
                        return segment;
                    }
                    from = end + 1;
                }
                return run.Skip(from).All(r => r.Op == OpCodes.Nop) ? null : "code after the last identity bridge's store";
            }
        }
        // A store, branch or address first named as such (whatever else the run does).
        for (int i = 0; i < run.Count; i++)
        {
            Ins ins = run[i];
            if (bridged != null && i == last)
            {
                continue; // The identity bridge's terminal store, judged below.
            }
            if (Analysis.Writes(ins) || ins.Op.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)
            {
                return $"{ins.Op.Name} inserted (no store, branch or address is accepted)";
            }
        }
        for (int i = 0; i < run.Count; i++)
        {
            Ins ins = run[i];
            OpCode op = ins.Op;
            if (op == OpCodes.Nop)
            {
                continue;
            }
            if (bridged != null && i == last)
            {
                // stloc X: the identity bridge's result, and nothing else of the run left.
                if (identityCalls != 1 || stack.Count == 0 || stack[^1].Kind != RunKind.Result)
                {
                    return $"local {bridged} stored from something other than one identity bridge's result";
                }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            if (IsConstant(op))
            {
                stack.Add(new RunValue(RunKind.Pure, -1));
                continue;
            }
            if (ArgumentIndex(op, ins.Operand) is int arg)
            {
                if (!body.IsStatic && arg == 0)
                {
                    // The state machine's own field, only when it holds the current listener there.
                    if (i + 1 >= run.Count || run[i + 1].Op != OpCodes.Ldfld || run[i + 1].Operand is not FieldInfo field
                        || field.DeclaringType != body.DeclaringType)
                    {
                        return "the state machine itself loaded";
                    }
                    Value held = gap.Field(field);
                    if (held.Kind != Kind.Current)
                    {
                        return $"field {field.Name} of the state machine read (not the current listener there)";
                    }
                    stack.Add(new RunValue(RunKind.Listener, held.N));
                    i++;
                    continue;
                }
                if (!analysis.ArgumentNeverWritten(arg))
                {
                    return $"argument {arg} read (the game's code writes it or takes its address)";
                }
                stack.Add(new RunValue(RunKind.Pure, -1));
                continue;
            }
            if (LocalIndex(op, ins.Operand, load: true) is int local)
            {
                Value held = local < gap.Locals.Length ? gap.Locals[local] : Value.Unknown;
                if (held.Kind != Kind.Current)
                {
                    if (bridged == local)
                    {
                        // The value the game just stored (an identity bridge's argument only).
                        stack.Add(new RunValue(RunKind.Stored, local));
                        continue;
                    }
                    if (bridged != null && ReadBefore(original, gapIndex, local))
                    {
                        // A local the game's own code read just before in the same straight code: the same value here.
                        stack.Add(new RunValue(RunKind.Pure, -1));
                        continue;
                    }
                    return $"local {local} read (not the current listener there)";
                }
                stack.Add(new RunValue(RunKind.Listener, held.N));
                continue;
            }
            if (op == OpCodes.Dup)
            {
                if (stack.Count == 0)
                {
                    return "dup of an empty stack";
                }
                RunValue top = stack[^1];
                if (top.Kind == RunKind.Stored)
                {
                    return "the stored local copied";
                }
                if (top.Kind == RunKind.Entry)
                {
                    Value held = gap.Stack[top.Index];
                    if (held.Kind != Kind.Current)
                    {
                        return "a value of the game's stack copied (not the current listener)";
                    }
                    stack.Add(new RunValue(RunKind.Listener, held.N));
                }
                else
                {
                    stack.Add(top);
                }
                continue;
            }
            if (op == OpCodes.Pop)
            {
                if (stack.Count == 0 || stack[^1].Kind == RunKind.Entry)
                {
                    return "a value of the game's stack popped";
                }
                if (stack[^1].Kind == RunKind.Stored)
                {
                    return "the stored local popped";
                }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            if (op == OpCodes.Call && ins.Operand is MethodInfo callee)
            {
                if (CalleeRefusal(callee) is { } refused)
                {
                    return refused;
                }
                ParameterInfo[] parameters = callee.GetParameters();
                if (stack.Count < parameters.Length)
                {
                    return "a call with too few values";
                }
                int listenerAt = -1, loop = -1, storedAt = -1;
                for (int p = parameters.Length - 1; p >= 0; p--)
                {
                    RunValue value = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if (value.Kind == RunKind.Entry)
                    {
                        return $"{Describe(callee)} takes a value of the game's stack";
                    }
                    if (value.Kind == RunKind.Stored)
                    {
                        if (storedAt >= 0)
                        {
                            return $"{Describe(callee)} takes the stored local twice";
                        }
                        storedAt = p;
                        continue;
                    }
                    if (value.Kind == RunKind.Listener)
                    {
                        if (listenerAt >= 0)
                        {
                            return $"{Describe(callee)} takes the listener twice";
                        }
                        listenerAt = p;
                        loop = value.Index;
                    }
                }
                if (storedAt >= 0 && listenerAt < 0)
                {
                    return $"{Describe(callee)} takes the stored local but not the current listener";
                }
                if (listenerAt >= 0)
                {
                    Type[] gates;
                    if (storedAt >= 0)
                    {
                        // The identity bridge: gated on the listener, else gives the stored value back as it is.
                        if (IdentityGatesOn(callee, listenerAt, storedAt, LocalType(body, bridged!.Value)) is not { } identity)
                        {
                            return $"{Describe(callee)} takes the stored local but doesn't provably give it back unless the listener is of its gate types";
                        }
                        gates = identity;
                        identityCalls++;
                    }
                    else if (GatesOn(callee, listenerAt) is not { } plain)
                    {
                        return $"{Describe(callee)} takes the current listener but isn't provably gated on its type";
                    }
                    else
                    {
                        gates = plain;
                    }
                    if (gatedLoop >= 0 && gatedLoop != loop)
                    {
                        return "listeners of two loops passed";
                    }
                    gatedLoop = loop;
                    gatedCalls++;
                    gateList.AddRange(gates);
                    calleeList.Add(callee);
                }
                else
                {
                    plainCalls++;
                }
                if (callee.ReturnType != typeof(void))
                {
                    stack.Add(new RunValue(listenerAt >= 0 ? RunKind.Result : RunKind.Pure, -1));
                }
                continue;
            }
            return op == OpCodes.Call ? "a call of something other than a method"
                : $"{op.Name} inserted (only loads, dup, pop and static calls are accepted: no store, branch, field or address)";
        }
        if (stack.Count != gap.Stack.Count || stack.Where((v, k) => v.Kind != RunKind.Entry || v.Index != k).Any())
        {
            return "the stack left changed";
        }
        if (bridged != null)
        {
            if (identityCalls != 1 || gatedCalls != 1 || plainCalls > 0)
            {
                return $"local {bridged} stored back by something other than exactly one identity bridge";
            }
            if (!gap.OnlyIn(gatedLoop))
            {
                return "the identity bridge isn't inside exactly one listener walk's iteration";
            }
            bridges++;
            return null;
        }
        if (gatedCalls == 0)
        {
            return gap.AllOut ? null : "runs inside a listener's iteration (or may) without a type gate on the listener: its calls would follow the walk's length";
        }
        if (plainCalls > 0)
        {
            return "a call besides the gated one inside a listener's iteration";
        }
        return gap.OnlyIn(gatedLoop) ? null : "the gated call isn't inside exactly one listener walk's iteration";
    }

    /// <summary>
    /// The listener calls whose result local an identity bridge may store back into (re-audit of the premises, DMG-1: the
    /// owner-approved scope, nothing wider): <c>Hook.ModifyDamageInternal</c>'s listener loops (v0.113's late multiplicative one
    /// included), the <c>AbstractModel</c> virtual the game calls on the current listener right before its <c>stloc</c>.
    /// </summary>
    private static readonly string[] IdentityResultCalls =
        { "ModifyDamageAdditive", "ModifyDamageMultiplicative", "ModifyDamageMultiplicativeLate", "ModifyDamageCap" };

    private const string IdentityHook = "ModifyDamageInternal";

    /// <summary>
    /// The local the identity bridge stores back (the premise's form, shape rules on), or null: the hook is
    /// <c>Hook.ModifyDamageInternal</c>, the game's instructions just before the gap are its own <c>callvirt</c> of a listener's
    /// damage modifier (<see cref="IdentityResultCalls"/>, returning X's type) and <c>stloc X</c>, the run's last instruction is
    /// <c>stloc X</c>, neither the gap nor that store is a jump target, and X's address is never taken (a call can't change it then).
    /// </summary>
    private static int? IdentityStore(MethodBase body, IReadOnlyList<Ins> original, List<Ins> run, int gapIndex, int last)
    {
        if (!PatchSafety.ShapeRules || last < 0 || gapIndex <= 1 || gapIndex >= original.Count
            || body.DeclaringType != typeof(MegaCrit.Sts2.Core.Hooks.Hook) || body.Name != IdentityHook
            || original[gapIndex - 2].Op != OpCodes.Callvirt || original[gapIndex - 2].Operand is not MethodInfo result
            || result.DeclaringType != typeof(AbstractModel) || Array.IndexOf(IdentityResultCalls, result.Name) < 0
            || LocalIndex(run[last].Op, run[last].Operand, load: false) is not int stored
            || LocalIndex(original[gapIndex - 1].Op, original[gapIndex - 1].Operand, load: false) != stored
            || original[gapIndex - 2].Labels.Length > 0 || LocalType(body, stored) != result.ReturnType
            || original[gapIndex - 1].Labels.Length > 0 || original[gapIndex].Labels.Length > 0 || original[gapIndex].Blocks.Length > 0
            || AddressTaken(original, "local " + stored) || AddressTaken(run, "local " + stored) || LocalType(body, stored) is null)
        {
            return null;
        }
        return stored;
    }

    /// <summary>A local's declared type in the game's method, or null.</summary>
    private static Type? LocalType(MethodBase body, int local)
    {
        try
        {
            IList<LocalVariableInfo>? locals = body.GetMethodBody()?.LocalVariables;
            return locals != null && local >= 0 && local < locals.Count && !locals[local].LocalType.IsByRef ? locals[local].LocalType : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The game's own code read <paramref name="local"/> before the gap in the same straight code: going back from the gap, an
    /// <c>ldloc</c> of it is met before any write of it, any jump target (a path that would skip that read), any exception mark and
    /// any instruction without a fall-through; at most 32 instructions back. Its address is never taken (a call can't change it).
    /// </summary>
    private static bool ReadBefore(IReadOnlyList<Ins> original, int gapIndex, int local)
    {
        if (AddressTaken(original, "local " + local))
        {
            return false;
        }
        for (int k = gapIndex - 1; k >= 0 && k >= gapIndex - 32; k--)
        {
            Ins ins = original[k];
            if (LocalIndex(ins.Op, ins.Operand, load: true) == local)
            {
                return true;
            }
            if (LocalIndex(ins.Op, ins.Operand, load: false) == local || ins.Labels.Length > 0 || ins.Blocks.Length > 0 || Unconditional(ins.Op)
                || ins.Op.FlowControl == FlowControl.Cond_Branch)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// The identity bridge: <see cref="GatesOn"/>'s gate on the listener's parameter <paramref name="listener"/>, whose no-op exit is
    /// exactly <c>ldarg identity; ret</c> (it gives the stored value back as it is), returning <paramref name="type"/> (the local's
    /// type, the parameter's too). Its class without a static constructor and every class it reaches initializer-free
    /// (<see cref="PatchSafety.InitializersFree"/>, the TypeGated rule), not patched itself. Null otherwise.
    /// </summary>
    internal static Type[]? IdentityGatesOn(MethodInfo method, int listener, int identity, Type? type)
    {
        try
        {
            if (type == null || identity == listener || method.ReturnType != type || identity < 0 || identity >= method.GetParameters().Length
                || method.GetParameters()[identity].ParameterType != type || Patched(method) || !PatchSafety.InitializersFree(method))
            {
                return null;
            }
            return GatesOn(method, listener, identity);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Why a call inserted by another mod can't be accepted at all, or null.</summary>
    private static string? CalleeRefusal(MethodInfo callee)
    {
        if (!callee.IsStatic)
        {
            return $"{Describe(callee)}: an instance method called";
        }
        if (callee.ContainsGenericParameters)
        {
            return $"{Describe(callee)}: an open generic method";
        }
        if (callee.GetParameters().Any(p => p.ParameterType.IsByRef || p.IsOut))
        {
            return $"{Describe(callee)} takes a parameter by reference";
        }
        Assembly assembly = callee.Module.Assembly;
        if (assembly == typeof(AbstractModel).Assembly)
        {
            return $"{Describe(callee)}: a call into the game's code";
        }
        // This mod's own code (the test tooling's shapes aside: they stand for another mod).
        if ((assembly == typeof(TranspilerJudge).Assembly || assembly.GetName().Name?.StartsWith(ModEntry.Id, StringComparison.Ordinal) == true)
            && callee.DeclaringType?.Namespace?.StartsWith(ModEntry.Id + ".Bench", StringComparison.Ordinal) != true)
        {
            return $"{Describe(callee)}: a call into this mod's code";
        }
        return null;
    }

    private static string Describe(MethodInfo method) => $"{method.DeclaringType?.Name}.{method.Name}";

    private static bool IsConstant(OpCode op) =>
        op == OpCodes.Ldnull || op == OpCodes.Ldstr || op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S || op == OpCodes.Ldc_I4_M1
        || op == OpCodes.Ldc_I4_0 || op == OpCodes.Ldc_I4_1 || op == OpCodes.Ldc_I4_2 || op == OpCodes.Ldc_I4_3 || op == OpCodes.Ldc_I4_4
        || op == OpCodes.Ldc_I4_5 || op == OpCodes.Ldc_I4_6 || op == OpCodes.Ldc_I4_7 || op == OpCodes.Ldc_I4_8 || op == OpCodes.Ldc_I8
        || op == OpCodes.Ldc_R4 || op == OpCodes.Ldc_R8;

    /// <summary>The argument an <c>ldarg</c> (by value) loads, else null.</summary>
    private static int? ArgumentIndex(OpCode op, object? operand) =>
        op == OpCodes.Ldarg_0 ? 0 : op == OpCodes.Ldarg_1 ? 1 : op == OpCodes.Ldarg_2 ? 2 : op == OpCodes.Ldarg_3 ? 3
        : (op == OpCodes.Ldarg_S || op == OpCodes.Ldarg) ? OperandIndex(operand) : null;

    /// <summary>The local an <c>ldloc</c> (load) or <c>stloc</c> (store) names, else null.</summary>
    private static int? LocalIndex(OpCode op, object? operand, bool load) => load
        ? (op == OpCodes.Ldloc_0 ? 0 : op == OpCodes.Ldloc_1 ? 1 : op == OpCodes.Ldloc_2 ? 2 : op == OpCodes.Ldloc_3 ? 3
            : (op == OpCodes.Ldloc_S || op == OpCodes.Ldloc) ? OperandIndex(operand) : null)
        : (op == OpCodes.Stloc_0 ? 0 : op == OpCodes.Stloc_1 ? 1 : op == OpCodes.Stloc_2 ? 2 : op == OpCodes.Stloc_3 ? 3
            : (op == OpCodes.Stloc_S || op == OpCodes.Stloc) ? OperandIndex(operand) : null);

    private static int? OperandIndex(object? operand) => operand switch
    {
        LocalVariableInfo local => local.LocalIndex,
        ParameterInfo parameter => parameter.Position,
        null => null,
        _ when IsIntegral(operand) => (int)Convert.ToInt64(operand),
        _ => null,
    };

    /// <summary>
    /// The types a static method called with the listener as parameter <paramref name="position"/> is gated on: from its first
    /// instruction (nops aside) only tests <c>ldarg p; isinst T; [stloc n; ldloc n;] brfalse|brtrue</c> along the path a listener of
    /// none of those types takes (isinst gives null), unconditional branches, then its no-op exit: <c>ret</c> (void), or one load
    /// of a constant or an argument and <c>ret</c>. Nothing else on that path (no call, no store, no field). Null otherwise, or
    /// when its class has a static constructor (the first call would run it at another time), or the parameter isn't the
    /// listener's reference.
    /// </summary>
    internal static Type[]? GatesOn(MethodInfo method, int position) => GatesOn(method, position, -1);

    /// <summary>
    /// <see cref="GatesOn(MethodInfo, int)"/>; with <paramref name="identity"/> (an argument's index, the identity bridge), the only
    /// no-op exit accepted is <c>ldarg identity; ret</c>.
    /// </summary>
    private static Type[]? GatesOn(MethodInfo method, int position, int identity)
    {
        try
        {
            if (!method.IsStatic || method.ContainsGenericParameters || method.DeclaringType == null || method.DeclaringType.TypeInitializer != null
                || method.GetMethodBody() == null)
            {
                return null;
            }
            ParameterInfo[] parameters = method.GetParameters();
            if (position < 0 || position >= parameters.Length)
            {
                return null;
            }
            Type type = parameters[position].ParameterType;
            if (type.IsByRef || type.IsValueType || !type.IsAssignableFrom(typeof(AbstractModel)))
            {
                return null;
            }
            bool returns = method.ReturnType != typeof(void);
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(method);
            var at = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++)
            {
                at[code[i].Offset] = i;
            }
            var gates = new List<Type>();
            var visited = new HashSet<int>();
            int index = 0;
            while (index >= 0 && index < code.Count && visited.Add(index))
            {
                MethodFingerprint.Instruction ins = code[index];
                if (ins.Op == OpCodes.Nop)
                {
                    index++;
                    continue;
                }
                if (ins.Op == OpCodes.Br || ins.Op == OpCodes.Br_S)
                {
                    index = ins.Operand is int target && at.TryGetValue(target, out int next) ? next : -1;
                    continue;
                }
                if (ins.Op == OpCodes.Ret)
                {
                    return !returns && identity < 0 && gates.Count > 0 ? gates.ToArray() : null;
                }
                if (returns && index + 1 < code.Count && code[index + 1].Op == OpCodes.Ret
                    && (identity >= 0 ? ArgumentIndex(ins.Op, ins.Operand) == identity : IsConstant(ins.Op) || ArgumentIndex(ins.Op, ins.Operand) != null))
                {
                    return gates.Count > 0 ? gates.ToArray() : null;
                }
                if (!PatchSafety.LoadsArgument(ins, position) || index + 2 >= code.Count || code[index + 1].Op != OpCodes.Isinst
                    || code[index + 1].Operand is not Type gate || gate.IsValueType || gate.ContainsGenericParameters)
                {
                    return null;
                }
                int branch = index + 2;
                if (PatchSafety.StoresLocal(code[branch], out int local) && branch + 1 < code.Count && PatchSafety.LoadsLocal(code[branch + 1], local))
                {
                    branch += 2;
                }
                if (branch >= code.Count)
                {
                    return null;
                }
                MethodFingerprint.Instruction test = code[branch];
                bool onFalse = test.Op == OpCodes.Brfalse || test.Op == OpCodes.Brfalse_S;
                bool onTrue = test.Op == OpCodes.Brtrue || test.Op == OpCodes.Brtrue_S;
                if (!onFalse && !onTrue || test.Operand is not int to || !at.TryGetValue(to, out int taken))
                {
                    return null;
                }
                gates.Add(gate);
                index = onFalse ? taken : branch + 1;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ---- Abstract interpretation of the game's code ----

    private enum Kind : byte
    {
        Unknown,
        Const,
        This,
        /// <summary>The listener walk's result (N: the walk's loop), possibly.</summary>
        Walk,
        /// <summary>Its enumerator, possibly.</summary>
        Enum,
        /// <summary>The current listener of loop N, on every path.</summary>
        Current,
        /// <summary>The result of loop N's MoveNext.</summary>
        Next,
        /// <summary>Walk, enumerator or MoveNext result mixed with something else: refused on use.</summary>
        Poison,
    }

    private readonly record struct Value(Kind Kind, int N)
    {
        public static readonly Value Unknown = new(Kind.Unknown, 0);

        public bool Tracked => Kind is Kind.Walk or Kind.Enum or Kind.Next or Kind.Poison;

        public static Value Join(Value a, Value b)
        {
            if (a == b)
            {
                return a;
            }
            // A walk or enumerator on one path, a plain value on the other (combatState?.Walk() ?? Array.Empty()): the walk's,
            // possibly (its loop judged as the walk's loop: conservative).
            if (a.Kind is Kind.Walk or Kind.Enum && b.Kind is Kind.Unknown or Kind.Const)
            {
                return a;
            }
            if (b.Kind is Kind.Walk or Kind.Enum && a.Kind is Kind.Unknown or Kind.Const)
            {
                return b;
            }
            return a.Tracked || b.Tracked ? new Value(Kind.Poison, 0) : Unknown;
        }
    }

    private enum Phase : byte { Out, In, Mixed }

    private sealed class State
    {
        public List<Value> Stack;
        public Value[] Locals;
        public Dictionary<(Module, int), Value> Fields;
        public Phase[] Phases;

        public State(List<Value> stack, Value[] locals, Dictionary<(Module, int), Value> fields, Phase[] phases)
        {
            Stack = stack;
            Locals = locals;
            Fields = fields;
            Phases = phases;
        }

        public State Clone() => new(new List<Value>(Stack), (Value[])Locals.Clone(), new Dictionary<(Module, int), Value>(Fields), (Phase[])Phases.Clone());

        public Value Field(FieldInfo field) => Fields.TryGetValue((field.Module, field.MetadataToken), out Value v) ? v : Value.Unknown;

        public bool AllOut => Phases.All(p => p == Phase.Out);

        public bool OnlyIn(int loop) => loop >= 0 && loop < Phases.Length && Enumerable.Range(0, Phases.Length).All(k => Phases[k] == (k == loop ? Phase.In : Phase.Out));

        /// <summary>Joins <paramref name="other"/> into this one; true when this one changed. Throws on stacks of different heights.</summary>
        public bool JoinFrom(State other)
        {
            if (other.Stack.Count != Stack.Count)
            {
                throw new JudgeException("stack heights differ where paths meet");
            }
            bool changed = false;
            for (int i = 0; i < Stack.Count; i++)
            {
                Value v = Value.Join(Stack[i], other.Stack[i]);
                changed |= v != Stack[i];
                Stack[i] = v;
            }
            for (int i = 0; i < Locals.Length; i++)
            {
                Value v = Value.Join(Locals[i], other.Locals[i]);
                changed |= v != Locals[i];
                Locals[i] = v;
            }
            // A field missing on one side is unknown there.
            foreach ((Module, int) key in Fields.Keys.Union(other.Fields.Keys).ToList())
            {
                Value mine = Fields.TryGetValue(key, out Value a) ? a : Value.Unknown;
                Value theirs = other.Fields.TryGetValue(key, out Value b) ? b : Value.Unknown;
                Value v = Value.Join(mine, theirs);
                changed |= v != mine;
                if (v != Value.Unknown || Fields.ContainsKey(key))
                {
                    Fields[key] = v;
                }
            }
            for (int i = 0; i < Phases.Length; i++)
            {
                Phase p = Phases[i] == other.Phases[i] ? Phases[i] : Phase.Mixed;
                changed |= p != Phases[i];
                Phases[i] = p;
            }
            return changed;
        }

        public void Forget(int loop)
        {
            for (int i = 0; i < Stack.Count; i++)
            {
                if (Stack[i].Kind == Kind.Current && Stack[i].N == loop)
                {
                    Stack[i] = Value.Unknown;
                }
            }
            for (int i = 0; i < Locals.Length; i++)
            {
                if (Locals[i].Kind == Kind.Current && Locals[i].N == loop)
                {
                    Locals[i] = Value.Unknown;
                }
            }
            foreach ((Module, int) key in Fields.Keys.ToList())
            {
                if (Fields[key].Kind == Kind.Current && Fields[key].N == loop)
                {
                    Fields[key] = Value.Unknown;
                }
            }
        }
    }

    private sealed class JudgeException(string message) : Exception(message);

    /// <summary>
    /// Where the listener walks' iterations are in the game's code, and what holds the current listener: a forward analysis per
    /// entry context (a plain method: one; an async state machine's MoveNext: the first call, then one per state a suspension
    /// leaves, with the fields and iterations it leaves), to a fixpoint. Refuses (a reason) whatever it doesn't follow exactly:
    /// the walk or its enumerator used other than by GetEnumerator/MoveNext/Current/Dispose or a null test, a MoveNext result
    /// used other than by a branch, a finally that writes, an exit whose state isn't a constant, stacks that don't meet.
    /// </summary>
    private sealed class Analysis
    {
        private readonly MethodBase _body;
        private readonly IReadOnlyList<Ins> _code;
        private readonly Dictionary<int, int> _labels = new();
        private readonly int[] _loopOf;
        private readonly int _loops;
        private readonly int _localCount;
        private readonly bool _instance;
        private readonly FieldInfo? _stateField;
        private readonly HashSet<int> _writtenArguments = new();
        /// <summary>Per instruction, the handlers whose protected region holds it: (handler start, catch or not).</summary>
        private readonly List<(int Start, bool Catch)>[] _handlers;
        private readonly bool[] _inFinally;
        private readonly List<State?[]> _results = new();
        private State?[]? _joined;

        public Analysis(MethodBase body, IReadOnlyList<Ins> code, Func<MethodInfo, bool> isWalk)
        {
            _body = body;
            _code = code;
            _instance = !body.IsStatic;
            _localCount = body.GetMethodBody()?.LocalVariables.Count ?? 0;
            _loopOf = new int[code.Count];
            _handlers = new List<(int, bool)>[code.Count];
            _inFinally = new bool[code.Count];
            for (int i = 0; i < code.Count; i++)
            {
                _loopOf[i] = -1;
                _handlers[i] = new List<(int, bool)>();
                foreach (int label in code[i].Labels)
                {
                    _labels[label] = i;
                }
                if ((code[i].Op == OpCodes.Call || code[i].Op == OpCodes.Callvirt) && code[i].Operand is MethodInfo called && isWalk(called))
                {
                    _loopOf[i] = _loops++;
                }
                if ((code[i].Op == OpCodes.Starg || code[i].Op == OpCodes.Starg_S || code[i].Op == OpCodes.Ldarga || code[i].Op == OpCodes.Ldarga_S)
                    && OperandIndex(code[i].Operand) is int written)
                {
                    _writtenArguments.Add(written);
                }
            }
            if (_instance && typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(body.DeclaringType))
            {
                _stateField = body.DeclaringType!.GetField("<>1__state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
        }

        public bool ArgumentNeverWritten(int argument) =>
            _body.IsStatic && argument < _body.GetParameters().Length && !_writtenArguments.Contains(argument);

        /// <summary>The state where code inserted before instruction <paramref name="index"/> runs (every context joined), or null.</summary>
        public State? Gap(int index) => _joined![index];

        /// <summary>Runs the analysis; null when done, else why it can't be followed.</summary>
        public string? Run()
        {
            try
            {
                if (_loops == 0)
                {
                    return "no listener walk found";
                }
                if (_instance && _stateField == null)
                {
                    return "an instance method that isn't an async state machine";
                }
                ParseRegions();
                var entries = new Dictionary<int, State>();
                State first = Initial();
                if (_stateField != null)
                {
                    first.Fields[(_stateField.Module, _stateField.MetadataToken)] = new Value(Kind.Const, -1);
                }
                entries[_stateField != null ? -1 : 0] = first;
                for (int round = 0; ; round++)
                {
                    if (round > 12)
                    {
                        return "too many resume states";
                    }
                    _results.Clear();
                    var exits = new Dictionary<int, State>();
                    foreach (State entry in entries.Values)
                    {
                        _results.Add(Interpret(entry, exits));
                    }
                    bool changed = false;
                    foreach ((int stateValue, State exit) in exits)
                    {
                        State resume = exit.Clone();
                        resume.Stack.Clear();
                        resume.Locals = new Value[_localCount];
                        if (entries.TryGetValue(stateValue, out State? known))
                        {
                            changed |= known.JoinFrom(resume);
                        }
                        else
                        {
                            entries[stateValue] = resume;
                            changed = true;
                        }
                    }
                    if (!changed)
                    {
                        break;
                    }
                }
                _joined = new State?[_code.Count];
                foreach (State?[] states in _results)
                {
                    for (int i = 0; i < _code.Count; i++)
                    {
                        if (states[i] is { } s)
                        {
                            if (_joined[i] == null)
                            {
                                _joined[i] = s.Clone();
                            }
                            else
                            {
                                _joined[i]!.JoinFrom(s);
                            }
                        }
                    }
                }
                return null;
            }
            catch (JudgeException e)
            {
                return e.Message;
            }
        }

        private State Initial() => new(new List<Value>(), new Value[_localCount], new Dictionary<(Module, int), Value>(), new Phase[_loops]);

        /// <summary>Try regions and their handlers from Harmony's exception block marks (filters refused).</summary>
        private void ParseRegions()
        {
            var open = new Stack<(int TryStart, List<(int Start, ExceptionBlockType Kind)> Handlers)>();
            for (int i = 0; i < _code.Count; i++)
            {
                foreach (Block block in _code[i].Blocks)
                {
                    switch (block.Kind)
                    {
                        case ExceptionBlockType.BeginExceptionBlock:
                            open.Push((i, new List<(int, ExceptionBlockType)>()));
                            break;
                        case ExceptionBlockType.BeginCatchBlock:
                        case ExceptionBlockType.BeginFinallyBlock:
                        case ExceptionBlockType.BeginFaultBlock:
                            if (open.Count == 0)
                            {
                                throw new JudgeException("a handler without its try");
                            }
                            open.Peek().Handlers.Add((i, block.Kind));
                            break;
                        case ExceptionBlockType.BeginExceptFilterBlock:
                            throw new JudgeException("an exception filter");
                    }
                }
                foreach (Block block in _code[i].Blocks)
                {
                    if (block.Kind != ExceptionBlockType.EndExceptionBlock)
                    {
                        continue;
                    }
                    if (open.Count == 0)
                    {
                        throw new JudgeException("an exception block end without its start");
                    }
                    (int tryStart, List<(int Start, ExceptionBlockType Kind)> handlers) = open.Pop();
                    if (handlers.Count == 0)
                    {
                        throw new JudgeException("a try without handler");
                    }
                    int tryEnd = handlers[0].Start;
                    for (int h = 0; h < handlers.Count; h++)
                    {
                        int end = h + 1 < handlers.Count ? handlers[h + 1].Start : i + 1;
                        bool isCatch = handlers[h].Kind == ExceptionBlockType.BeginCatchBlock;
                        // Code a protected region (the try, and for an outer handler its inner handlers too) may throw into.
                        for (int k = tryStart; k < tryEnd; k++)
                        {
                            _handlers[k].Add((handlers[h].Start, isCatch));
                        }
                        if (!isCatch)
                        {
                            for (int k = handlers[h].Start; k < end; k++)
                            {
                                _inFinally[k] = true;
                                if (Writes(_code[k]))
                                {
                                    throw new JudgeException("a finally or fault handler that writes");
                                }
                            }
                        }
                    }
                }
            }
            if (open.Count > 0)
            {
                throw new JudgeException("an exception block not closed");
            }
        }

        internal static bool Writes(Ins ins)
        {
            OpCode op = ins.Op;
            return LocalIndex(op, ins.Operand, load: false) != null || op == OpCodes.Starg || op == OpCodes.Starg_S || op == OpCodes.Stfld
                || op == OpCodes.Stsfld || op == OpCodes.Stobj || op == OpCodes.Initobj || op == OpCodes.Cpobj || op == OpCodes.Cpblk
                || op == OpCodes.Initblk || (op.Name ?? "").StartsWith("stind", StringComparison.Ordinal) || (op.Name ?? "").StartsWith("stelem", StringComparison.Ordinal)
                || op == OpCodes.Ldloca || op == OpCodes.Ldloca_S || op == OpCodes.Ldarga || op == OpCodes.Ldarga_S || op == OpCodes.Ldflda
                || op == OpCodes.Ldsflda;
        }

        private State?[] Interpret(State entry, Dictionary<int, State> exits)
        {
            var states = new State?[_code.Count];
            var work = new Queue<int>();
            var queued = new bool[_code.Count];
            int steps = 0;

            void Flow(int target, State state)
            {
                if (target < 0 || target >= _code.Count)
                {
                    throw new JudgeException("a jump outside the method");
                }
                if (_code[target].Op == OpCodes.Ret)
                {
                    RecordExit(state, exits);
                }
                if (states[target] == null)
                {
                    states[target] = state.Clone();
                }
                else if (!states[target]!.JoinFrom(state))
                {
                    return;
                }
                if (!queued[target])
                {
                    queued[target] = true;
                    work.Enqueue(target);
                }
            }

            Flow(0, entry);
            while (work.Count > 0)
            {
                if (++steps > 200_000)
                {
                    throw new JudgeException("no fixpoint");
                }
                int i = work.Dequeue();
                queued[i] = false;
                State before = states[i]!;
                State s = before.Clone();
                foreach ((int start, bool isCatch) in _handlers[i])
                {
                    State into = before.Clone();
                    into.Stack = isCatch ? new List<Value> { Value.Unknown } : new List<Value>();
                    Flow(start, into);
                }
                Step(i, s, Flow);
                // The state after the instruction too (a call's effects before it throws): joined into the handlers.
                foreach ((int start, bool isCatch) in _handlers[i])
                {
                    State into = s.Clone();
                    into.Stack = isCatch ? new List<Value> { Value.Unknown } : new List<Value>();
                    Flow(start, into);
                }
            }
            return states;
        }

        private void RecordExit(State state, Dictionary<int, State> exits)
        {
            if (_stateField == null)
            {
                return;
            }
            Value stateValue = state.Field(_stateField);
            if (stateValue.Kind != Kind.Const)
            {
                throw new JudgeException("the state machine's state not known at an exit");
            }
            if (stateValue.N < 0)
            {
                return; // Finished (or failed): no resume.
            }
            if (exits.TryGetValue(stateValue.N, out State? known))
            {
                known.JoinFrom(WithoutStack(state));
            }
            else
            {
                exits[stateValue.N] = WithoutStack(state);
            }
        }

        private static State WithoutStack(State state)
        {
            State copy = state.Clone();
            copy.Stack.Clear();
            return copy;
        }

        private int Target(object? operand) =>
            operand is int label && _labels.TryGetValue(label, out int index) ? index : throw new JudgeException("a branch to an unknown label");

        private static Value Pop(State s)
        {
            if (s.Stack.Count == 0)
            {
                throw new JudgeException("stack underflow");
            }
            Value v = s.Stack[^1];
            s.Stack.RemoveAt(s.Stack.Count - 1);
            return v;
        }

        private static void Plain(Value v, string what)
        {
            if (v.Tracked)
            {
                throw new JudgeException($"the listener walk (or its enumerator) used by {what}");
            }
        }

        private void Step(int i, State s, Action<int, State> flow)
        {
            Ins ins = _code[i];
            OpCode op = ins.Op;
            int next = i + 1;
            // Loads and stores the analysis follows.
            if (ArgumentIndex(op, ins.Operand) is int arg)
            {
                s.Stack.Add(_instance && arg == 0 ? new Value(Kind.This, 0) : Value.Unknown);
                flow(next, s);
                return;
            }
            if (LocalIndex(op, ins.Operand, load: true) is int load)
            {
                s.Stack.Add(load < s.Locals.Length ? s.Locals[load] : Value.Unknown);
                flow(next, s);
                return;
            }
            if (LocalIndex(op, ins.Operand, load: false) is int store)
            {
                Value v = Pop(s);
                if (store < s.Locals.Length)
                {
                    s.Locals[store] = v;
                }
                flow(next, s);
                return;
            }
            if (op == OpCodes.Ldloca || op == OpCodes.Ldloca_S)
            {
                int local = OperandIndex(ins.Operand) ?? throw new JudgeException("ldloca without a local");
                if (local < s.Locals.Length)
                {
                    Plain(s.Locals[local], "its address");
                    s.Locals[local] = Value.Unknown;
                }
                s.Stack.Add(Value.Unknown);
                flow(next, s);
                return;
            }
            if (op == OpCodes.Ldarga || op == OpCodes.Ldarga_S)
            {
                if (_instance && OperandIndex(ins.Operand) == 0)
                {
                    Clobber(s);
                }
                s.Stack.Add(Value.Unknown);
                flow(next, s);
                return;
            }
            if (op == OpCodes.Ldfld || op == OpCodes.Ldflda)
            {
                Value obj = Pop(s);
                var field = ins.Operand as FieldInfo ?? throw new JudgeException("a field that doesn't resolve");
                if (obj.Kind == Kind.This)
                {
                    (Module, int) key = (field.Module, field.MetadataToken);
                    Value held = s.Fields.TryGetValue(key, out Value v) ? v : Value.Unknown;
                    if (op == OpCodes.Ldflda)
                    {
                        Plain(held, "its field's address");
                        s.Fields[key] = Value.Unknown;
                        s.Stack.Add(Value.Unknown);
                    }
                    else
                    {
                        s.Stack.Add(held);
                    }
                }
                else
                {
                    Plain(obj, "a field read");
                    s.Stack.Add(Value.Unknown);
                }
                flow(next, s);
                return;
            }
            if (op == OpCodes.Stfld)
            {
                Value value = Pop(s);
                Value obj = Pop(s);
                var field = ins.Operand as FieldInfo ?? throw new JudgeException("a field that doesn't resolve");
                if (obj.Kind == Kind.This)
                {
                    s.Fields[(field.Module, field.MetadataToken)] = value;
                }
                else
                {
                    Plain(obj, "a field write");
                    Plain(value, "a field write");
                }
                flow(next, s);
                return;
            }
            if (op == OpCodes.Dup)
            {
                if (s.Stack.Count == 0)
                {
                    throw new JudgeException("stack underflow");
                }
                s.Stack.Add(s.Stack[^1]);
                flow(next, s);
                return;
            }
            if (op == OpCodes.Pop)
            {
                Pop(s);
                flow(next, s);
                return;
            }
            if (ConstantOf(op, ins.Operand) is int constant)
            {
                s.Stack.Add(new Value(Kind.Const, constant));
                flow(next, s);
                return;
            }
            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
            {
                Call(i, ins, s);
                flow(next, s);
                return;
            }
            if (op == OpCodes.Calli || op == OpCodes.Jmp)
            {
                throw new JudgeException(op.Name ?? "calli");
            }
            // Control flow.
            if (op == OpCodes.Br || op == OpCodes.Br_S)
            {
                flow(Target(ins.Operand), s);
                return;
            }
            if (op == OpCodes.Leave || op == OpCodes.Leave_S)
            {
                // The finally handlers it runs on the way write nothing (ParseRegions): straight to the target.
                s.Stack.Clear();
                flow(Target(ins.Operand), s);
                return;
            }
            if (op == OpCodes.Brtrue || op == OpCodes.Brtrue_S || op == OpCodes.Brfalse || op == OpCodes.Brfalse_S)
            {
                Value v = Pop(s);
                bool jumpsOnTrue = op == OpCodes.Brtrue || op == OpCodes.Brtrue_S;
                if (v.Kind == Kind.Poison)
                {
                    throw new JudgeException("a branch on a mixed walk value");
                }
                if (v.Kind == Kind.Const)
                {
                    flow((v.N != 0) == jumpsOnTrue ? Target(ins.Operand) : next, s);
                    return;
                }
                State taken = s.Clone();
                if (v.Kind == Kind.Next)
                {
                    // MoveNext true: an iteration of that loop; false: the loop is over.
                    taken.Phases[v.N] = jumpsOnTrue ? Phase.In : Phase.Out;
                    s.Phases[v.N] = jumpsOnTrue ? Phase.Out : Phase.In;
                }
                flow(Target(ins.Operand), taken);
                flow(next, s);
                return;
            }
            if (op.FlowControl == FlowControl.Cond_Branch && op != OpCodes.Switch)
            {
                Value b = Pop(s);
                Value a = Pop(s);
                Plain(a, "a comparison");
                Plain(b, "a comparison");
                if (a.Kind == Kind.Const && b.Kind == Kind.Const && Compare(op, a.N, b.N) is bool decided)
                {
                    flow(decided ? Target(ins.Operand) : next, s);
                    return;
                }
                flow(Target(ins.Operand), s.Clone());
                flow(next, s);
                return;
            }
            if (op == OpCodes.Switch)
            {
                Value v = Pop(s);
                Plain(v, "a switch");
                int[] targets = ins.Operand as int[] ?? throw new JudgeException("a switch without targets");
                if (v.Kind == Kind.Const)
                {
                    flow(v.N >= 0 && v.N < targets.Length ? Target(targets[v.N]) : next, s);
                    return;
                }
                foreach (int t in targets)
                {
                    flow(Target(t), s.Clone());
                }
                flow(next, s);
                return;
            }
            if (op == OpCodes.Ret || op == OpCodes.Endfinally || op == OpCodes.Endfilter || op == OpCodes.Throw || op == OpCodes.Rethrow)
            {
                if (op == OpCodes.Throw)
                {
                    Plain(Pop(s), "a throw");
                }
                return; // Exits: recorded on the way in (Flow); endfinally: the leave went to its target already.
            }
            if (op == OpCodes.Ceq || op == OpCodes.Cgt || op == OpCodes.Cgt_Un || op == OpCodes.Clt || op == OpCodes.Clt_Un)
            {
                Value b = Pop(s);
                Value a = Pop(s);
                Plain(a, "a comparison");
                Plain(b, "a comparison");
                s.Stack.Add(a.Kind == Kind.Const && b.Kind == Kind.Const
                    ? new Value(Kind.Const, (op == OpCodes.Ceq ? a.N == b.N : op == OpCodes.Cgt ? a.N > b.N : op == OpCodes.Clt ? a.N < b.N
                        : op == OpCodes.Cgt_Un ? (uint)a.N > (uint)b.N : (uint)a.N < (uint)b.N) ? 1 : 0)
                    : Value.Unknown);
                flow(next, s);
                return;
            }
            // Anything else: its stack effect, values unknown; the walk values never consumed here.
            int pops = PopCount(op);
            for (int k = 0; k < pops; k++)
            {
                Plain(Pop(s), op.Name ?? "an instruction");
            }
            int pushes = PushCount(op);
            for (int k = 0; k < pushes; k++)
            {
                s.Stack.Add(Value.Unknown);
            }
            if (op.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw)
            {
                throw new JudgeException($"{op.Name} not followed");
            }
            flow(next, s);
        }

        private void Call(int i, Ins ins, State s)
        {
            var method = ins.Operand as MethodBase ?? throw new JudgeException("a call that doesn't resolve");
            ParameterInfo[] parameters = method.GetParameters();
            bool hasThis = !method.IsStatic && ins.Op != OpCodes.Newobj;
            var args = new Value[parameters.Length + (hasThis ? 1 : 0)];
            for (int k = args.Length - 1; k >= 0; k--)
            {
                args[k] = Pop(s);
            }
            bool returns = ins.Op == OpCodes.Newobj || (method is MethodInfo mi && mi.ReturnType != typeof(void));
            if (_loopOf[i] >= 0)
            {
                foreach (Value a in args)
                {
                    Plain(a, "the walk's own call");
                }
                s.Stack.Add(new Value(Kind.Walk, _loopOf[i]));
                return;
            }
            if (hasThis && parameters.Length == 0 && args[0].Kind is Kind.Walk && method.Name == "GetEnumerator")
            {
                s.Stack.Add(new Value(Kind.Enum, args[0].N));
                return;
            }
            if (hasThis && parameters.Length == 0 && args[0].Kind is Kind.Enum)
            {
                int loop = args[0].N;
                switch (method.Name)
                {
                    case "MoveNext":
                        s.Forget(loop);
                        s.Stack.Add(new Value(Kind.Next, loop));
                        return;
                    case "get_Current":
                        s.Stack.Add(new Value(Kind.Current, loop));
                        return;
                    case "Dispose":
                        return;
                }
            }
            bool thisPassed = false;
            foreach (Value a in args)
            {
                Plain(a, method.Name);
                thisPassed |= a.Kind == Kind.This;
            }
            // The state machine passed on (AwaitUnsafeOnCompleted's ref this): only the async method builders, which don't change
            // the fields followed here; anything else may write any of them.
            if (thisPassed && !(method.DeclaringType?.Namespace == "System.Runtime.CompilerServices" && method.DeclaringType.Name.Contains("MethodBuilder")))
            {
                Clobber(s);
            }
            if (returns)
            {
                s.Stack.Add(Value.Unknown);
            }
        }

        private static void Clobber(State s)
        {
            foreach ((Module, int) key in s.Fields.Keys.ToList())
            {
                Plain(s.Fields[key], "code that may write the state machine's fields");
                s.Fields[key] = Value.Unknown;
            }
        }

        private static int? ConstantOf(OpCode op, object? operand) =>
            op == OpCodes.Ldc_I4_M1 ? -1 : op == OpCodes.Ldc_I4_0 ? 0 : op == OpCodes.Ldc_I4_1 ? 1 : op == OpCodes.Ldc_I4_2 ? 2
            : op == OpCodes.Ldc_I4_3 ? 3 : op == OpCodes.Ldc_I4_4 ? 4 : op == OpCodes.Ldc_I4_5 ? 5 : op == OpCodes.Ldc_I4_6 ? 6
            : op == OpCodes.Ldc_I4_7 ? 7 : op == OpCodes.Ldc_I4_8 ? 8
            : (op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S) && operand != null && IsIntegral(operand) ? (int)Convert.ToInt64(operand) : null;

        private static bool? Compare(OpCode op, int a, int b)
        {
            if (op == OpCodes.Beq || op == OpCodes.Beq_S) return a == b;
            if (op == OpCodes.Bne_Un || op == OpCodes.Bne_Un_S) return a != b;
            if (op == OpCodes.Bge || op == OpCodes.Bge_S) return a >= b;
            if (op == OpCodes.Bgt || op == OpCodes.Bgt_S) return a > b;
            if (op == OpCodes.Ble || op == OpCodes.Ble_S) return a <= b;
            if (op == OpCodes.Blt || op == OpCodes.Blt_S) return a < b;
            if (op == OpCodes.Bge_Un || op == OpCodes.Bge_Un_S) return (uint)a >= (uint)b;
            if (op == OpCodes.Bgt_Un || op == OpCodes.Bgt_Un_S) return (uint)a > (uint)b;
            if (op == OpCodes.Ble_Un || op == OpCodes.Ble_Un_S) return (uint)a <= (uint)b;
            if (op == OpCodes.Blt_Un || op == OpCodes.Blt_Un_S) return (uint)a < (uint)b;
            return null;
        }

        private static int PopCount(OpCode op) => op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
                or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
                or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref or StackBehaviour.Popref_popi_pop1 => 3,
            _ => throw new JudgeException($"{op.Name}: stack effect unknown"),
        };

        private static int PushCount(OpCode op) => op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8
                or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            _ => throw new JudgeException($"{op.Name}: stack effect unknown"),
        };
    }
}
