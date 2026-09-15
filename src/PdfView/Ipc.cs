using System.IO.Pipes;
using System.Text;

namespace PdfView;

/// A named pipe that lets a second launch hand its file to the first instance.
static class Ipc
{
    const string PipeName = "pdfview.open.v2";

    public static event Action<string?>? OpenRequested;

    public static void StartServer(CancellationToken cancel)
    {
        var thread = new Thread(() => Listen(cancel))
        {
            IsBackground = true,
            Name = "pdfview-ipc",
        };
        thread.Start();
    }

    static void Listen(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                server.WaitForConnectionAsync(cancel).GetAwaiter().GetResult();

                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = reader.ReadToEnd().Trim();
                OpenRequested?.Invoke(line.Length == 0 ? null : line);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // A malformed or abandoned connection should not kill the listener.
                Thread.Sleep(200);
            }
        }
    }

    public static bool SendToRunningInstance(string? file)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(file ?? string.Empty);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
