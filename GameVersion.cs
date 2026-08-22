using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SpectrePatch;

// ADOFAI 游戏版本探针（共享库源码直接引游戏类型——源码链接式编进各宿主，
// 宿主工程都引用 Assembly-CSharp）。启动读一次 releaseNumber：
// 新版游戏在 GCNS，旧版在 Releases；读取失败 = -1（版本门控放行，只降级警告）。
// 支持下限/已验证 build 是各宿主自己的政策值（Spectre 在 Main 里声明后经
// SpectrePatchHub.Configure 注入）。
internal static class GameVersion
{
    internal static readonly int Release = ReadRelease();

    private static int ReadRelease()
    {
        try
        {
            Assembly game = typeof(ADOBase).Assembly;
            foreach (string typeName in new[] { "GCNS", "Releases" })
            {
                Type type = game.GetType(typeName) ?? AccessTools.TypeByName(typeName);
                FieldInfo field = type == null ? null : AccessTools.Field(type, "releaseNumber");
                if (field != null && field.IsStatic)
                    return Convert.ToInt32(field.GetValue(null));
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectrePatch] 游戏版本读取失败: " + ex.Message);
        }
        return -1;
    }
}
