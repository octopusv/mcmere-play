using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Mcmere.Play.Core;

namespace Mcmere.Play;

internal sealed class SingleInstance : IDisposable
{
    private readonly FileStream? _lock;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    public bool Primary => _lock is not null;
    public SingleInstance(PlayPaths paths)
    {
        _pipe = "mcmere-play-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.ControlRoot.ToUpperInvariant())))[..24];
        try { _lock = new FileStream(PlayFiles.Child(paths.ControlRoot, "application.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { }
    }
    public async Task SendAsync(string value)
    {
        using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(value + "\n"));
    }
    public void Receive(Action<string> callback) => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var input = new MemoryStream(); var buffer = new byte[512]; int read;
                while ((read = await pipe.ReadAsync(buffer, _stop.Token)) > 0)
                {
                    input.Write(buffer, 0, read);
                    if (input.Length > 8192) break;
                    if (buffer.AsSpan(0, read).Contains((byte)'\n')) break;
                }
                if (input.Length <= 8192) callback(Encoding.UTF8.GetString(input.ToArray()).TrimEnd('\r', '\n'));
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    });
    public void Dispose() { _stop.Cancel(); _lock?.Dispose(); _stop.Dispose(); }
}
