# SpectrePatch

源码链接式共享补丁框架（attribute 声明 + 应用器 + 探针 + 异常日志泵 + ADOFAI 版本探针）。
**不产出独立 dll**——各宿主 mod 把本仓库源码编进各自程序集，静态状态天然隔离，
多 mod 同装互不干扰（对比独立 dll 分发的 LoadFrom 身份去重 / 静态串门问题）。
源自 Spectre mod，通用化后独立成库（MIT）。

## 在宿主 mod 中接入

1. **把本仓库克隆到宿主仓库旁边**（兄弟目录），csproj 源码链接：

   ```xml
   <ItemGroup>
     <Compile Include="..\SpectrePatch\*.cs" Link="SpectrePatch\%(Filename)%(Extension)" />
   </ItemGroup>
   ```

2. **mod 启动时注入宿主上下文**（必须先于 Initialize）：

   ```csharp
   // 日志前缀改成自己的 mod 名（可选，默认 "SpectrePatch"）
   SpectrePatchHub.LogTag = "MyMod";
   // 游戏主程序集（ClassName 字符串解析用）+ 版本政策三项：
   //   release 用共享库 GameVersion.Release（ADOFAI 探针）；
   //   minSupportedBuild / verifiedBuild = 本 mod 的支持下限 / 已验证 build
   SpectrePatchHub.Configure(typeof(ADOBase).Assembly,
       GameVersion.Release, minSupported, verified);
   SpectrePatchHub.Initialize(typeof(Main).Assembly);
   ```

3. **注册功能开关**（每个 `Feature` 一条，拨开关时调 `SpectrePatchHub.Refresh()`）：

   ```csharp
   SpectrePatchHub.SetFeatureToggle("MyFeature", () => Options.MyFeatureOn);
   SpectrePatchHub.Refresh();
   ```

之后补丁写法与 Spectre 内一致：

```csharp
[SpectrePatch(typeof(scrPlayer), "Hit", PatchType.Prefix, Feature = "MyFeature",
    Priority = PatchPriority.First, After = new[] { "别的mod的HarmonyID" })]
internal static bool Prefix(scrPlayer __instance) { ... }
```

（完整用法见 `SpectrePatch.cs` 文件头的多版本剧本 / 参数限定注释块）

## 依赖

- 0Harmony——由 UnityModManager（UMM）注入游戏目录的 `Managed/UnityModManager/`，是魔改版：Priority 常量为 Last=0/Normal=400/First=800（标准 Harmony 是 ±int.MaxValue），priority 未设默认 -1
- UnityEngine（Debug 日志）
- MelonLoader 宿主注意：Melon 自带标准 Harmony，上述常量值不同（见 `PatchPriority` 注释）
