using System.Collections;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using WuwatappoNative.Assets;
using WuwatappoNative.Net;
using WuwatappoNative.Pet;

internal static partial class Program
{
    static int _checks;

    [STAThread]
    static int Main()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Console.WriteLine("Isolated checks: no app startup, login/config access, networking, input hooks, or visible windows.");
            CheckMainCache();
            CheckRemoteCache();
            CheckSprites();
            CheckBubbles();
            CheckClockLifecycle();
            MeasureClocks();
            Benchmark();
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

    static Bitmap Art(Color color, bool circle = false)
    {
        var image = new Bitmap(200, 200, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        if (circle) g.FillEllipse(brush, 10, 20, 173, 176);
        else g.FillRectangle(brush, 5, 6, 190, 190);
        return image;
    }

    static byte[] Pixels(Bitmap image)
    {
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[image.Width * image.Height * 4];
            for (int y = 0; y < image.Height; ++y)
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * image.Width * 4, image.Width * 4);
            return bytes;
        }
        finally { image.UnlockBits(data); }
    }

    static void SamePixels(Bitmap expected, Bitmap actual, string scenario, int tolerance = 0)
    {
        var a = Pixels(expected);
        var b = Pixels(actual);
        int max = 0, changed = 0;
        for (int i = 0; i < a.Length; ++i)
        {
            int diff = Math.Abs(a[i] - b[i]);
            if (diff > 0) ++changed;
            max = Math.Max(max, diff);
        }
        Assert(max <= tolerance, $"{scenario}: maximum channel difference {max}, {changed} changed bytes; tolerance {tolerance}.");
    }

    static void CheckMainCache()
    {
        using var head = Art(Color.FromArgb(190, 70, 130, 240), true);
        using var body = Art(Color.FromArgb(180, 200, 70, 50));
        using var other = Art(Color.FromArgb(160, 50, 210, 80), true);
        using var acc = Art(Color.FromArgb(110, 190, 80, 220), true);
        var frames = new PetFrames { Head1 = head, Head2 = other, Body1 = body, Body2 = other, Body3 = head, Accessory = acc };
        var info = new PetComposer.Info { Clicks = 123456, KeyboardOn = true };
        using var cache = new PetComposer.FrameCache();
        foreach (var size in new[] { new Size(272, 331), new Size(340, 414), new Size(544, 662), new Size(68, 83) })
        {
            using var expected = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using var actual = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            void Verify(string label, int bodyFrame = 1, int headFrame = 1)
            {
                PetComposer.ComposeInto(expected, frames, bodyFrame, headFrame, info);
                PetComposer.ComposeInto(actual, frames, bodyFrame, headFrame, info, cache);
                // Separately composited premultiplied-alpha layers may round by one channel value.
                SamePixels(expected, actual, $"main {size}: {label}", 2);
            }
            Verify("first frame");
            Verify("cache hit");
            info.Clicks++; Verify("counter invalidation");
            info.KeyboardOn = !info.KeyboardOn; Verify("keyboard invalidation");
            Verify("body 2 / blink", 2, 2);
            Verify("body 3", 3);
            frames.Head1 = other; Verify("downloaded head replacement");
            frames.Body1 = head; Verify("downloaded body replacement");
            frames.AccOffsetX += 7.5; frames.AccOffsetY -= 3.25; Verify("asset accessory offset");
            info.AccOffsetX -= 20; info.AccOffsetY += 30; Verify("user accessory offset");
            frames.Accessory = null; Verify("remove accessory");
            frames.Accessory = acc; Verify("replace accessory");
            info.ShowChestLeft = true; info.ChestLeftTs = 100_000; info.NowMs = 10_000; Verify("countdown layer");
            info.NowMs = 100_000; Verify("claimable chest");
            info.Loading = true; info.LoadingPhase = 0.25; Verify("loading layer");
            info.Loading = false;
            var wheel = new EmoteWheelLayer();
            wheel.Show(new long[] { 1, 2, 3, 4, 5 }, _ => other);
            Field(wheel, "_openedAt").SetValue(wheel, DateTime.UtcNow.AddSeconds(-2));
            info.Wheel = wheel; Verify("wheel below strip");
            info.Wheel = null;
            var bubbles = new EmoteBubbleLayer();
            bubbles.Add(other); FreezeBubbles(bubbles, 0, 140);
            info.Bubbles = bubbles; Verify("bubble below strip");
            info.Bubbles = null;
            bubbles.Clear();
            using var keys = new KeyBubbleLayer();
            keys.Add(65); FreezeBubbles(keys, 0, 150);
            info.KeyBubbles = keys; Verify("key bubble below strip");
            info.KeyBubbles = null;
        }
        cache.Dispose();
        using var afterDispose = new Bitmap(272, 331, PixelFormat.Format32bppPArgb);
        PetComposer.ComposeInto(afterDispose, frames, 1, 1, info, cache);
        Console.WriteLine("PASS: main cache matches uncached rendering across scale, frames, HUD, offsets, and overlay ordering.");
    }

    static FieldInfo Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    static void FreezeBubbles(object layer, float? offsetX = null, float? offsetY = null)
    {
        var items = (IList)Field(layer, "_items").GetValue(layer)!;
        foreach (var item in items)
        {
            var type = item.GetType();
            type.GetField("Start")!.SetValue(item, AnimationTimer.NowMs() + 60_000);
            if (offsetX.HasValue) type.GetField("OffsetX")!.SetValue(item, offsetX.Value);
            if (offsetY.HasValue) type.GetField("OffsetY")!.SetValue(item, offsetY.Value);
        }
    }

    static void CheckRemoteCache()
    {
        using var store = new AssetStore();
        var catalog = new Catalog();
        var loader = new PetImageLoader(catalog, store);
        var member = new RoomMember { Nickname = "Animation test" };
        using var window = new RemoteCatWindow(member, loader, catalog, store);
        using var head = Art(Color.FromArgb(180, 70, 130, 240), true);
        using var body = Art(Color.FromArgb(180, 200, 70, 50));
        using var acc = Art(Color.FromArgb(110, 190, 80, 220), true);
        var frames = new PetFrames { Head1 = head, Body1 = body, Body2 = head, Body3 = acc, Accessory = acc };
        Field(window, "_frames").SetValue(window, frames);
        foreach (float scale in new[] { 1f, 1.25f, 2f, 0.25f })
        {
            Field(window, "_scale").SetValue(window, scale);
            using var actual = new Bitmap((int)(200 * scale), (int)(341 * scale), PixelFormat.Format32bppPArgb);
            using var expected = new Bitmap(actual.Width, actual.Height, PixelFormat.Format32bppPArgb);
            void Verify(string label)
            {
                window.ComposeInto(actual);
                var bubbles = (EmoteBubbleLayer)Field(window, "_bubbles").GetValue(window)!;
                DrawOriginalRemote(expected, frames, (int)Field(window, "_bodyFrame").GetValue(window)!,
                    scale, (double)Field(window, "_remoteNameScale").GetValue(window)!, member, bubbles);
                SamePixels(expected, actual, $"remote {scale}: {label}", 2);
            }
            Verify("first frame");
            var cached = Field(window, "_baseFrame").GetValue(window);
            Verify("cache hit");
            Assert(ReferenceEquals(cached, Field(window, "_baseFrame").GetValue(window)), "Remote cache should reuse its bitmap.");
            member.Nickname += "X"; Verify("nickname");
            Field(window, "_remoteNameScale").SetValue(window, 1.7); Verify("name size");
            Field(window, "_bodyFrame").SetValue(window, 2); Verify("hand frame");
            Field(window, "_bodyFrame").SetValue(window, 3); Verify("alternate hand");
            frames.Head1 = body; Verify("head replacement");
            frames.Body3 = head; Verify("body replacement");
            frames.AccOffsetX += 11; frames.AccOffsetY -= 7; Verify("accessory offsets");
            frames.Accessory = null; Verify("accessory removal");
            var bubbles = (EmoteBubbleLayer)Field(window, "_bubbles").GetValue(window)!;
            bubbles.Add(acc); FreezeBubbles(bubbles); Verify("bubble above nickname");
            bubbles.Clear();
        }
        // Create a hidden handle and force the queued path, rather than letting Request return early.
        _ = window.Handle;
        var lastPaint = typeof(CatPainter).GetField("_lastPaintMs", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousPaint = lastPaint.GetValue(null);
        try
        {
            lastPaint.SetValue(null, Environment.TickCount64 + 60_000);
            CatPainter.Request(window);
            CatPainter.Request(window);
            CatPainter.Request(window);
        }
        finally { lastPaint.SetValue(null, previousPaint); }
        var painterTimer = (AnimationTimer)typeof(CatPainter).GetField("Timer", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var queue = (IList)typeof(CatPainter).GetField("Queue", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert(painterTimer.Enabled && queue.Count == 1, "Repeated requests must queue one window and start the painter clock.");
        RemoteEmoteAnimator.Add(window);
        var emoteTimer = (AnimationTimer)typeof(RemoteEmoteAnimator).GetField("Timer", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert(emoteTimer.Enabled, "Adding a remote emote must start its animation clock.");
        window.Close();
        Assert(!painterTimer.Enabled && queue.Count == 0, "Closing the last queued remote window must clear its queue and stop its painter clock.");
        Assert(!emoteTimer.Enabled, "Closing the last animating remote window must stop its emote clock.");
        Console.WriteLine("PASS: remote cache invalidates correctly and retains bubble layering.");
    }

    static void Expire(object layer)
    {
        var items = (IList)Field(layer, "_items").GetValue(layer)!;
        foreach (var item in items) item.GetType().GetField("Start")!.SetValue(item, AnimationTimer.NowMs() - 1001);
    }

    static void CheckBubbles()
    {
        using var image = Art(Color.Red);
        var bubbles = new EmoteBubbleLayer(3);
        for (int i = 0; i < 12; ++i) bubbles.Add(image);
        Assert(bubbles.Count == 3, "Emote cap changed.");
        Assert(bubbles.Tick(), "New emotes must stay active.");
        Expire(bubbles); Assert(!bubbles.Tick() && bubbles.Count == 0, "Expired emotes must leave a clean final frame.");
        using var keys = new KeyBubbleLayer(3);
        for (int i = 0; i < 12; ++i) keys.Add(65 + i);
        Assert(keys.Count == 3 && keys.Tick(), "Keyboard bubble cap or lifetime changed.");
        keys.AddLabel("A"); keys.AddLabel("LB", true);
        Assert(keys.Count == 3, "Gamepad cap changed.");
        Expire(keys); Assert(!keys.Tick() && keys.Count == 0, "Expired keyboard/gamepad bubbles must be removed.");
        Console.WriteLine("PASS: emote, keyboard, and gamepad lifetimes and caps.");
    }

    sealed class CountingContext(SynchronizationContext inner) : SynchronizationContext
    {
        int _pending;
        public int MaxPending;
        public override void Post(SendOrPostCallback callback, object state)
        {
            int pending = Interlocked.Increment(ref _pending);
            MaxPending = Math.Max(MaxPending, pending);
            inner.Post(_ => { Interlocked.Decrement(ref _pending); callback(state); }, null);
        }
    }

    static void CheckClockLifecycle()
    {
        int thread = Environment.CurrentManagedThreadId, ticks = 0;
        var original = SynchronizationContext.Current!;
        var context = new CountingContext(original);
        SynchronizationContext.SetSynchronizationContext(context);
        using var timer = new AnimationTimer();
        SynchronizationContext.SetSynchronizationContext(original);
        using var app = new ApplicationContext();
        using var finish = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            Assert(Environment.CurrentManagedThreadId == thread, "Animation tick must execute on UI thread.");
            ++ticks;
            if (ticks == 1)
            {
                Thread.Sleep(180); // Background may queue only one update while UI is busy.
                timer.Stop();
                finish.Start();
            }
        };
        finish.Tick += (_, _) => { finish.Stop(); app.ExitThread(); };
        timer.Start(); timer.Start();
        Application.Run(app);
        Assert(ticks == 1, "A pending tick ran after Stop.");
        Assert(context.MaxPending == 1, "Animation clock accumulated UI notifications.");
        Assert(!timer.Enabled, "Stopped timer must be disabled.");
        timer.Tick += (_, _) => { timer.Stop(); app.ExitThread(); };
        finish.Tick += (_, _) => app.ExitThread();
        timer.Start();
        Application.Run(app);
        Assert(ticks == 2, "Timer must restart without stale ticks.");
        timer.Dispose();
        Assert(!timer.Enabled, "Disposed timer must be stopped.");
        // 动画进度时钟需要毫秒级分辨率；TickCount64 约 15.6ms 才跳一次，200ms 内只有十几个值。
        var distinct = new HashSet<long>();
        long last = AnimationTimer.NowMs();
        bool monotonic = true;
        var probe = Stopwatch.StartNew();
        while (probe.ElapsedMilliseconds < 200)
        {
            long now = AnimationTimer.NowMs();
            monotonic &= now >= last;
            last = now;
            distinct.Add(now);
        }
        Assert(monotonic, "Animation progress clock must be monotonic.");
        Assert(distinct.Count >= 150, $"Animation progress clock is too coarse: {distinct.Count} values in 200ms.");
        Console.WriteLine("PASS: high-resolution UI timer stop/restart/dispose, bounded pending queue, and millisecond progress clock.");
    }

    static double MeasureClock(string label, bool precise, int interval)
    {
        using var app = new ApplicationContext();
        using var timer = precise ? null : new System.Windows.Forms.Timer { Interval = interval };
        using var animation = precise ? new AnimationTimer() : null;
        using var end = new System.Windows.Forms.Timer { Interval = 50 };
        var watch = Stopwatch.StartNew();
        var samples = new List<double>();
        double previous = 0;
        EventHandler tick = (_, _) => { double now = watch.Elapsed.TotalMilliseconds; if (previous > 0) samples.Add(now - previous); previous = now; };
        if (precise) { animation!.Tick += tick; animation.Start(); }
        else { timer!.Tick += tick; timer.Start(); }
        end.Tick += (_, _) => { if (watch.ElapsedMilliseconds < 3000) return; end.Stop(); timer?.Stop(); animation?.Stop(); app.ExitThread(); };
        end.Start(); Application.Run(app);
        double rate = (samples.Count + 1) / watch.Elapsed.TotalSeconds;
        samples.Sort();
        Console.WriteLine($"{label}: {rate:F2}/s, median={samples[samples.Count / 2]:F2}ms, p95={samples[(int)(samples.Count * 0.95)]:F2}ms");
        return rate;
    }

    static void MeasureClocks()
    {
        MeasureClock("Original WinForms 50ms", false, 50);
        MeasureClock("WinForms 16ms", false, 16);
        MeasureClock("New AnimationTimer 16ms", true, 16);
    }

    static void Benchmark()
    {
        using var store = new AssetStore();
        var frames = new PetImageLoader(new Catalog(), store).Get(0, 0);
        Assert(frames.Ready, "Bundled default frames missing.");
        using var emote = Art(Color.FromArgb(180, 210, 80, 50), true);
        foreach (int scale in new[] { 1, 2 })
        foreach (int count in new[] { 0, 10 })
        {
            var bubbles = new EmoteBubbleLayer(10);
            for (int i = 0; i < count; ++i) bubbles.Add(emote);
            var info = new PetComposer.Info { Bubbles = bubbles, Clicks = 123456, KeyboardOn = true };
            using var image = new Bitmap(272 * scale, 331 * scale, PixelFormat.Format32bppPArgb);
            using var cache = new PetComposer.FrameCache();
            double Run(bool cached)
            {
                info.Bubbles = cached ? bubbles : null;
                void Draw(int frame)
                {
                    SetProgress(bubbles, (frame % 60) / 60.0);
                    if (cached) PetComposer.ComposeInto(image, frames, 1, 1, info, cache);
                    else
                    {
                        PetComposer.ComposeInto(image, frames, 1, 1, info);
                        using var g = Graphics.FromImage(image);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        DrawOriginalEmotes(g, bubbles, scale, 124 * scale, 220 * scale);
                    }
                }
                for (int i = 0; i < 5; ++i) Draw(i);
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 120; ++i) Draw(i);
                return watch.Elapsed.TotalMilliseconds / 120;
            }
            double original = Run(false), updated = Run(true);
            Console.WriteLine($"Compose {image.Width}x{image.Height}, emotes={count}: original-reference={original:F3}ms, cached={updated:F3}ms, reduction={(1 - updated / original) * 100:F1}% (excludes ULW/DWM/game)");
            bubbles.Clear();
        }
    }
}
