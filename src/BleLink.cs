using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace PhoneRemote
{
    internal struct CommandDispatch
    {
        internal readonly long Started;
        internal readonly long Acknowledged;

        internal CommandDispatch(long started, long acknowledged)
        {
            Started = started;
            Acknowledged = acknowledged;
        }
    }

    internal sealed class MediaState
    {
        public int v { get; set; }
        public string title { get; set; }
        public bool session { get; set; }
        public bool playing { get; set; }
        public int volume { get; set; }
        public int min { get; set; }
        public int max { get; set; }
        public uint revision { get; set; }
        public uint trackRevision { get; set; }
    }

    internal sealed class FastMediaState
    {
        internal readonly uint Revision;
        internal readonly uint TrackRevision;
        internal readonly bool Session;
        internal readonly bool Playing;
        internal readonly int Volume;
        internal readonly int Min;
        internal readonly int Max;

        internal FastMediaState(uint revision, uint track, bool session, bool playing, int volume, int min, int max)
        {
            Revision = revision;
            TrackRevision = track;
            Session = session;
            Playing = playing;
            Volume = volume;
            Min = min;
            Max = max;
        }

        internal static FastMediaState Decode(byte[] data)
        {
            if (data.Length != 13 || data[8] != 0xD1 || (data[9] & ~3) != 0 || data[10] >= data[11] ||
                data[11] > 100 || data[12] < data[10] || data[12] > data[11]) return null;
            uint revision = BitConverter.ToUInt32(data, 0);
            uint track = BitConverter.ToUInt32(data, 4);
            bool session = (data[9] & 1) != 0, playing = (data[9] & 2) != 0;
            return revision != 0 && track != 0 && (session || !playing)
                ? new FastMediaState(revision, track, session, playing, data[12], data[10], data[11]) : null;
        }
    }

    internal sealed class PlaybackProgress
    {
        public uint Track { get; private set; }
        public int Position { get; private set; }
        public int Duration { get; private set; }
        public float Speed { get; private set; }
        public bool CanSeek { get; private set; }
        private readonly long sampledAt = Stopwatch.GetTimestamp();

        internal static PlaybackProgress Decode(byte[] data)
        {
            if (data.Length != 17 || data[16] > 1) return null;
            uint track = BitConverter.ToUInt32(data, 0);
            int position = BitConverter.ToInt32(data, 4), duration = BitConverter.ToInt32(data, 8);
            float speed = BitConverter.ToSingle(data, 12);
            if (track == 0 || position < -1 || duration < 0 || float.IsNaN(speed) || float.IsInfinity(speed)) return null;
            return new PlaybackProgress { Track = track, Position = position, Duration = duration, Speed = speed,
                CanSeek = data[16] == 1 && duration > 0 };
        }

        internal int CurrentPosition(bool playing)
        {
            if (Position < 0) return 0;
            double elapsed = (Stopwatch.GetTimestamp() - sampledAt) * (1000.0 / Stopwatch.Frequency);
            return (int)Math.Max(0, Math.Min(Duration, Position + (playing ? elapsed * Speed : 0)));
        }
    }

    internal sealed class BleLink : IDisposable
    {
        private static readonly Guid ServiceId = CharacteristicId("007");
        private static readonly Guid StateId = CharacteristicId("008");
        private static readonly Guid CommandId = CharacteristicId("009");
        private static readonly Guid RevisionId = CharacteristicId("010");
        private static readonly Guid PageId = CharacteristicId("011");
        private static readonly Guid ProgressId = CharacteristicId("012");
        private const int PageSize = 20;
        private static readonly Dictionary<string, byte> Commands = new Dictionary<string, byte>
        {
            {"play-pause", 1}, {"previous", 2}, {"next", 3}, {"volume-down", 4}, {"volume-up", 5}
        };

        private readonly BluetoothLEDevice phone;
        private readonly GattDeviceService service;
        private readonly GattSession session;
        private GattCharacteristic state;
        private GattCharacteristic command;
        private GattCharacteristic revision;
        private GattCharacteristic page;
        private GattCharacteristic progress;
        private bool subscribed;
        private bool progressSubscribed;

        internal event Action<FastMediaState> Changed;
        internal event Action<PlaybackProgress> ProgressChanged;
        internal event Action Disconnected;
        internal bool CanReadSummary { get { return (revision.CharacteristicProperties & GattCharacteristicProperties.Read) != 0; } }
        internal bool SupportsProgress { get { return progress != null; } }

        private BleLink(BluetoothLEDevice phone, GattDeviceService service)
        {
            this.phone = phone;
            this.service = service;
            session = service.Session;
        }

        private static Guid CharacteristicId(string suffix)
        {
            return new Guid("c00fe8e2-30ac-41d2-802d-86309fbbb" + suffix);
        }

        internal static bool IsCommand(string action) { return Commands.ContainsKey(action); }

        private static Task<DeviceInformationCollection> PairedDevices(CancellationToken stop)
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            return Receive(DeviceInformation.FindAllAsync(selector), stop);
        }

        internal static async Task<string[]> PairedNames()
        {
            var devices = await PairedDevices(CancellationToken.None).ConfigureAwait(false);
            return devices.Select(d => d.Name).Where(n => !String.IsNullOrWhiteSpace(n)).Distinct().OrderBy(n => n).ToArray();
        }

        internal static async Task<BleLink> Open(string phoneName, CancellationToken stop)
        {
            var devices = await PairedDevices(stop).ConfigureAwait(false);
            var matches = devices.Where(d => String.Equals(d.Name, phoneName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new IOException(matches.Length == 0 ? "找不到已配对的手机。" : "有多个同名蓝牙设备，请先在 Windows 中区分名称。");

            var phone = await Receive(BluetoothLEDevice.FromIdAsync(matches[0].Id), stop).ConfigureAwait(false);
            if (phone == null) throw new IOException("Windows 蓝牙暂时无法打开这台手机。");
            BleLink link = null;
            try
            {
                var services = await Receive(phone.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached), stop).ConfigureAwait(false);
                Check(services.Status, services.ProtocolError, "连接手机");
                if (services.Services.Count != 1) throw new IOException("手机暂未提供媒体服务。请确认蓝牙和手机端服务已开启。");
                link = new BleLink(phone, services.Services[0]);
                var fields = await Receive(link.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached), stop).ConfigureAwait(false);
                Check(fields.Status, fields.ProtocolError, "获取媒体服务");
                link.state = fields.Characteristics.FirstOrDefault(c => c.Uuid == StateId);
                link.command = fields.Characteristics.FirstOrDefault(c => c.Uuid == CommandId);
                link.page = fields.Characteristics.FirstOrDefault(c => c.Uuid == PageId);
                link.revision = fields.Characteristics.FirstOrDefault(c => c.Uuid == RevisionId);
                link.progress = fields.Characteristics.FirstOrDefault(c => c.Uuid == ProgressId);
                if (link.state == null || link.command == null || link.page == null || link.revision == null)
                    throw new IOException("手机端媒体服务版本需要更新。");
                return link;
            }
            catch
            {
                if (link != null) link.Dispose();
                else phone.Dispose();
                throw;
            }
        }

        internal async Task Subscribe(CancellationToken stop)
        {
            phone.ConnectionStatusChanged += OnPhoneConnection;
            session.SessionStatusChanged += OnSessionStatus;
            revision.ValueChanged += OnRevision;
            var status = await Receive(revision.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify), stop).ConfigureAwait(false);
            Check(status, null, "订阅手机媒体状态");
            subscribed = true;
            session.MaintainConnection = true;
            if (progress != null && (progress.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0)
            {
                progress.ValueChanged += OnProgress;
                try
                {
                    var progressStatus = await Receive(progress.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify), stop, 1800).ConfigureAwait(false);
                    progressSubscribed = progressStatus == GattCommunicationStatus.Success;
                }
                catch (TimeoutException) { }
                catch (COMException) { }
            }
        }

        private void OnProgress(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var handler = ProgressChanged;
            var state = PlaybackProgress.Decode(ReadBytes(args.CharacteristicValue));
            if (handler != null && state != null) handler(state);
        }

        private void OnRevision(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var changed = Changed;
            if (changed == null) return;
            byte[] data = ReadBytes(args.CharacteristicValue);
            if (data.Length == 4) changed(null);
            else
            {
                var state = FastMediaState.Decode(data);
                if (state != null) changed(state);
            }
        }

        internal Task<FastMediaState> ReadSummary(CancellationToken stop)
        {
            return WithLink(async () =>
            {
                var result = await Receive(revision.ReadValueAsync(BluetoothCacheMode.Uncached), stop).ConfigureAwait(false);
                Check(result.Status, result.ProtocolError, "读取音量和播放状态");
                return FastMediaState.Decode(ReadBytes(result.Value));
            }, stop);
        }

        private static byte[] ReadBytes(IBuffer buffer)
        {
            using (var reader = DataReader.FromBuffer(buffer))
            {
                var data = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(data);
                return data;
            }
        }

        private void OnPhoneConnection(BluetoothLEDevice sender, object unused)
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) NotifyDisconnected();
        }

        private void OnSessionStatus(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed) NotifyDisconnected();
        }

        private void NotifyDisconnected()
        {
            var disconnected = Disconnected;
            if (disconnected != null) disconnected();
        }

        internal async Task<MediaState> Query(string action, CancellationToken stop)
        {
            if (action != null)
            {
                await SendCommand(action, stop).ConfigureAwait(false);
                await Task.Delay(220, stop).ConfigureAwait(false);
            }
            return await ReadState(stop).ConfigureAwait(false);
        }

        internal Task<CommandDispatch> SendCommand(string action, CancellationToken stop)
        {
            return Send(new[] { Commands[action] }, stop);
        }

        internal Task<CommandDispatch> SendSeek(uint track, int position, CancellationToken stop)
        {
            var payload = new byte[9];
            payload[0] = 6;
            BitConverter.GetBytes(track).CopyTo(payload, 1);
            BitConverter.GetBytes(position).CopyTo(payload, 5);
            return Send(payload, stop);
        }

        private Task<CommandDispatch> Send(byte[] payload, CancellationToken stop)
        {
            return WithLink(async () =>
            {
                long began = Stopwatch.GetTimestamp();
                await Write(command, payload, stop).ConfigureAwait(false);
                return new CommandDispatch(began, Stopwatch.GetTimestamp());
            }, stop);
        }

        internal Task<PlaybackProgress> ReadProgress(CancellationToken stop)
        {
            return WithLink(async () =>
            {
                try
                {
                    var read = await Receive(progress.ReadValueAsync(BluetoothCacheMode.Uncached), stop, 1800).ConfigureAwait(false);
                    return read.Status == GattCommunicationStatus.Success ? PlaybackProgress.Decode(ReadBytes(read.Value)) : null;
                }
                catch (TimeoutException) { return null; }
                catch (COMException) { return null; }
            }, stop);
        }

        internal Task<MediaState> ReadState(CancellationToken stop, Func<bool> yieldToCommand = null)
        {
            return WithLink(() => ReadSnapshot(stop, yieldToCommand), stop);
        }

        private static Task<T> WithLink<T>(Func<Task<T>> operation, CancellationToken stop)
        {
            // A snapshot spans several ATT operations. Serialize it with the diagnostic CLI on this PC.
            return Task.Run(() =>
            {
                using (var gate = new Mutex(false, @"Local\PhoneMediaBLE"))
                {
                    bool acquired;
                    try { acquired = WaitHandle.WaitAny(new WaitHandle[] { gate, stop.WaitHandle }, 6000) == 0; }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                    {
                        stop.ThrowIfCancellationRequested();
                        throw new IOException("另一个媒体请求仍在处理中。");
                    }
                    try { return operation().GetAwaiter().GetResult(); }
                    finally { gate.ReleaseMutex(); }
                }
            }, stop);
        }

        private async Task<MediaState> ReadSnapshot(CancellationToken stop, Func<bool> yieldToCommand)
        {
            using (var snapshot = new MemoryStream(514))
            {
                byte index = 0;
                int expected = 2;
                do
                {
                    if (yieldToCommand != null && yieldToCommand()) return null;
                    await Write(page, index, stop).ConfigureAwait(false);
                    var read = await Receive(state.ReadValueAsync(BluetoothCacheMode.Uncached), stop).ConfigureAwait(false);
                    Check(read.Status, read.ProtocolError, "读取媒体状态");
                    using (var reader = DataReader.FromBuffer(read.Value))
                    {
                        var data = new byte[reader.UnconsumedBufferLength];
                        reader.ReadBytes(data);
                        if (index == 0)
                        {
                            if (data.Length < 2) throw new InvalidDataException("手机返回了不完整的媒体状态。");
                            expected += BitConverter.ToUInt16(data, 0);
                            if (expected <= 2 || expected > 514) throw new InvalidDataException("媒体状态长度无效。");
                        }
                        if (data.Length != Math.Min(PageSize, expected - snapshot.Length))
                            throw new InvalidDataException("手机返回了不完整的媒体状态。");
                        snapshot.Write(data, 0, data.Length);
                    }
                    index++;
                }
                while (snapshot.Length < expected);
                var json = new UTF8Encoding(false, true).GetString(snapshot.ToArray(), 2, expected - 2);
                var media = new JavaScriptSerializer().Deserialize<MediaState>(json);
                if (media == null || media.v != 1 || media.min < 0 || media.max <= media.min || media.max > 100 ||
                    media.volume < media.min || media.volume > media.max)
                    throw new InvalidDataException("手机媒体状态格式无效。");
                return media;
            }
        }

        private static Task Write(GattCharacteristic characteristic, byte value, CancellationToken stop)
        {
            return Write(characteristic, new[] { value }, stop);
        }

        private static async Task Write(GattCharacteristic characteristic, byte[] value, CancellationToken stop)
        {
            using (var writer = new DataWriter())
            {
                writer.WriteBytes(value);
                var result = await Receive(characteristic.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse), stop)
                    .ConfigureAwait(false);
                Check(result.Status, result.ProtocolError, "发送蓝牙请求");
            }
        }

        private static void Check(GattCommunicationStatus status, byte? error, string operation)
        {
            if (status == GattCommunicationStatus.Success) return;
            if (status == GattCommunicationStatus.ProtocolError && error == 6)
                throw new NotSupportedException("当前手机播放器未接收这项操作。");
            var detail = error.HasValue ? status + " · ATT " + error.Value : status.ToString();
            throw new IOException(operation + "失败（" + detail + "）。");
        }

        private static async Task<T> Receive<T>(IAsyncOperation<T> operation, CancellationToken stop, int milliseconds = 12000)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.Completed = (source, status) =>
            {
                if (status == AsyncStatus.Canceled) completion.TrySetCanceled();
                else
                {
                    try { completion.TrySetResult(source.GetResults()); }
                    catch (Exception error) { completion.TrySetException(error); }
                }
            };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                if (await Task.WhenAny(completion.Task, Task.Delay(milliseconds, timeout.Token)).ConfigureAwait(false) != completion.Task)
                {
                    try { operation.Cancel(); } catch (Exception) { }
                    stop.ThrowIfCancellationRequested();
                    throw new TimeoutException("等待手机蓝牙响应超时。");
                }
                timeout.Cancel();
                return await completion.Task.ConfigureAwait(false);
            }
        }

        internal async Task Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            using (var timeout = new CancellationTokenSource(700))
            {
                try
                {
                    if (progressSubscribed)
                    {
                        await Receive(progress.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None), timeout.Token).ConfigureAwait(false);
                    }
                    await Receive(revision.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            if (revision != null) revision.ValueChanged -= OnRevision;
            if (progress != null) progress.ValueChanged -= OnProgress;
            if (session != null)
            {
                session.SessionStatusChanged -= OnSessionStatus;
                try { session.MaintainConnection = false; } catch (Exception) { }
                session.Dispose();
            }
            phone.ConnectionStatusChanged -= OnPhoneConnection;
            service.Dispose();
            phone.Dispose();
        }
    }
}
