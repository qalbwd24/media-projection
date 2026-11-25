using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneRemote
{
    internal sealed class LocalControl : IDisposable
    {
        private static readonly SecurityIdentifier User = WindowsIdentity.GetCurrent().User;
        internal static readonly string Name = "MediaProjectionTool." + User.Value;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly Func<string, Task<string>> action;

        internal LocalControl(Func<string, Task<string>> action)
        {
            this.action = action;
            Task.Run((Func<Task>)Listen);
        }

        internal static bool Nudge()
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut))
                {
                    pipe.Connect(2500);
                    var bytes = Utf8.GetBytes("show\n");
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                }
                return true;
            }
            catch (TimeoutException) { return false; }
            catch (IOException) { return false; }
        }

        private async Task Listen()
        {
            var access = new PipeSecurity();
            access.SetAccessRuleProtection(true, false);
            access.AddAccessRule(new PipeAccessRule(User, PipeAccessRights.FullControl, AccessControlType.Allow));
            var token = stop.Token;
            while (!token.IsCancellationRequested)
            {
                bool retry = false;
                try
                {
                    using (var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 512, 1024, access))
                    {
                        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            limit.CancelAfter(3000);
                            string request = await ReadRequest(pipe, limit.Token).ConfigureAwait(false);
                            string response = await action(request).ConfigureAwait(false);
                            var bytes = Utf8.GetBytes(response + "\n");
                            await pipe.WriteAsync(bytes, 0, bytes.Length, limit.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { retry = true; }
                catch (DecoderFallbackException) { retry = true; }
                if (retry && !token.IsCancellationRequested) await Task.Delay(150).ConfigureAwait(false);
            }
        }

        private static async Task<string> ReadRequest(Stream stream, CancellationToken stop)
        {
            var buffer = new byte[128];
            int used = 0;
            while (used < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer, used, buffer.Length - used, stop).ConfigureAwait(false);
                if (count == 0) break;
                int lineEnd = Array.IndexOf(buffer, (byte)'\n', used, count);
                if (lineEnd >= 0) return Utf8.GetString(buffer, 0, lineEnd).Trim();
                used += count;
            }
            return "";
        }

        public void Dispose() { stop.Cancel(); }
    }
}
