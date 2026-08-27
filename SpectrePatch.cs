using System;
using System.Reflection;

namespace SpectrePatch;

/// <summary>补丁注入方式（Harmony 原生语义）。</summary>
internal enum PatchType
{
    /// <summary>前置：先于原方法执行；返回 bool 可跳过原方法（false = 跳过）。</summary>
    Prefix,
    /// <summary>后置：原方法之后执行，可改写 <c>__result</c>。</summary>
    Postfix,
    /// <summary>IL 改写：直接改写原方法体；异常发生在补丁应用期，不走 TryingCatch。</summary>
    Transpiler,
    /// <summary>收尾：原方法+全部补丁之后执行，吞/改异常；Harmony 语义下无异常时也会被调用。</summary>
    Finalizer,
}

/// <summary>成员访问器目标：目标成员在 IL 层可能拆成多个方法，补丁需指明打哪一个。
/// 枚举名沿用历史 PropertyAccessor，语义已泛化为"成员访问器"。</summary>
internal enum PropertyAccessor
{
    /// <summary>普通方法（默认）。</summary>
    None,
    /// <summary>打属性的 getter。</summary>
    Get,
    /// <summary>打属性的 setter。</summary>
    Set,
    /// <summary>打事件的 add 访问器（目标名填事件名）。</summary>
    EventAdd,
    /// <summary>打事件的 remove 访问器（目标名填事件名）。</summary>
    EventRemove,
    /// <summary>打 async/迭代器方法的编译器状态机 MoveNext（目标名填方法名）。
    /// 协程/async 的方法体不在原方法里——原方法只建状态机；要看每次推进的逻辑得打 MoveNext。
    /// 解析优先读方法的 StateMachineAttribute，老编译器退回嵌套类型名 &lt;方法名&gt;d 模式。</summary>
    MoveNext,
}

// 优先级枚举：成员值即本环境（UMM 注入的魔改 Harmony）的 Priority 常量（数值越大越先执行；
// 前缀从高到低跑，后缀反序——高优先级像洋葱外层）。None 是"不设置"哨兵，
// 保持库默认 -1（垫底档，多个 -1 按挂载序）；Final = 要求绝对最后
//（比一切常量都晚，给"看最终结果再收尾"的补丁用，如飘字剥残留）。
internal enum PatchPriority
{
    /// <summary>不设置：保持 HarmonyMethod 库默认 -1（垫底档，多个 -1 按挂载序）。</summary>
    None = -1,
    /// <summary>绝对最后：比一切常量都晚，给"看最终结果再收尾"的补丁用。
    /// 枚举值只是常规域兜底；实际生效值由 Hub 运行时反射 HarmonyLib.Priority.Last
    /// 动态下探一位（防 UMM 更换 ±int.MaxValue 常量域后 -1000000 不再垫底）。</summary>
    Final = -1000000,
    /// <summary>最后（=0）。</summary>
    Last = 0,
    /// <summary>正常（=400）。默认档。</summary>
    Normal = 400,
    /// <summary>最前（=800）。注意 UMM 魔改 Harmony 的常量与标准版（±int.MaxValue）不同，别照抄。</summary>
    First = 800,
}

