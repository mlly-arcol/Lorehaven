using System.Net;
using System.Net.Sockets;
using System.Text;

var modelPath = args[Array.IndexOf(args, "--model") + 1];
var port = int.Parse(args[Array.IndexOf(args, "--port") + 1]);
var mode = File.ReadAllText(modelPath);
File.WriteAllText(modelPath + ".pid", Environment.ProcessId.ToString());
if (mode == "crash")
{
    Console.Error.WriteLine("GGUF_MAGIC_INVALID");
    return 7;
}
if (mode == "verbose") Console.Error.Write(new string('x', 1024 * 1024) + "MODEL_LOG_END");
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
while (true)
{
    using var client = await listener.AcceptTcpClientAsync();
    await using var stream = client.GetStream();
    using var reader = new StreamReader(stream, leaveOpen: true);
    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
    var body = mode == "loading" ? "{}" : "{\"data\":[{\"id\":\"NativeTavern-Local\"}]}";
    var bytes = Encoding.UTF8.GetBytes(body);
    var status = mode == "loading" ? "503 Service Unavailable" : "200 OK";
    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
    await stream.WriteAsync(bytes);
    await stream.FlushAsync();
    if (mode == "exit") return 0;
}
