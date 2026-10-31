using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WuwatappoNative.Core;

namespace WuwatappoNative.Net;

/// <summary>最小 Socket.IO v4 / Engine.IO v4 客户端（websocket-only，不自动重连）。</summary>
public sealed class SocketClient : IDisposable
{
    ClientWebSocket _ws;
    CancellationTokenSource _cts;
    Task _pump;
    int _nextId = 1;
    int _pingIntervalMs = 25000;
    System.Threading.Timer _pingTimer;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _acks = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly object _connectGate = new();

    public bool Connected { get; private set; }
    public event Action ConnectedEvent;
    public event Action<string> Closed;
    public event Action<string, JsonNode> Event;

    public async Task ConnectAsync(string token, string roomNo)
    {
        _ws = new ClientWebSocket();
        _ws.Options.CollectHttpResponseDetails = true;
        _cts = new CancellationTokenSource();
        var uri = new Uri("wss://api-mc-wuwatappo.kurogames-global.com/socket.io/?EIO=4&transport=websocket"
            + "&token=" + Uri.EscapeDataString(token) + "&rm=" + Uri.EscapeDataString(roomNo ?? ""));
        await _ws.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);
        Log.Write("socket 已连接 " + roomNo);
        _pump = Task.Run(PumpAsync);
    }

    void ParseHandshake(string msg)
    {
        try
        {
            var node = JsonNode.Parse(msg.Substring(1));
            int interval = (int)ApiClient.ReadLong(node?["pingInterval"]);
            if (interval >= 1000) _pingIntervalMs = Math.Max(5000, interval - 2000);
        }
        catch { }
    }

    void StartHeartbeat()
    {
        _pingTimer?.Dispose();
        // 原版 socket.io 客户端是“客户端发 ping、服务端回 pong”的心跳模型。
        _pingTimer = new System.Threading.Timer(_ =>
        {
            if (!Connected) return;
            _ = SendRawAsync("2");
        }, null, _pingIntervalMs, _pingIntervalMs);
    }

    async Task PumpAsync()
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;
                string msg = sb.ToString();
                sb.Clear();
                await HandleAsync(msg).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException)) { Log.Write("socket 读失败 " + ex.Message); }
        catch (OperationCanceledException) { }
        finally
        {
            Connected = false;
            _pingTimer?.Dispose();
            _pingTimer = null;
            foreach (var ack in _acks.Values) ack.TrySetResult(null);
            _acks.Clear();
            Closed?.Invoke("连接已断开");
        }
    }

    async Task HandleAsync(string msg)
    {
        if (msg.Length == 0) return;
        Log.Write("DBG socket<- " + (msg.Length > 180 ? msg.Substring(0, 180) : msg));
        switch (msg[0])
        {
            case '0': // engine.io open
                ParseHandshake(msg);
                await SendRawAsync("40").ConfigureAwait(false);
                StartHeartbeat();
                break;
            case '2': // 服务端 ping（兼容）
                Log.Write("DBG engine ping from server");
                await SendRawAsync("3").ConfigureAwait(false);
                break;
            case '3': // 服务端 pong（本客户端主动心跳）
                Log.Write("DBG engine pong");
                break;
            case '4':
                if (msg.Length < 2) return;
                if (msg[1] == '0') // namespace connected
                {
                    // 服务端会回 40 与 40[]，只处理第一次，避免重复发 rm/rd
                    lock (_connectGate)
                    {
                        if (Connected) return;
                        Connected = true;
                    }
                    Log.Write("socket 命名空间已连接");
                    ConnectedEvent?.Invoke();
                }
                else if (msg[1] == '1') // namespace disconnect
                {
                    Log.Write("socket 命名空间断开");
                }
                else if (msg[1] == '2') // event
                {
                    string body = StripId(msg.Substring(2), out _);
                    DispatchEvent(body);
                }
                else if (msg[1] == '3') // ack
                {
                    string body = msg.Substring(2);
                    int i = 0;
                    while (i < body.Length && char.IsDigit(body[i])) i++;
                    if (int.TryParse(body.Substring(0, i), out int id) && _acks.TryRemove(id, out var tcs))
                    {
                        try
                        {
                            var node = JsonNode.Parse(body.Substring(i));
                            // 服务端 ack 形如 43<id>[{...}]，统一取出第一项
                            if (node is JsonArray ackArr && ackArr.Count == 1) node = ackArr[0]?.DeepClone();
                            tcs.TrySetResult(node);
                        }
                        catch { tcs.TrySetResult(null); }
                    }
                }
                break;
        }
    }

    void DispatchEvent(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonArray arr && arr.Count > 0)
            {
                string name = arr[0]?.GetValue<string>() ?? "";
                var payload = arr.Count > 1 ? arr[1] : null;
                Event?.Invoke(name, payload);
            }
        }
        catch (Exception ex) { Log.Write("socket 事件解析失败 " + json + " " + ex.Message); }
    }

    static string StripId(string body, out int id)
    {
        id = 0;
        int i = 0;
        while (i < body.Length && char.IsDigit(body[i])) i++;
        if (i > 0 && int.TryParse(body.Substring(0, i), out id)) return body.Substring(i);
        return body;
    }

    public async Task<JsonNode> EmitAsync(string name, JsonNode payload = null, int timeoutMs = 8000)
    {
        if (!Connected) return null;
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acks[id] = tcs;
        var arr = new JsonArray { name };
        if (payload != null) arr.Add(payload.DeepClone());
        await SendRawAsync("42" + id + arr.ToJsonString()).ConfigureAwait(false);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (done != tcs.Task) { _acks.TryRemove(id, out _); return null; }
        return await tcs.Task.ConfigureAwait(false);
    }

    public Task EmitNoAckAsync(string name, JsonNode payload = null)
    {
        if (!Connected) return Task.CompletedTask;
        var arr = new JsonArray { name };
        if (payload != null) arr.Add(payload.DeepClone());
        return SendRawAsync("42" + arr.ToJsonString());
    }

    async Task SendRawAsync(string text)
    {
        Log.Write("DBG socket-> " + (text.Length > 180 ? text.Substring(0, 180) : text));
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Write("socket 发送失败 " + ex.Message); }
        finally { _sendLock.Release(); }
    }

    public async Task CloseAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_ws != null && _ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _cts?.Dispose();
        _sendLock.Dispose();
        _pingTimer?.Dispose();
    }
}
