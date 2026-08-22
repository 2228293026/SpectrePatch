using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using StackFrame = System.Diagnostics.StackFrame;
using StackTrace = System.Diagnostics.StackTrace;

namespace SpectrePatch;

// SpectrePatch 应用器：扫描程序集 → 解析目标（候选链 + 版本门控）→ 按补丁隔离应用/卸载。
// 引擎就是原生 Harmony（无任何内部手术）：不引入 Replace/Override 等需要改写
// Harmony 内部的扩展类型，TryingCatch 用原生 finalizer 实现。
//
// 性能设计（与裸 Harmony 补丁对齐）：
//   - 目标解析/反射只在 Initialize 时做一次，运行期零反射；
//   - 每补丁一个 Harmony 实例——实例只是 owner ID 字符串，无运行时成本，
//     换来的是按补丁精确卸载（Unpatch 只动自己，不误伤同目标的旧路径补丁）；
//   - 共享 finalizer 是同一个 MethodInfo：无异常时 Harmony 根本不调用 finalizer
//     （try/catch 块空开销），常驻补丁链不多任何方法；
//   - 探针和应用走同一个解析入口，不存在"探针说好、应用时挂"的分裂。
//
// 刷新节拍：PatchManager.RefreshPatches / ApplyAll 尾部调 Refresh()，
// 与旧路径补丁同拍响应 Options 开关（Options.cs 无需改动）。
internal static class SpectrePatchHub
{
    private sealed class Spec
    {
        public string Id;           // "SpectrePatch:类.方法#序号"，同时是 Harmony owner id
        public string Feature;      // null = 常开
        public MethodInfo PatchMethod;
        public SpectrePatchAttribute Attr;
        public MethodBase Target;   // null = 解析失败
        public string TargetName;   // 探针展示：命中候选的最终目标
        public string Error;        // 解析/应用失败原因；null = 正常
        public Harmony Owner;
        public bool Applied;
    }

    private static readonly List<Spec> _specs = new();
    private static readonly Dictionary<string, Func<bool>> _featureToggles = new();

    // TryingCatch 补丁方法集合：Guard 比对异常栈帧，只吞我们自己补丁体抛出的异常。
    // 只增不删（卸载后的残留条目无害，重新 Initialize 时整体清空）
    private static readonly HashSet<MethodBase> _guardedMethods = new();
    private static readonly HarmonyMethod _guardMethod
        = new(AccessTools.Method(typeof(SpectrePatchHub), nameof(Guard)));
    private static readonly HashSet<string> _guardLogged = new();
    // 未注册开关的功能名：只告警一次（ToggleOn 对未注册功能返回 false，补丁不应用）
    private static readonly HashSet<string> _warnedUnregistered = new();
    private static readonly object _lock = new();

    // —— 宿主上下文（源码链接共享库）——————————————————————
    // 本文件被各宿主 mod 以 <Compile Include> 编进各自程序集，静态状态随宿主隔离；
    // 游戏程序集与版本政策由宿主启动时注入（先于 Initialize 调用 Configure）
    internal static System.Reflection.Assembly GameAssembly;
    // 宿主实测游戏 build；-1 = 未知（版本门控放行，只降级为日志警告）
    internal static int Release = -1;
    // 宿主政策：支持的最低游戏 build——所有补丁的隐式 MinVersion 地板
    internal static int MinSupportedBuild;
    // 宿主已验证的游戏 build
    internal static int VerifiedBuild;
    internal static bool Verified => Release == VerifiedBuild;
    // 日志前缀（宿主可改成自己的 mod 名）
    internal static string LogTag = "SpectrePatch";

    internal static void Configure(System.Reflection.Assembly gameAssembly, int release,
        int minSupportedBuild, int verifiedBuild)
    {
        GameAssembly = gameAssembly;
        Release = release;
        MinSupportedBuild = minSupportedBuild;
        VerifiedBuild = verifiedBuild;
    }

    // —— 生命周期 ——————————————————————————————————————