// 方法级补丁声明，应用器见 SpectrePatchHub：
//     [SpectrePatch(typeof(scrPlayer), "Hit", PatchType.Prefix)]
//     internal static bool Prefix(scrPlayer __instance) { ... }
// 注入规则与 Harmony 原生一致（__instance / ___field / __result / ref 参数按名绑定）。
// 目标类型：稳定核心用 typeof 直引（编译期检查）；易变边缘用 ClassName 字符串，
// 游戏改名时只改字符串不动签名。
//
// ── 多版本剧本 ─────────────────────────────────────────────
// ① 游戏改名（最常见）：候选链，新名在前、旧名兜底，一个 dll 兼容两代游戏：
//     [SpectrePatch(typeof(scrPlayer), "OnHit", PatchType.Prefix,
//         MethodNames = new[] { "Hit" })]
//    解析顺序 = MethodName → MethodNames 依次尝试，命中即用，失配进探针。
//
// ② 核心补丁防未验证版本：pin 到已验证 build——游戏更新后自动停用（fail-closed，
//    探针报"高于 MaxVersion（未验证）"），适配完成把宿主注入的 VerifiedBuild
//    推进到新 build，所有 pin 的补丁一并解锁：
//     [SpectrePatch(typeof(scrPlayer), "Hit", PatchType.Prefix,
//         MaxVersion = GameVersion.VerifiedBuild)]
//    适合录制/录像这类"错数据比没功能更糟"的核心；纯展示类补丁不 pin，
//    失配时靠探针警告即可（fail-open）。
//
// ③ 真分叉（方法还在但语义随版本变了）：两个补丁方法各占一段版本区间，
//    任一时刻只有一边生效：
//     [SpectrePatch(typeof(X), "M", PatchType.Prefix, MaxVersion = 149)]
//     static void PrefixOld(...) { ... }          // r149 及以前
//     [SpectrePatch(typeof(X), "M", PatchType.Prefix, MinVersion = 150)]
//     static void PrefixNew(...) { ... }          // r150 起
//
// ④ 全局最低版本：宿主经 Configure 注入的 MinSupportedBuild 是所有补丁的隐式
//    MinVersion 地板，且低于它的游戏版本宿主侧应拒绝启用（Spectre 是 Main.OnToggle 网关）。
//    MinVersion 字段只在某补丁需要比全局更高的下限时才设（如目标 API r145 才有）：
//     [SpectrePatch(typeof(X), "NewApi", PatchType.Postfix, MinVersion = 145)]
//
// ── 参数限定（重载消歧，绝不静默绑到任意一个上）─────────────
//   1. ParameterTypes 精确签名匹配（推荐，编译期检查），可作第 4 个位置参数：
//     [SpectrePatch(typeof(DetailedResults), "Show", PatchType.Postfix, new[] { typeof(bool) })]
//      字符串形式（类型不便直引时，如游戏私有类型，仅命名赋值）：
//      ParameterTypeNames = new[] { "System.Boolean", "System.Int32" }
//   2. 零配置：补丁方法声明了普通参数（bool isAuto）即按参数名自动消歧
//   3. 仍不唯一 → 解析失败进探针，写明候选与建议
//
// ── 其他 ───────────────────────────────────────────────────
//   - 属性访问器：第 4 个位置参数 PropertyAccessor.Get/Set（目标名填属性名）：
//     [SpectrePatch(typeof(scrConductor), "songposition_minusi", PatchType.Postfix, PropertyAccessor.Get)]
//   - 构造函数：目标名填 ".ctor"（实例构造器；多个时用 ParameterTypes 消歧，唯一则免写）
//     或 ".cctor"（静态构造器，恒无参、至多一个）：
//     [SpectrePatch(typeof(scrConductor), ".ctor", PatchType.Postfix)]
//   - TryingCatch 默认 true：补丁体异常由共享 finalizer 吞掉并记录（无异常零开销），
//     同一异常按窗口限流合并，持续异常窗口后打一行计数摘要；
//     DisableAfterExceptions > 0 时窗口内累计达到阈值直接自动卸载该补丁
//   - Feature：功能分组开关，取值用 FeatureKeys 常量；Main 里 SetFeatureToggle
//     注册对应谓词，未注册的功能其补丁不应用并在日志告警。
//     契约：谓词为假时补丁根本不挂载，拨开关同拍卸载——补丁体内**无需再查
//     主开关**，体内检查只留给 Feature 之下的子选项。
//     多值：',' 或 '|' 分隔 = 任一开启即挂载（共享补丁不必宿主侧写 OR 谓词）：
//     Feature = "ResultsPlus,CalibAdvice"
//     '&' 分隔 = 全部开启才挂载（AND；与 ,/| 不可混用，混用进探针）：
//     Feature = "HitErrorMeter&CustomJudge"
//
// ── 目标种类的其余入口 ─────────────────────────────────────
//   - 事件访问器：Accessor 填 EventAdd/EventRemove，目标名填事件名
//   - async/迭代器：Accessor 填 MoveNext，目标名填方法名——实际打编译器
//     状态机的 MoveNext（优先读 StateMachineAttribute，老编译器退回嵌套类型名模式）：
//     [SpectrePatch(typeof(scrController), "PlayCoroutine", PatchType.Prefix, PropertyAccessor.MoveNext)]
//   - 全部重载：AllOverloads = true，同名重载挨个都挂（.ctor = 全部构造器）；
//     消歧失败不再进探针放弃，适合"同名多重载、逻辑一样"的场景
//   - 类型候选链：ClassNames = new[]{ "NewNs.NewName", "OldNs.OldName" }，
//     与 MethodNames 对称，类型改名/搬命名空间的多版本兜底
//
// ── 运行时手动挂载 ─────────────────────────────────────────
//   - 目标编译期不可描述（运行时反射才发现的类型/方法，如其他 mod 的）时，
//     跳过 attribute 直接调 SpectrePatchHub.PatchManual(target, patch, type, id, ...)；
//     与 attribute 补丁共用应用/卸载/开关/异常兜底与执行链视图
// 字段经 attribute 命名参数赋值，编译器看不到赋值点
#pragma warning disable CS0649
/// <summary>
/// 方法级补丁声明；由 <see cref="SpectrePatchHub"/> 扫描应用（候选链/版本门控/异常兜底/探针）。
/// 用法与多版本剧本详见上方注释块，注入规则与 Harmony 原生一致。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class SpectrePatchAttribute : Attribute
{
    /// <summary>目标类型（typeof 直引，编译期检查）。与 <see cref="ClassName"/> 二选一，优先用本字段。</summary>
    public Type ClassType;

    /// <summary>目标类型全名字符串（类型不便直引时用，如游戏私有/改名类型）。</summary>
    public string ClassName;

    /// <summary>类型候选链：ClassName 未命中时依次尝试（类型改名/搬命名空间的多版本兜底，新名在前）。</summary>
    public string[] ClassNames;

    /// <summary>目标方法/属性名（首选候选）。</summary>
    public string MethodName;

    /// <summary>候选链：MethodName 之后依次尝试（游戏改名的多版本兜底，新名在前）。</summary>
    public string[] MethodNames;

    /// <summary>打在全部同名重载上（目标名填 .ctor 时 = 全部构造器）。默认 false：
    /// 重载不唯一且消歧失败时进探针不挂。</summary>
    public bool AllOverloads;

    /// <summary>注入方式，默认 Postfix。</summary>
    public PatchType PatchType = PatchType.Postfix;

    /// <summary>最低游戏 build（含）；通常不设——全局地板已是 MinSupportedBuild。</summary>
    public int MinVersion;

    /// <summary>最高游戏 build（含）；核心补丁 pin 到 VerifiedBuild，游戏更新自动停用（fail-closed）。</summary>
    public int MaxVersion;

    /// <summary>目标参数类型的字符串形式（如 "System.Boolean"），用于重载消歧；优先用 <see cref="ParameterTypes"/>。</summary>
    public string[] ParameterTypeNames;

    /// <summary>目标参数类型精确匹配（编译期检查），用于重载消歧；可作第 4 个位置参数。</summary>
    public Type[] ParameterTypes;

    /// <summary>属性访问器目标：None = 普通方法；Get/Set = 属性访问器；EventAdd/EventRemove = 事件访问器；
    /// MoveNext = async/迭代器方法的编译器状态机（目标名填方法名，实际打其状态机的 MoveNext）。</summary>
    public PropertyAccessor Accessor = PropertyAccessor.None;

    /// <summary>补丁体异常由共享 finalizer 吞掉并记录（默认 true，无异常零开销）。</summary>
    public bool TryingCatch = true;

    /// <summary>TryingCatch 生效时，30s 窗口内异常累计达到该次数即自动卸载此补丁
    ///（0 = 从不，默认）。适合"宁缺毋滥"的补丁：持续故障自我了断好过无限刷日志；
    /// 卸载后重新 Initialize（mod 重启）前不会自动恢复。</summary>
    public int DisableAfterExceptions;

    /// <summary>功能分组开关（FeatureKeys 常量；多个用 "," 或 "|" 分隔，任一开启即挂载）。
    /// 谓词为假时补丁不挂载，体内无需再查主开关。</summary>
    public string Feature;

    /// <summary>同方法多补丁时的执行顺序。同 mod 内排序用本字段；跨 mod 用 <see cref="Before"/>/<see cref="After"/>。</summary>
    public PatchPriority Priority = PatchPriority.None;

    /// <summary>跨 mod 排序：排在指定 Harmony ID 的补丁之前（约束优先于 Priority 生效）。</summary>
    public string[] Before;

    /// <summary>跨 mod 排序：排在指定 Harmony ID 的补丁之后（约束优先于 Priority 生效）。</summary>
    public string[] After;

    // 应用器扫描时回填，attribute 用法里不写
    internal MethodInfo PatchMethod;

    public SpectrePatchAttribute() { }

    public SpectrePatchAttribute(Type classType, string methodName, PatchType patchType,
        PropertyAccessor accessor = PropertyAccessor.None)
    {
        ClassType = classType;
        MethodName = methodName;
        PatchType = patchType;
        Accessor = accessor;
    }

    /// <summary>带参数签名的重载消歧形式：<c>(类型, 方法名, 补丁类型, new[]{ 参数类型... })</c>，
    /// 与 PropertyAccessor 版按实参类型自动分流。</summary>
    public SpectrePatchAttribute(Type classType, string methodName, PatchType patchType, Type[] parameterTypes)
        : this(classType, methodName, patchType)
    {
        ParameterTypes = parameterTypes;
    }

    public SpectrePatchAttribute(string className, string methodName, PatchType patchType,
        PropertyAccessor accessor = PropertyAccessor.None)
    {
        ClassName = className;
        MethodName = methodName;
        PatchType = patchType;
        Accessor = accessor;
    }
}
#pragma warning restore CS0649
