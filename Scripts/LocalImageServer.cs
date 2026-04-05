using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace cielo.Scripts;

internal sealed class LocalImageServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly string _importFolder;

    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;
    public string Url => $"http://127.0.0.1:{Port}";

    public readonly ConcurrentQueue<string> UploadedFiles = new();

    public LocalImageServer(string importFolder)
    {
        _importFolder = importFolder;
    }

    public bool TryStart()
    {
        if (IsRunning) return true;

        foreach (var port in new[] { 47891, 47892, 47893, 0 })
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                _listener = l;
                Port = ((IPEndPoint)l.LocalEndpoint).Port;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoop(_cts.Token));
                ModLog.Info($"LocalImageServer: listening on port {Port}");
                return true;
            }
            catch { /* port busy, try next */ }
        }

        ModLog.Info("LocalImageServer: failed to bind any port");
        return false;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClient(client));
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch { }
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 120_000;
            client.SendTimeout = 30_000;
            using (client)
            using (var stream = client.GetStream())
            {
                var requestLine = ReadLine(stream);
                if (string.IsNullOrEmpty(requestLine)) return;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;

                var method = parts[0];
                var path = parts[1];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    var line = ReadLine(stream);
                    if (string.IsNullOrEmpty(line)) break;
                    var sep = line.IndexOf(':');
                    if (sep > 0)
                        headers[line[..sep].Trim()] = line[(sep + 1)..].Trim();
                }

                switch (method)
                {
                    case "GET" when path is "/" or "/index.html":
                        Send(stream, 200, "text/html; charset=utf-8", PageHtml);
                        break;
                    case "POST" when path == "/upload":
                        HandleUpload(stream, headers);
                        break;
                    default:
                        Send(stream, 404, "text/plain", "Not Found");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Info($"LocalImageServer: client error: {ex.Message}");
        }
    }

    private void HandleUpload(NetworkStream stream, Dictionary<string, string> headers)
    {
        // 部分手机浏览器会先等 100 Continue 再发 body；不响应会导致客户端一直卡在「上传中」
        if (headers.TryGetValue("Expect", out var expect)
            && string.Equals(expect.Trim(), "100-continue", StringComparison.OrdinalIgnoreCase))
        {
            var continueBytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
            stream.Write(continueBytes, 0, continueBytes.Length);
            stream.Flush();
        }

        if (!headers.TryGetValue("Content-Length", out var clStr)
            || !long.TryParse(clStr, out var contentLength) || contentLength <= 0)
        {
            Send(stream, 400, "text/plain; charset=utf-8", "缺少 Content-Length");
            return;
        }

        if (contentLength > 20 * 1024 * 1024)
        {
            Send(stream, 413, "text/plain; charset=utf-8", "文件太大（最大 20 MB）");
            return;
        }

        headers.TryGetValue("X-Filename", out var rawName);
        var fileName = SanitizeName(rawName);

        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = stream.Read(body, read, (int)Math.Min(contentLength - read, 65536));
            if (n == 0) break;
            read += n;
        }

        if (read < contentLength)
        {
            Send(stream, 400, "text/plain; charset=utf-8", $"数据不完整 ({read}/{contentLength})");
            return;
        }

        try
        {
            if (!Directory.Exists(_importFolder))
                Directory.CreateDirectory(_importFolder);

            var dest = Path.Combine(_importFolder, fileName);
            File.WriteAllBytes(dest, body);
            UploadedFiles.Enqueue(fileName);
            ModLog.Info($"LocalImageServer: saved {fileName} ({body.Length / 1024} KB)");
            Send(stream, 200, "text/plain; charset=utf-8",
                $"上传成功: {fileName} ({body.Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            Send(stream, 500, "text/plain; charset=utf-8", $"保存失败: {ex.Message}");
        }
    }

    private static string SanitizeName(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var name = Path.GetFileName(raw);
            if (!string.IsNullOrWhiteSpace(name)
                && Array.Exists(MapImportLibrary.SupportedExtensions,
                    ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return $"upload_{DateTime.Now:yyyyMMdd_HHmmss}.png";
    }

    private static void Send(NetworkStream stream, int code, string contentType, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var status = code switch
        {
            200 => "OK", 400 => "Bad Request",
            404 => "Not Found", 413 => "Payload Too Large",
            500 => "Internal Server Error", _ => "OK"
        };
        var header = $"HTTP/1.1 {code} {status}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    private static string ReadLine(NetworkStream stream)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = stream.ReadByte();
            if (b is -1 or '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        return sb.ToString();
    }

    private const string PageHtml = """
<!DOCTYPE html><html lang="zh-CN"><head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no">
<title>地图绘制 - 图片上传</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:#1c1c1e;color:#f2f2f7;font-family:-apple-system,system-ui,sans-serif;
     padding:20px;min-height:100vh;-webkit-tap-highlight-color:transparent}
h1{font-size:22px;margin-bottom:6px}
.sub{color:#8e8e93;font-size:13px;margin-bottom:20px}
.card{background:#2c2c2e;border-radius:14px;padding:20px;margin-bottom:16px}
.pick{display:block;width:100%;padding:40px 14px;border:2px dashed #48484a;border-radius:12px;
      background:transparent;color:#0a84ff;font-size:17px;cursor:pointer;text-align:center}
.pick:active{background:#0a84ff15}
.preview{max-width:100%;max-height:220px;border-radius:10px;margin-top:14px;display:none;
         object-fit:contain}
.fname{color:#aeaeb2;font-size:13px;margin-top:8px;word-break:break-all}
.up{display:none;width:100%;padding:15px;border:none;border-radius:12px;
    background:#0a84ff;color:#fff;font-size:17px;font-weight:600;cursor:pointer;margin-top:14px}
.up:disabled{background:#48484a;color:#636366}
.up:active:not(:disabled){background:#0064d2}
.bar-wrap{height:4px;background:#48484a;border-radius:2px;margin-top:10px;display:none;overflow:hidden}
.bar{height:100%;background:#0a84ff;border-radius:2px;width:0;transition:width .15s}
.msg{margin-top:10px;font-size:14px;color:#8e8e93;min-height:20px;word-break:break-all}
.ok{color:#30d158}.err{color:#ff453a}
.tip{font-size:13px;color:#aeaeb2;margin-top:14px;line-height:1.55;padding:12px 14px;
     background:#1c1c1e;border-radius:10px;border:1px solid #3a3a3c}
.info{font-size:12px;color:#48484a;margin-top:16px;line-height:1.6}
</style></head><body>
<h1>🎨 地图绘制 · 图片上传</h1>
<p class="sub">选择图片上传成功后，游戏内图库会自动更新</p>
<div class="card">
 <input type="file" accept="image/*" id="fi" hidden>
 <button class="pick" id="pk" onclick="fi.click()">📷 点击选择图片</button>
 <img class="preview" id="pv">
 <div class="fname" id="fn"></div>
 <button class="up" id="ub" onclick="send()">上传到游戏</button>
 <div class="bar-wrap" id="bw"><div class="bar" id="br"></div></div>
 <div class="msg" id="mg"></div>
 <p class="tip">若页面一直显示「上传中…」，可先<strong style="color:#d1d1d6">返回游戏</strong>查看图库：多数情况下文件<strong style="color:#d1d1d6">已经成功上传</strong>，只是本页未及时显示完成。</p>
</div>
<div class="info">
 支持 PNG / JPG / WebP / BMP · 最大 20 MB<br>
 上传成功后回到游戏即可选用新图
</div>
<script>
var F=null,fi=document.getElementById('fi'),pk=document.getElementById('pk');
fi.onchange=function(){
 F=fi.files[0]; if(!F)return;
 var pv=document.getElementById('pv');
 pv.src=URL.createObjectURL(F); pv.style.display='block';
 pk.textContent='📷 重新选择';
 document.getElementById('fn').textContent=F.name+' ('+Math.round(F.size/1024)+' KB)';
 document.getElementById('ub').style.display='block';
 document.getElementById('mg').textContent='';
 document.getElementById('mg').className='msg';
};
function send(){
 if(!F)return;
 var ub=document.getElementById('ub'),mg=document.getElementById('mg'),
     bw=document.getElementById('bw'),br=document.getElementById('br');
 ub.disabled=true; ub.textContent='上传中…';
 bw.style.display='block'; br.style.width='0';
 var x=new XMLHttpRequest();
 var done=false;
 function resetBtn(){ub.textContent='上传到游戏';ub.disabled=false;}
 function fail(t){if(done)return;done=true;br.style.width='100%';mg.textContent=t;mg.className='msg err';resetBtn();}
 function finish(){
  if(done)return; done=true;
  br.style.width='100%'; resetBtn();
  if(x.status>=200&&x.status<300){mg.textContent='✅ '+x.responseText;mg.className='msg ok';}
  else if(x.status===0){mg.textContent='❌ 网络错误';mg.className='msg err';}
  else{mg.textContent='❌ '+(x.responseText||('HTTP '+x.status));mg.className='msg err';}
 }
 x.open('POST','/upload');
 x.setRequestHeader('X-Filename',F.name);
 x.timeout=120000;
 x.upload.onprogress=function(e){if(e.lengthComputable)br.style.width=(e.loaded/e.total*100)+'%';};
 x.onreadystatechange=function(){if(x.readyState===4)finish();};
 x.onerror=function(){fail('❌ 网络错误');};
 x.ontimeout=function(){fail('❌ 上传超时');};
 x.send(F);
}
</script></body></html>
""";
}
