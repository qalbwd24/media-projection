using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneRemote
{
    internal sealed class ConnectionView
    {
        public readonly string Status;
        public readonly string Phone;
        public readonly string Detail;
        public readonly MediaState Media;

        internal ConnectionView(string status, string phone, string detail, MediaState media)
        {
            Status = status;
            Phone = phone;
            Detail = detail;
            Media = media;
        }
    }

    internal sealed class MediaCache
    {
        private FastMediaState latest;
        private uint titleRevision;
        private string title;

        internal MediaState Current { get; private set; }
        internal bool SupportsPush { get { return latest != null; } }
        internal bool NeedsTitle { get { return latest != null && latest.Session && latest.TrackRevision != titleRevision; } }

        internal bool Accept(FastMediaState state)
        {
            if (latest != null && unchecked((int)(state.Revision - latest.Revision)) <= 0) return false;
            latest = state;
            Current = Combine();
            return true;
        }

        internal void ApplyFull(MediaState state)
        {
            if (state.revision != 0 && state.trackRevision != 0)
            {
                titleRevision = state.trackRevision;
                title = state.title ?? "";
                Accept(new FastMediaState(state.revision, state.trackRevision, state.session, state.playing, state.volume, state.min, state.max));
                Current = Combine();
            }
            else if (latest == null) Current = state;
        }

        private MediaState Combine()
        {
            return new MediaState
            {
                v = 1,
                title = NeedsTitle ? null : latest.Session ? title : "",
                session = latest.Session,
                playing = latest.Playing,
                volume = latest.Volume,
                min = latest.Min,
                max = latest.Max,
                revision = latest.Revision,
                trackRevision = latest.TrackRevision
            };
        }
    }

    internal sealed class BleController
    {
        internal sealed class CommandTiming
        {
            public string Action { get; set; }
            public int QueueMs { get; set; }
            public int WriteToAckMs { get; set; }
        }

        private sealed class QueuedCommand
        {
            internal readonly string Action;
            internal readonly long Enqueued;
            internal readonly CancellationToken Hold;
            internal readonly uint Track;
            internal readonly int Position;
            internal QueuedCommand(string action, CancellationToken hold, uint track = 0, int position = 0)
            {
                Action = action;
                Hold = hold;
                Track = track;
                Position = position;
                Enqueued = Stopwatch.GetTimestamp();
            }
        }

        private readonly object sync = new object();
        private readonly SemaphoreSlim wake = new SemaphoreSlim(0, 1);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly Queue<QueuedCommand> pending = new Queue<QueuedCommand>();
        private readonly Task worker;
        private CancellationTokenSource cycle;
        private MediaCache cache;
        private string phone;
        private bool refresh;
        private bool progressRequested;
        private volatile bool supportsProgress;
        private volatile PlaybackProgress progress;
        private int selection;
        private volatile ConnectionView view;
        private volatile CommandTiming lastCommand;
        private long fastPushes;
        private long fullReads;
        private long summaryReads;
        private long progressReads;
        private long connectionFailures;
        private volatile string lastConnectionFailure;

        internal event Action<ConnectionView> Updated;
        internal ConnectionView View { get { return view; } }
        internal CommandTiming LastCommand { get { return lastCommand; } }
        internal long FastPushes { get { return Interlocked.Read(ref fastPushes); } }
        internal long FullReads { get { return Interlocked.Read(ref fullReads); } }
        internal long SummaryReads { get { return Interlocked.Read(ref summaryReads); } }
        internal long ProgressReads { get { return Interlocked.Read(ref progressReads); } }
        internal long ConnectionFailures { get { return Interlocked.Read(ref connectionFailures); } }
        internal string LastConnectionFailure { get { return lastConnectionFailure; } }
        internal bool SupportsProgress { get { return supportsProgress && view.Status == "connected"; } }
        internal PlaybackProgress Progress
        {
            get
            {
                var latest = progress;
                var media = view.Media;
                return latest != null && media != null && media.session && latest.Track == media.trackRevision ? latest : null;
            }
        }

        internal BleController(string phone)
        {
            this.phone = phone;
            view = StartView(phone);
            worker = Task.Run((Func<Task>)Run);
        }

        internal void Reconnect(string target)
        {
            lock (sync)
            {
                phone = target;
                selection++;
                pending.Clear();
                if (cycle != null) cycle.Cancel();
            }
            Publish(StartView(target));
            Wake();
        }

        internal bool Send(string command, CancellationToken hold = default(CancellationToken))
        {
            if (!BleLink.IsCommand(command)) return false;
            lock (sync)
            {
                DiscardReleasedRepeats();
                // Key holds should not leave a backlog of volume steps on a slow link.
                if (view.Status != "connected" || pending.Count >= 5 ||
                    (hold.CanBeCanceled && (hold.IsCancellationRequested || pending.Count != 0))) return false;
                pending.Enqueue(new QueuedCommand(command, hold));
            }
            Wake();
            return true;
        }

        internal async Task Stop()
        {
            stop.Cancel();
            Wake();
            await worker.ConfigureAwait(false);
        }

        internal bool RequestProgress()
        {
            lock (sync)
            {
                if (!SupportsProgress) return false;
                progressRequested = true;
            }
            Wake();
            return true;
        }

        internal bool SendSeek(uint track, int position)
        {
            lock (sync)
            {
                DiscardReleasedRepeats();
                var media = view.Media;
                if (!SupportsProgress || progress == null || !progress.CanSeek || progress.Track != track ||
                    media == null || media.trackRevision != track || position < 0 || position > progress.Duration || pending.Count >= 5)
                    return false;
                pending.Enqueue(new QueuedCommand("seek", CancellationToken.None, track, position));
            }
            Wake();
            return true;
        }

        private async Task Run()
        {
            int retry = 2;
            int previousSelection = -1;
            while (!stop.IsCancellationRequested)
            {
                string target;
                int version;
                CancellationTokenSource attempt;
                lock (sync)
                {
                    target = phone;
                    version = selection;
                    attempt = null;
                    if (!String.IsNullOrWhiteSpace(target))
                    {
                        cycle = attempt = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        refresh = false;
                        progressRequested = false;
                        supportsProgress = false;
                        progress = null;
                        cache = new MediaCache();
                    }
                }
                if (attempt == null)
                {
                    try { await wake.WaitAsync(stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }
                if (version != previousSelection) retry = 2;
                previousSelection = version;
                BleLink link = null;
                Action<FastMediaState> signal = state => OnRevision(attempt, state);
                Action<PlaybackProgress> progressSignal = state => OnProgress(attempt, state);
                try
                {
                    if (retry == 2) Publish(StartView(target));
                    link = await BleLink.Open(target, attempt.Token).ConfigureAwait(false);
                    link.Changed += signal;
                    link.ProgressChanged += progressSignal;
                    link.Disconnected += () =>
                    {
                        lock (sync)
                        {
                            if (cycle == attempt) attempt.Cancel();
                        }
                    };
                    await link.Subscribe(attempt.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref fullReads);
                    var media = await link.ReadState(attempt.Token).ConfigureAwait(false);
                    supportsProgress = link.SupportsProgress;
                    retry = 2;
                    ApplyFull(attempt, target, media);
                    await RunConnected(link, target, attempt).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    if (!stop.IsCancellationRequested && SameSelection(version))
                    {
                        string detail = attempt.IsCancellationRequested ? "蓝牙已断开，正在等待重连。" : error.Message;
                        lastConnectionFailure = detail;
                        Interlocked.Increment(ref connectionFailures);
                        Publish(new ConnectionView("offline", target, detail, null));
                    }
                }
                lock (sync)
                {
                    cycle = null;
                    pending.Clear();
                }
                if (link != null)
                {
                    link.Changed -= signal;
                    link.ProgressChanged -= progressSignal;
                    await link.Unsubscribe().ConfigureAwait(false);
                    link.Dispose();
                }
                attempt.Dispose();

                if (stop.IsCancellationRequested) break;
                if (!SameSelection(version)) continue;
                try { await wake.WaitAsync(TimeSpan.FromSeconds(retry), stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                retry = Math.Min(retry * 2, 45);
            }
        }

        private static ConnectionView StartView(string target)
        {
            return String.IsNullOrWhiteSpace(target)
                ? new ConnectionView("unselected", "", "请先配对手机，再点击下方的「切换手机」。", null)
                : new ConnectionView("connecting", target, "正在连接手机蓝牙…", null);
        }

        private async Task RunConnected(BleLink link, string target, CancellationTokenSource attempt)
        {
            var token = attempt.Token;
            long notBefore = 0;
            long fallbackAt = 0;
            long pushesBeforeCommand = 0;
            long nextToggleAt = 0;
            long progressAt = 0;
            while (!token.IsCancellationRequested)
            {
                QueuedCommand command = null;
                bool readFull = false, readSummary = false, readProgress = false;
                int wait = Timeout.Infinite;
                long now = Stopwatch.GetTimestamp();
                lock (sync)
                {
                    DiscardReleasedRepeats();
                    if (pending.Count > 0)
                    {
                        if (pending.Peek().Action == "play-pause" && now < nextToggleAt)
                            wait = WaitMs(nextToggleAt - now);
                        else command = pending.Dequeue();
                    }
                    else
                    {
                        if (fallbackAt != 0 && fastPushes != pushesBeforeCommand) fallbackAt = 0;
                        bool needsFull = refresh || cache.NeedsTitle;
                        long due = needsFull ? notBefore : fallbackAt;
                        if (needsFull || fallbackAt != 0)
                        {
                            if (now < due) wait = WaitMs(due - now);
                            else
                            {
                                readSummary = !needsFull && cache.SupportsPush && link.CanReadSummary;
                                readFull = !readSummary;
                                refresh = false;
                            }
                        }
                        if (!readFull && !readSummary && progressRequested)
                        {
                            if (now < progressAt)
                                wait = wait == Timeout.Infinite ? WaitMs(progressAt - now) : Math.Min(wait, WaitMs(progressAt - now));
                            else
                            {
                                progressRequested = false;
                                readProgress = true;
                            }
                        }
                    }
                }

                if (command != null)
                {
                    if (command.Hold.IsCancellationRequested) continue;
                    try
                    {
                        long pushesBefore = FastPushes;
                        var delivered = command.Track != 0
                            ? await link.SendSeek(command.Track, command.Position, token).ConfigureAwait(false)
                            : await link.SendCommand(command.Action, token).ConfigureAwait(false);
                        lastCommand = new CommandTiming
                        {
                            Action = command.Action,
                            QueueMs = ElapsedMs(delivered.Started - command.Enqueued),
                            WriteToAckMs = ElapsedMs(delivered.Acknowledged - delivered.Started)
                        };
                        if (command.Track != 0)
                        {
                            progressAt = After(delivered.Acknowledged, 750);
                            lock (sync) progressRequested = true;
                            continue;
                        }
                        pushesBeforeCommand = pushesBefore;
                        // Consecutive toggles allow the Android player to publish the new playback state.
                        if (command.Action == "play-pause") nextToggleAt = After(delivered.Acknowledged, 230);
                        notBefore = After(delivered.Acknowledged, 160);
                        fallbackAt = After(delivered.Acknowledged, 310);
                    }
                    catch (NotSupportedException error)
                    {
                        ConnectionView next;
                        lock (sync)
                        {
                            if (attempt.IsCancellationRequested) return;
                            if (command.Track != 0) progressRequested = true;
                            view = next = new ConnectionView("connected", target, error.Message, cache.Current);
                        }
                        Notify(next);
                    }
                }
                else if (readSummary)
                {
                    Interlocked.Increment(ref summaryReads);
                    var state = await link.ReadSummary(token).ConfigureAwait(false);
                    fallbackAt = 0;
                    if (state != null) ApplyFast(attempt, state, false);
                    else lock (sync) refresh = true;
                }
                else if (readFull)
                {
                    Interlocked.Increment(ref fullReads);
                    var fresh = await link.ReadState(token, HasWaitingCommand).ConfigureAwait(false);
                    if (fresh == null)
                    {
                        lock (sync) refresh = true;
                    }
                    else
                    {
                        fallbackAt = 0;
                        ApplyFull(attempt, target, fresh);
                    }
                }
                else if (readProgress)
                {
                    var last = progress;
                    var updated = await link.ReadProgress(token).ConfigureAwait(false);
                    lock (sync)
                    {
                        if (cycle != attempt || attempt.IsCancellationRequested) return;
                        if (updated != null && ReferenceEquals(progress, last)) progress = updated;
                        Interlocked.Increment(ref progressReads);
                    }
                    Notify(view);
                }
                else await wake.WaitAsync(wait, token).ConfigureAwait(false);
            }
        }

        private void OnProgress(CancellationTokenSource attempt, PlaybackProgress state)
        {
            lock (sync)
            {
                if (cycle != attempt || attempt.IsCancellationRequested) return;
                progress = state;
            }
            Notify(view);
        }

        private bool HasWaitingCommand()
        {
            lock (sync)
            {
                DiscardReleasedRepeats();
                return pending.Count != 0;
            }
        }

        private void DiscardReleasedRepeats()
        {
            while (pending.Count > 0 && pending.Peek().Hold.IsCancellationRequested) pending.Dequeue();
        }

        private static long After(long tick, int milliseconds) { return tick + Stopwatch.Frequency * milliseconds / 1000; }
        private static int ElapsedMs(long ticks) { return (int)(ticks * 1000 / Stopwatch.Frequency); }
        private static int WaitMs(long ticks) { return Math.Max(1, ElapsedMs(ticks)); }

        private bool SameSelection(int version)
        {
            lock (sync) return version == selection;
        }

        private void ApplyFull(CancellationTokenSource attempt, string target, MediaState state)
        {
            ConnectionView next;
            lock (sync)
            {
                if (cycle != attempt || attempt.IsCancellationRequested) return;
                cache.ApplyFull(state);
                view = next = new ConnectionView("connected", target, "", cache.Current);
            }
            Notify(next);
        }

        private void OnRevision(CancellationTokenSource attempt, FastMediaState state)
        {
            if (state != null)
            {
                ApplyFast(attempt, state, true);
                return;
            }
            lock (sync)
            {
                if (cycle != attempt || attempt.IsCancellationRequested) return;
                refresh = true;
            }
            Wake();
        }

        private void ApplyFast(CancellationTokenSource attempt, FastMediaState state, bool fromPush)
        {
            ConnectionView next = null;
            lock (sync)
            {
                if (cycle != attempt || attempt.IsCancellationRequested || !cache.Accept(state)) return;
                if (fromPush) Interlocked.Increment(ref fastPushes);
                if (view.Status == "connected") view = next = new ConnectionView("connected", view.Phone, "", cache.Current);
            }
            if (next != null) Notify(next);
            Wake();
        }

        private void Wake()
        {
            try { wake.Release(); } catch (SemaphoreFullException) { }
        }

        private void Publish(ConnectionView next)
        {
            view = next;
            Notify(next);
        }

        private void Notify(ConnectionView next)
        {
            var updated = Updated;
            if (updated != null) updated(next);
        }
    }
}
