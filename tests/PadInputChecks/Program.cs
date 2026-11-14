using System;
using System.Collections.Generic;
using System.Linq;
using WuwatappoNative.Pet;

internal static class Program
{
    static int _checks;

    const ushort Kd = PadInputLogic.KeyStrokeKeyDown;
    const ushort Ku = PadInputLogic.KeyStrokeKeyUp;
    const ushort Rep = PadInputLogic.KeyStrokeRepeat;

    [STAThread]
    static int Main()
    {
        try
        {
            CheckKeystrokeFilter();
            CheckVirtualKeyMap();
            CheckInitialKeyDown();
            CheckRepeatIgnored();
            CheckKeyUpDoesNotPress();
            CheckStateAndQueueDedupe();
            CheckLongHoldSingleDownSingleUp();
            CheckQuickTap();
            CheckMultiButton();
            CheckRapidPresses();
            Console.WriteLine($"PASS: {_checks} assertions.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static void Assert(bool condition, string message)
    {
        ++_checks;
        if (!condition) throw new InvalidOperationException(message);
    }

    static void CheckKeystrokeFilter()
    {
        Assert(PadInputLogic.IsFreshKeyDown(Kd), "KEYDOWN must be accepted.");
        Assert(!PadInputLogic.IsFreshKeyDown((ushort)(Kd | Rep)), "KEYDOWN | REPEAT must be rejected.");
        Assert(!PadInputLogic.IsFreshKeyDown(Ku), "KEYUP must not be treated as a down.");
        Assert(!PadInputLogic.IsFreshKeyDown(0), "Empty flags must not be treated as a down.");
        Assert(PadInputLogic.IsKeyUp(Ku), "KEYUP must be recognized.");
        Assert(!PadInputLogic.IsKeyUp((ushort)(Kd | Rep)), "KEYDOWN | REPEAT must not be treated as an up.");
    }

    static void CheckVirtualKeyMap()
    {
        var expected = new (ushort Vk, string Label)[]
        {
            (0x5800, "A"), (0x5801, "B"), (0x5802, "X"), (0x5803, "Y"),
            (0x5804, "RB"), (0x5805, "LB"), (0x5806, "LT"), (0x5807, "RT"),
            (0x5810, "↑"), (0x5811, "↓"), (0x5812, "←"), (0x5813, "→"),
            (0x5814, "Start"), (0x5815, "Back"), (0x5816, "LS"), (0x5817, "RS"),
        };
        foreach (var pair in expected)
            Assert(PadInputLogic.LabelForVirtualKey(pair.Vk) == pair.Label, $"Virtual key 0x{pair.Vk:X4} must map to {pair.Label}.");
        Assert(PadInputLogic.LabelForVirtualKey(0x9999) == null, "Unknown virtual keys must be ignored.");
    }

    static void CheckInitialKeyDown()
    {
        var h = new Harness();
        h.Poll(t => Assert(t.ObserveKeystroke(0x5800, Kd), "Initial KEYDOWN must start a press."));
        Assert(h.Downs == 1, "Initial KEYDOWN must produce exactly one down.");
        Assert(h.Ups == 0, "Initial KEYDOWN must not produce an up.");
        Assert(h.Bubbles.SequenceEqual(new[] { "A" }), "Initial KEYDOWN must produce one A bubble.");
    }

    static void CheckRepeatIgnored()
    {
        // 没有初次按下时，孤立的 REPEAT 也不能凭空触发。
        var fresh = new Harness();
        fresh.Poll(t => Assert(!t.ObserveKeystroke(0x5800, (ushort)(Kd | Rep)), "A REPEAT without a down must be ignored."));
        Assert(fresh.Downs == 0 && fresh.Bubbles.Count == 0, "A REPEAT without a down must not bubble or count.");

        var h = new Harness();
        h.Poll(t => t.ObserveStateButton("A", true));
        Assert(h.Downs == 1, "Initial state press must produce one down.");
        for (int i = 0; i < 120; i++)
        {
            h.Poll(t =>
            {
                t.ObserveStateButton("A", true);
                Assert(!t.ObserveKeystroke(0x5800, (ushort)(Kd | Rep)), "Held-button REPEAT must be ignored.");
            });
            Assert(h.Downs == 1, "Long hold must not produce extra downs.");
            Assert(h.Ups == 0, "Long hold must not release early.");
        }
        Assert(h.Bubbles.Count == 1, "Long hold must produce exactly one bubble.");
    }

    static void CheckKeyUpDoesNotPress()
    {
        var h = new Harness();
        h.Poll(t => Assert(!t.ObserveKeystroke(0x5800, Ku), "KEYUP must not start a press."));
        Assert(h.Downs == 0 && h.Bubbles.Count == 0 && !h.Tracker.AnyActive, "KEYUP alone must produce no down.");

        h.Poll(t => t.ObserveStateButton("A", true));
        h.Poll(t => Assert(!t.ObserveKeystroke(0x5800, Ku), "KEYUP while state still shows held must not end the press."));
        Assert(h.Downs == 1 && h.Ups == 0 && h.Tracker.AnyActive, "A stale queue KEYUP must not release a held button.");
        h.Poll(t => t.ObserveStateButton("A", false));
        Assert(h.Ups == 1, "Physical release must produce one up.");
    }

    static void CheckStateAndQueueDedupe()
    {
        // 状态轮询先看到：队列里的同一按下不再计数。
        var stateFirst = new Harness();
        stateFirst.Poll(t =>
        {
            t.ObserveStateButton("A", true);
            Assert(!t.ObserveKeystrokeDown("A"), "Queue KEYDOWN after a state down must be deduped.");
        });
        Assert(stateFirst.Downs == 1 && stateFirst.Bubbles.Count == 1, "State + queue for one press must count once.");

        // 队列先看到：随后状态轮询看到按住，不再重复。
        var queueFirst = new Harness();
        queueFirst.Poll(t =>
        {
            Assert(t.ObserveKeystrokeDown("A"), "Queue KEYDOWN must start the press.");
            t.ObserveStateButton("A", true);
        });
        Assert(queueFirst.Downs == 1 && queueFirst.Bubbles.Count == 1, "Queue + state for one press must count once.");

        // 跨轮询的重复：状态已经计过，下一轮队列不能再计。
        var acrossPolls = new Harness();
        acrossPolls.Poll(t => t.ObserveStateButton("A", true));
        acrossPolls.Poll(t => t.ObserveKeystrokeDown("A"));
        Assert(acrossPolls.Downs == 1 && acrossPolls.Bubbles.Count == 1, "Cross-poll state/queue duplicate must count once.");
    }

    static void CheckLongHoldSingleDownSingleUp()
    {
        var h = new Harness();
        h.Poll(t => t.ObserveStateButton("A", true));
        Assert(h.Downs == 1 && h.Ups == 0, "Long hold start must emit one down.");

        for (int i = 0; i < 200; i++)
        {
            h.Poll(t =>
            {
                t.ObserveStateButton("A", true);
                t.ObserveKeystroke(0x5800, (ushort)(Kd | Rep));
            });
            if (i == 0)
            {
                h.Tracker.ReleaseUnconfirmed();
                Assert(h.Tracker.AnyActive, "Fallback release must not clear a physically held press.");
            }
            Assert(h.Downs == 1, "Held state polling must not repeat down.");
            Assert(h.Ups == 0, "Held state polling must not produce up.");
        }

        h.Poll(t => t.ObserveStateButton("A", false));
        Assert(h.Downs == 1 && h.Ups == 1, "Release after a long hold must emit exactly one up.");
        Assert(h.Bubbles.Count == 1, "Release must not create another bubble.");

        h.Poll(t => t.ObserveStateButton("A", false));
        Assert(h.Ups == 1, "Repeated released-state polling must not emit another up.");

        // keystroke 队列单独承担整段长按（没有状态轮询介入）时也只计一次。
        var queueOnly = new Harness();
        queueOnly.Poll(t => Assert(t.ObserveKeystroke(0x5800, Kd), "Queue-only long hold must start once."));
        for (int i = 0; i < 60; i++)
            queueOnly.Poll(t => Assert(!t.ObserveKeystroke(0x5800, (ushort)(Kd | Rep)), "Queue-only long hold repeat must be ignored."));
        Assert(queueOnly.Downs == 1 && queueOnly.Ups == 0, "Queue-only long hold must not repeat.");
        queueOnly.Poll(t => Assert(t.ObserveKeystroke(0x5800, Ku), "Queue KEYUP must end the long hold."));
        Assert(queueOnly.Downs == 1 && queueOnly.Ups == 1, "Queue-only long hold must release exactly once.");
    }

    static void CheckQuickTap()
    {
        // 完整点按落在两次状态采样之间：只有 keystroke 队列看得到。
        var samePoll = new Harness();
        samePoll.Poll(t =>
        {
            Assert(t.ObserveKeystroke(0x5800, Kd), "Queue-only quick tap must still emit a down.");
            Assert(t.ObserveKeystroke(0x5800, Ku), "Queue KEYUP must close the quick tap.");
        });
        Assert(samePoll.Downs == 1 && samePoll.Bubbles.Count == 1, "Quick tap must produce one down and one bubble.");
        Assert(!samePoll.LastReleasedAll, "Same-poll quick tap release is left to the 90ms fallback timer.");
        Assert(!samePoll.Tracker.AnyActive, "Same-poll quick tap must not stay active.");
        samePoll.Tracker.ReleaseUnconfirmed();
        Assert(!samePoll.Tracker.AnyActive, "Fallback release must clear an unconfirmed queue press.");

        // KEYDOWN 和 KEYUP 落在相邻两轮：队列仍能补出一次完整按下/释放。
        var acrossPolls = new Harness();
        acrossPolls.Poll(t => Assert(t.ObserveKeystroke(0x5800, Kd), "Queue-only down must be accepted."));
        Assert(acrossPolls.Downs == 1 && !acrossPolls.LastReleasedAll, "Queue-only down must wait for its release.");
        acrossPolls.Poll(t => Assert(t.ObserveKeystroke(0x5800, Ku), "Queue KEYUP must close the pending queue press."));
        Assert(acrossPolls.Downs == 1 && acrossPolls.Ups == 1, "Quick tap across polls must produce one down and one up.");
    }

    static void CheckMultiButton()
    {
        var h = new Harness();
        h.Poll(t =>
        {
            t.ObserveStateButton("A", true);
            t.ObserveStateButton("B", true);
        });
        Assert(h.Downs == 1, "Simultaneous buttons must count as one gesture.");
        Assert(h.Bubbles.SequenceEqual(new[] { "A", "B" }), "Each newly pressed button must get its own bubble.");

        h.Poll(t =>
        {
            t.ObserveStateButton("A", true);
            t.ObserveStateButton("B", true);
            t.ObserveKeystrokeDown("A");
            t.ObserveKeystrokeDown("B");
        });
        Assert(h.Downs == 1 && h.Bubbles.Count == 2, "Held state + queue must not duplicate either button.");

        h.Poll(t => t.ObserveStateButton("A", false));
        Assert(h.Ups == 0, "Releasing one of two buttons must not emit up.");
        Assert(h.Tracker.AnyActive, "The other button must remain active.");
        h.Poll(t => t.ObserveStateButton("B", false));
        Assert(h.Ups == 1, "Releasing the last button must emit one up.");
    }

    static void CheckRapidPresses()
    {
        var h = new Harness();
        for (int i = 0; i < 10; i++)
        {
            h.Poll(t =>
            {
                Assert(t.ObserveKeystroke(0x5800, Kd), "Each rapid tap must produce a new down.");
                Assert(t.ObserveKeystroke(0x5800, Ku), "Each rapid tap must release.");
            });
        }
        Assert(h.Downs == 10, "Ten rapid taps must produce ten downs (no 25ms throttle).");
        Assert(h.Bubbles.Count == 10, "Ten rapid taps must produce ten bubbles.");
    }

    sealed class Harness
    {
        internal readonly PadInputTracker Tracker = new();
        internal readonly List<string> Bubbles = new();
        internal int Downs;
        internal int Ups;
        internal bool LastReleasedAll;

        internal void Poll(Action<PadInputTracker> feed)
        {
            Tracker.BeginPoll();
            feed(Tracker);
            if (Tracker.PressedAny)
            {
                ++Downs;
                Bubbles.AddRange(Tracker.PressedLabels);
            }
            LastReleasedAll = Tracker.ReleasedAll;
            if (LastReleasedAll) ++Ups;
        }
    }
}
