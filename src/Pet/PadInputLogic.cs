using System;
using System.Collections.Generic;

namespace WuwatappoNative.Pet;

/// <summary>
/// 手柄输入的纯逻辑部分：keystroke 过滤、XInput 虚拟键映射和按下去重。
/// 不依赖 XInput DLL / WinForms，可在隔离测试中直接驱动。
/// </summary>
internal static class PadInputLogic
{
    internal const ushort KeyStrokeKeyDown = 0x0001;
    internal const ushort KeyStrokeKeyUp = 0x0002;
    internal const ushort KeyStrokeRepeat = 0x0004;

    /// <summary>
    /// 只接受真正的初次按下。长按期间 XInput 会持续发送 KEYDOWN | REPEAT，
    /// 这些重复事件不能再算作新的按下。
    /// </summary>
    internal static bool IsFreshKeyDown(ushort flags) =>
        (flags & KeyStrokeKeyDown) != 0 && (flags & KeyStrokeRepeat) == 0;

    internal static bool IsKeyUp(ushort flags) =>
        (flags & KeyStrokeKeyUp) != 0 && (flags & KeyStrokeRepeat) == 0;

    internal static string LabelForVirtualKey(ushort vk) => vk switch
    {
        0x5800 => "A",
        0x5801 => "B",
        0x5802 => "X",
        0x5803 => "Y",
        0x5804 => "RB",
        0x5805 => "LB",
        0x5806 => "LT",
        0x5807 => "RT",
        0x5810 => "↑",
        0x5811 => "↓",
        0x5812 => "←",
        0x5813 => "→",
        0x5814 => "Start",
        0x5815 => "Back",
        0x5816 => "LS",
        0x5817 => "RS",
        _ => null,
    };
}

/// <summary>
/// 手柄按下状态去重器。状态轮询与 keystroke 队列都从这里进入：
/// 同一个物理按下无论被哪条路径先看到，只会返回一次“新按下”；
/// 只有所有活动按键都结束后才报告一次 ReleasedAll。
/// </summary>
internal sealed class PadInputTracker
{
    readonly HashSet<string> _physicalDown = new(StringComparer.Ordinal);
    readonly HashSet<string> _active = new(StringComparer.Ordinal);
    readonly List<string> _pressed = new();

    bool _hadActiveAtPollStart;

    /// <summary>当前轮询内新按下的键（每个键最多一次）。</summary>
    internal IReadOnlyList<string> PressedLabels => _pressed;

    internal bool PressedAny => _pressed.Count > 0;

    internal bool AnyActive => _active.Count > 0;

    /// <summary>
    /// 本轮开始时还有按键按着，并且轮询结束后所有按键都已松开。
    /// 极短点按从按下到松开都在同一轮里完成时不算 ReleasedAll，
    /// 由 90ms 补释放定时器负责收尾。
    /// </summary>
    internal bool ReleasedAll => _hadActiveAtPollStart && _active.Count == 0 && _pressed.Count == 0;

    internal void BeginPoll()
    {
        _hadActiveAtPollStart = _active.Count > 0;
        _pressed.Clear();
    }

    internal void Reset()
    {
        _physicalDown.Clear();
        _active.Clear();
        _pressed.Clear();
        _hadActiveAtPollStart = false;
    }

    /// <summary>连接/换槽位后的首帧：只同步按下态，不产生按下边沿。</summary>
    internal void SyncDown(IEnumerable<string> labels)
    {
        Reset();
        if (labels == null) return;
        foreach (string label in labels)
        {
            if (string.IsNullOrEmpty(label)) continue;
            _physicalDown.Add(label);
            _active.Add(label);
        }
    }

    /// <summary>状态轮询看到某个键的按下/松开。</summary>
    internal void ObserveStateButton(string label, bool down)
    {
        if (string.IsNullOrEmpty(label)) return;
        if (down)
        {
            // 第一次看到该键按下；如果 keystroke 队列已经先计过，就不再重复。
            if (!_physicalDown.Add(label)) return;
            if (_active.Add(label)) _pressed.Add(label);
        }
        else
        {
            if (!_physicalDown.Remove(label)) return;
            _active.Remove(label);
        }
    }

    /// <summary>keystroke 队列中的初次 KEYDOWN。返回 true 表示这是一次新的按下。</summary>
    internal bool ObserveKeystrokeDown(string label)
    {
        if (string.IsNullOrEmpty(label)) return false;
        if (!_active.Add(label)) return false;
        _pressed.Add(label);
        return true;
    }

    /// <summary>
    /// keystroke 队列中的 KEYUP。状态仍显示物理按住时不结束按下，
    /// 避免队列里旧点按的尾巴把当前长按提前松开。
    /// </summary>
    internal bool ObserveKeystrokeUp(string label)
    {
        if (string.IsNullOrEmpty(label)) return false;
        if (_physicalDown.Contains(label)) return false;
        return _active.Remove(label);
    }

    /// <summary>keystroke 事件入口：内部完成过滤与虚拟键映射。</summary>
    internal bool ObserveKeystroke(ushort virtualKey, ushort flags)
    {
        string label = PadInputLogic.LabelForVirtualKey(virtualKey);
        if (label == null) return false;
        if (PadInputLogic.IsFreshKeyDown(flags)) return ObserveKeystrokeDown(label);
        if (PadInputLogic.IsKeyUp(flags)) return ObserveKeystrokeUp(label);
        return false;
    }

    /// <summary>
    /// 补释放定时器专用：只清理状态轮询从未确认过的活动按下
    /// （极短点按的 KEYDOWN 可能没有对应 KEYUP），物理按住的键保持不动。
    /// </summary>
    internal void ReleaseUnconfirmed()
    {
        _active.RemoveWhere(label => !_physicalDown.Contains(label));
    }
}
