using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace SpectrePatch;

// 主线程零阻塞日志：调用线程只做字符串拼接与入队（微秒级），
// Debug.Log 的栈采集/入队等重活由专用后台线程承担。
// 取舍（有意为之）：
//   - 与其他 mod/游戏本体的日志行序可能交错（各自线程竞争入队）；
//   - 本日志的栈信息指向后台线程（消息自带上下文，不影响排查）；
//   - 进程硬退出时队列尾部可能丢几行（worker 是后台线程不阻止退出）；
//     mod 关闭时 UnpatchAll 会停泵并同步清空队列。
internal static class AsyncLog
{
    private static readonly ConcurrentQueue<(int Level, string Msg)> Queue = new();
    private static readonly ManualResetEventSlim Wake = new(false);
    private static Thread worker;
    private static volatile bool stopping;

    internal static void Info(string msg) => Enqueue(0, msg);
    internal static void Warning(string msg) => Enqueue(1, msg);

    private static void Enqueue(int level, string msg)
    {
        if (worker == null || stopping)
        {
            Emit(level, msg); // 未启动/已停泵：同步打，不丢记录
            return;
        }
        Queue.Enqueue((level, msg));
        Wake.Set();
    }

    // 幂等；Hub.Initialize 时启动
    internal static void Start()
    {
        if (worker != null)
            return;
        stopping = false;
        worker = new Thread(Drain) { IsBackground = true, Name = "SpectrePatch-AsyncLog" };
        worker.Start();
    }

    // 停泵并同步清空剩余队列（Hub.UnpatchAll 时调用）
    internal static void Stop()
    {
        if (worker == null)
            return;
        stopping = true;
        Wake.Set();
        worker.Join(1000);
        worker = null;
        while (Queue.TryDequeue(out var e))
            Emit(e.Level, e.Msg);
    }

    private static void Drain()
    {
        while (true)
        {
            while (Queue.TryDequeue(out var e))
                Emit(e.Level, e.Msg);
            if (stopping)
                return;
            Wake.Reset();
            // Reset 后复查一次再等，防"Reset 与入队 Set 竞争"漏唤醒；500ms 兜底轮询
            if (Queue.IsEmpty)
                Wake.Wait(500);
        }
    }

    private static void Emit(int level, string msg)
    {
        if (level == 0)
            Debug.Log(msg);
        else
            Debug.LogWarning(msg);
    }
}
