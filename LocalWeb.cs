using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ElanciaLocal;
static class LocalWeb {
    public static async Task Run(CharacterStore characters,int port,CancellationToken ct) {
        var listener=new TcpListener(IPAddress.Loopback,port);listener.Start();
        Console.WriteLine($"Local launcher http://127.0.0.1:{port}/launcher");
        try {while(!ct.IsCancellationRequested) {var c=await listener.AcceptTcpClientAsync(ct);_=Reply(c,characters,ct);}}
        catch(OperationCanceledException){}finally{listener.Stop();}
    }
    static async Task Reply(TcpClient c,CharacterStore characters,CancellationToken ct) {
        using(c)try {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var s=c.GetStream();var b=new byte[8192];var n=0;
            while(n<b.Length) {var r=await s.ReadAsync(b.AsMemory(n),timeout.Token);if(r==0)return;n+=r;if(Encoding.ASCII.GetString(b,0,n).Contains("\r\n\r\n"))break;}
            bool movementSpeeds=Encoding.ASCII.GetString(b,0,n).StartsWith("GET /movement-speeds HTTP/1.",StringComparison.Ordinal);
            string contentType=movementSpeeds?"application/json":"text/html; charset=utf-8";
            var body=movementSpeeds
                ? JsonSerializer.SerializeToUtf8Bytes(characters.List("test").Where(c=>c.MovementSpeed.HasValue)
                    .ToDictionary(c=>c.Id,c=>c.MovementSpeed!.Value))
                : Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset='utf-8'><title>Elancia Local</title></head><body style='background:#17382c;color:white;font:24px sans-serif;text-align:center;padding:70px'><h1>Elancia Local</h1><p>127.0.0.1 · test / test</p><a href='javascript:GameStart();' style='display:block;padding:24px;background:#df8635;color:white'>게임시작</a></body></html>");
            var head=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            await s.WriteAsync(head,timeout.Token);await s.WriteAsync(body,timeout.Token);
        }catch(Exception ex) when(ex is IOException or OperationCanceledException or SocketException) {Console.WriteLine("Local web: "+ex.Message);}
    }
}