    // mod 开启时调用一次（可重复调用，重扫前先卸载）
    public static void Initialize(Assembly assembly)
    {
        UnpatchAll();
        lock (_lock)
        {
            _featureToggles.Clear();
            _guardedMethods.Clear();
            _guardLogged.Clear();
            _warnedUnregistered.Clear();
            int index = 0;
            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    foreach (SpectrePatchAttribute attr in method.GetCustomAttributes<SpectrePatchAttribute>(false))
                    {
                        var spec = new Spec
                        {
                            Id = "SpectrePatch:" + type.Name + "." + method.Name + "#" + (index++),
                            Feature = attr.Feature,
                            PatchMethod = method,
                            Attr = attr
                        };
                        attr.PatchMethod = method;
                        Resolve(spec);
                        _specs.Add(spec);
                    }
                }
            }
        }
        LogProbe();
    }

    // 功能开关注册：Feature 名 → 开关谓词（Main 里与 Options 绑定）
    public static void SetFeatureToggle(string feature, Func<bool> toggle)
    {
        lock (_lock)
        {
            _featureToggles[feature] = toggle;
        }
    }

    // 按当前开关状态应用/卸载（PatchManager.RefreshPatches 与 ApplyAll 尾部调用）
    public static void Refresh()
    {
        lock (_lock)
        {
            // 开关谓词按功能只求值一次（同功能的多个补丁共享结果，49 个 spec 只跑
            // 12 个谓词）；状态未翻转的补丁不触碰 Harmony——GUI 拨单个开关引发的
            // 全量刷新，实际作用于状态翻转的功能
            var toggleResults = new Dictionary<string, bool>();
            foreach (Spec spec in _specs)
            {
                // 未注册开关的功能：补丁不会应用（ToggleOn=false），这里显式告警一次，
                // 否则错位的 Feature 字符串会表现为"探针就绪但功能没生效"的静默失败
                if (spec.Error == null && spec.Feature != null
                    && !_featureToggles.ContainsKey(spec.Feature)
                    && _warnedUnregistered.Add(spec.Feature))
                {
                    Debug.LogWarning("[" + LogTag + "] 功能 \"" + spec.Feature
                        + "\" 未注册开关（Main 里 SetFeatureToggle 缺失），相关补丁不会应用");
                }
                bool want = spec.Error == null && ToggleOnCached(spec.Feature, toggleResults);
                if (want && !spec.Applied)
                    Apply(spec);
                else if (!want && spec.Applied)
                    Unapply(spec);
            }
        }
    }

    // ToggleOn 的按次缓存版本：同一次 Refresh 内每个功能只执行一次开关谓词
    private static bool ToggleOnCached(string feature, Dictionary<string, bool> cache)
    {
        if (feature == null) return true;
        if (!cache.TryGetValue(feature, out bool on))
        {
            on = _featureToggles.TryGetValue(feature, out Func<bool> toggle) && toggle();
            cache[feature] = on;
        }
        return on;
    }

    public static void UnpatchAll()
    {
        lock (_lock)
        {
            foreach (Spec spec in _specs)
                if (spec.Applied)
                    Unapply(spec);
            _specs.Clear();
        }
    }

    // 某功能当前是否可用（所有相关补丁解析/应用正常）。UI 可用来显示"录像模式不可用"
    public static bool IsAvailable(string feature)
    {
        lock (_lock)
        {
            return _specs.Where(s => s.Feature == feature).All(s => s.Error == null);
        }
    }

    // —— 解析 ————————————————————————————————————————————

    private static void Resolve(Spec spec)
    {
        SpectrePatchAttribute a = spec.Attr;
        if (Release > 0)
        {
            // 宿主注入的最低支持版本作隐式地板，attribute 只在需要更高下限时才设 MinVersion
            int min = Math.Max(a.MinVersion, MinSupportedBuild);
            if (Release < min)
            {
                spec.Error = "游戏 r" + Release + " 低于 MinVersion " + min;
                return;
            }
            if (a.MaxVersion > 0 && Release > a.MaxVersion)
            {
                spec.Error = "游戏 r" + Release + " 高于 MaxVersion " + a.MaxVersion + "（未验证）";
                return;
            }
        }
        Type type = a.ClassType;
        if (type == null && !string.IsNullOrEmpty(a.ClassName))
            type = GameAssembly?.GetType(a.ClassName) ?? AccessTools.TypeByName(a.ClassName);
        if (type == null)
        {
            spec.Error = "目标类型未找到: " + (a.ClassType != null ? a.ClassType.FullName : a.ClassName);
            return;
        }

        var names = new List<string>();
        if (!string.IsNullOrEmpty(a.MethodName)) names.Add(a.MethodName);
        if (a.MethodNames != null)
            names.AddRange(a.MethodNames.Where(n => !string.IsNullOrEmpty(n) && !names.Contains(n)));
        if (names.Count == 0)
        {
            spec.Error = "未指定目标方法名";
            return;
        }

        foreach (string name in names)
        {
            MethodBase target = FindMethod(type, name, a);
            if (target != null)
            {
                spec.Target = target;
                spec.TargetName = type.Name + "." + name;
                return;
            }
        }
        spec.Error = "目标未找到或重载无法消歧（候选: " + string.Join(", ", names)
            + "）；可用 ParameterTypes 精确限定参数签名";
    }

    // 目标解析：属性访问器直取；方法则枚举全部同名重载（含基类）再消歧——
    // ParameterTypes 精确签名优先，其次用补丁方法声明的普通参数名消歧
    // （Prefix 里写了 bool isAuto，就选含 isAuto 的重载），仍不唯一返回 null 进探针，
    // 绝不静默绑到任意一个重载上
    private static MethodBase FindMethod(Type type, string name, SpectrePatchAttribute a)
    {
        try
        {
            if (a.Accessor != PropertyAccessor.None)
            {
                PropertyInfo pi = AccessTools.Property(type, name);
                if (pi == null) return null;
                return a.Accessor == PropertyAccessor.Get ? pi.GetGetMethod(true) : pi.GetSetMethod(true);
            }

            Type[] args = a.ParameterTypes;
            if (args == null && a.ParameterTypeNames != null)
                args = a.ParameterTypeNames
                    .Select(n => Type.GetType(n, false) ?? AccessTools.TypeByName(n))
                    .ToArray();

            if (name == ".ctor")
            {
                ConstructorInfo[] ctors = type.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (args != null)
                    return ctors.FirstOrDefault(c => SignatureMatches(c, args));
                return ctors.Length == 1 ? ctors[0] : null;
            }

            var candidates = new List<MethodInfo>();
            for (Type t = type; t != null; t = t.BaseType)
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == name) candidates.Add(m);
                }
            }
            // 去重：override 方法连同基类（abstract/virtual）声明会被一起收进候选，
            // 两者 GetBaseDefinition 相同——只保留最派生的实现（前向遍历，派生类先入列，
            // 基类声明被视为重复剔除）；不同重载的 BaseDefinition 各不相同，不受影响
            var deduped = new List<MethodInfo>();
            var seenBaseDefs = new HashSet<MethodInfo>();
            foreach (MethodInfo m in candidates)
                if (seenBaseDefs.Add(m.GetBaseDefinition()))
                    deduped.Add(m);
            candidates = deduped;
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            if (args != null)
                return candidates.FirstOrDefault(m => SignatureMatches(m, args));
            return DisambiguateByPatchParams(candidates, a.PatchMethod);
        }
        catch
        {
            return null;
        }
    }

    private static bool SignatureMatches(MethodBase method, Type[] args)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != args.Length) return false;
        for (int i = 0; i < args.Length; i++)
            if (parameters[i].ParameterType != args[i]) return false;
        return true;
    }

    // 重载消歧：补丁方法的普通参数（__ 开头的注入项除外）按名落在哪个重载上。
    // 候选必须包含全部普通参数名才幸存；多个幸存时选"多出来的目标参数最少"者，
    // 并列取声明序靠前者（确定性），全部淘汰则返回 null（进探针，不猜）
    private static MethodInfo DisambiguateByPatchParams(List<MethodInfo> candidates, MethodInfo patch)
    {
        if (patch == null) return null;
        ParameterInfo[] ordinary = patch.GetParameters()
            .Where(p => !p.Name.StartsWith("__", StringComparison.Ordinal))
            .ToArray();
        if (ordinary.Length == 0) return null;
        MethodInfo best = null;
        int bestExtra = int.MaxValue;
        foreach (MethodInfo candidate in candidates)
        {
            ParameterInfo[] target = candidate.GetParameters();
            int matched = 0;
            foreach (ParameterInfo p in ordinary)
                if (target.Any(tp => tp.Name == p.Name)) matched++;
            if (matched < ordinary.Length) continue;
            int extra = target.Length - matched;
            if (extra < bestExtra)
            {
                bestExtra = extra;
                best = candidate;
            }
        }
        return best;
    }

    // —— 应用 ————————————————————————————————————————————

    private static bool ToggleOn(string feature)
        => feature == null || (_featureToggles.TryGetValue(feature, out Func<bool> toggle) && toggle());

    private static void Apply(Spec spec)
    {
        try
        {
            spec.Owner = new Harmony(spec.Id);
            HarmonyMethod patch = new(spec.PatchMethod);
            // 本库 HarmonyMethod 的优先级字段拼写正常（老版本是 prioriy 拼错版）
            if (spec.Attr.Priority != PatchPriority.None)
                patch.priority = (int)spec.Attr.Priority;
            patch.before = spec.Attr.Before;
            patch.after = spec.Attr.After;
            // finalizer 只对 Prefix/Postfix 有意义（Transpiler 异常发生在补丁期，Finalizer 自身即兜底）
            HarmonyMethod guard = spec.Attr.TryingCatch && spec.Attr.PatchType != PatchType.Transpiler
                && spec.Attr.PatchType != PatchType.Finalizer
                ? _guardMethod : null;
            switch (spec.Attr.PatchType)
            {
                case PatchType.Prefix:
                    spec.Owner.Patch(spec.Target, patch, null, null, guard);
                    break;
                case PatchType.Postfix:
                    spec.Owner.Patch(spec.Target, null, patch, null, guard);
                    break;
                case PatchType.Transpiler:
                    spec.Owner.Patch(spec.Target, null, null, patch, null);
                    break;
                case PatchType.Finalizer:
                    spec.Owner.Patch(spec.Target, null, null, null, patch);
                    break;
            }
            spec.Applied = true;
            if (guard != null)
                _guardedMethods.Add(spec.PatchMethod);
        }
        catch (Exception ex)
        {
            spec.Error = "应用失败: " + ex.GetType().Name + ": " + ex.Message;
            Debug.LogWarning("[" + LogTag + "] " + spec.Id + " → " + spec.Error);
        }
    }

    private static void Unapply(Spec spec)
    {
        try
        {
            if (spec.Owner != null && spec.Target != null)
                spec.Owner.Unpatch(spec.Target, HarmonyPatchType.All, spec.Id);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[" + LogTag + "] 卸载失败 " + spec.Id + ": " + ex.Message);
        }
        spec.Applied = false;
    }

    // —— 异常兜底（共享 finalizer）———————————————————————

    // 只在异常路径执行（无异常时 Harmony 不会调用 finalizer）。
    // 只吞"我们自己补丁体"抛出的异常（栈帧比对），游戏本体的异常原样放行——
    // 否则会把游戏自身的 bug 也吞掉，静默劣化比报错更糟。
    // 注意：Prefix 中途抛出被吞后原方法不会执行（Harmony 语义），该功能可能退化，
    // 属"有日志的可控降级"，仍优于旧二进制在新游戏上反复打断游戏流程。
    // 返回 null = 吞掉；返回原异常 = 继续抛。Guard 自身绝不允许再抛
    // （finalizer 内的异常 Harmony 不再保护，会直接终止游戏）。
    private static Exception Guard(Exception __exception, MethodBase __originalMethod)
    {
        if (__exception == null) return null;
        try
        {
            var stack = new StackTrace(__exception, false);
            foreach (StackFrame frame in stack.GetFrames())
            {
                MethodBase frameMethod = frame?.GetMethod();
                if (frameMethod == null) continue;
                lock (_lock)
                {
                    if (!_guardedMethods.Contains(frameMethod)) continue;
                    string key = frameMethod.DeclaringType?.FullName + "." + frameMethod.Name
                        + "@" + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
                    if (_guardLogged.Add(key))
                        Debug.LogWarning("[" + LogTag + "] 补丁 " + frameMethod.DeclaringType?.FullName + "."
                            + frameMethod.Name + " 在 " + __originalMethod.DeclaringType?.Name + "."
                            + __originalMethod.Name + " 内抛出异常（已吞掉，功能可能退化，游戏版本不匹配？）: "
                            + __exception.GetType().Name + ": " + __exception.Message + "\n" + __exception.StackTrace);
                    return null;
                }
            }
        }
        catch
        {
            // 比对失败时保守放行原异常
        }
        return __exception;
    }

    // —— 探针 ————————————————————————————————————————————

    private static void LogProbe()
    {
        int total, ok;
        List<Spec> failed;
        lock (_lock)
        {
            total = _specs.Count;
            failed = _specs.Where(s => s.Error != null).ToList();
            ok = total - failed.Count;
        }
        Debug.Log("[" + LogTag + "] SpectrePatch 探针: 游戏 r" + Release
            + "，已验证 r" + VerifiedBuild + "，" + ok + "/" + total + " 就绪");
        if (!Verified)
            Debug.LogWarning("[" + LogTag + "] 当前游戏版本未验证（r" + Release
                + " ≠ r" + VerifiedBuild + "），如遇异常请反馈日志");
        foreach (Spec s in failed)
            Debug.LogWarning("[" + LogTag + "]   ✗ " + s.Id + " → " + s.Error);
    }
}
