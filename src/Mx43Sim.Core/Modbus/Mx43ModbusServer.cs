using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Mx43Sim.Core.Modbus;

/// <summary>
/// Minimal Modbus TCP server (port 502) supporting the function codes
/// required by the MX43 supervisor:
///
///   FC 3  Read Holding Registers
///   FC 4  Read Input Registers
///   FC 6  Write Single Register
///   FC16  Write Multiple Registers
///
/// No external dependencies — pure System.Net.Sockets. Each connected
/// client runs in its own Task; a CancellationToken is shared across
/// all of them to enable a clean shutdown.
/// </summary>
public sealed class Mx43ModbusServer : IAsyncDisposable
{
    private readonly Mx43RegisterStore _store;
    private readonly int _port;
    private TcpListener? _listener;
    private readonly ConcurrentBag<TcpClient> _clients = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public event Action<string>? Log;

    public bool IsRunning => _listener is not null;

    public Mx43ModbusServer(Mx43RegisterStore store, int port = 502)
    {
        _store = store;
        _port = port;
    }

    public Task StartAsync()
    {
        if (_listener is not null) return Task.CompletedTask;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _acceptTask = AcceptLoop(_cts.Token);
        Log?.Invoke($"Modbus server listening on TCP/{_port}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_listener is null) return;
        _cts?.Cancel();
        _listener.Stop();
        _listener = null;

        foreach (var c in _clients)
        {
            try { c.Close(); } catch { }
        }
        _clients.Clear();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { }
        }
        Log?.Invoke("Modbus server stopped");
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task AcceptLoop(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke("Accept failed: " + ex.Message);
                continue;
            }
            _clients.Add(client);
            _ = Task.Run(() => ClientLoop(client, ct), ct);
        }
    }

    private async Task ClientLoop(TcpClient client, CancellationToken ct)
    {
        var ep = client.Client.RemoteEndPoint;
        Log?.Invoke($"Client connected: {ep}");
        using var stream = client.GetStream();
        var buf = new byte[260];
        try
        {
            while (!ct.IsCancellationRequested && client.Connected)
            {
                int read = 0;
                // MBAP header is 7 bytes: transactionId(2) protocolId(2) length(2) unitId(1)
                while (read < 7)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(read, 7 - read), ct).ConfigureAwait(false);
                    if (n == 0) return;
                    read += n;
                }
                ushort txId  = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0, 2));
                ushort proto = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2, 2));
                ushort len   = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4, 2));
                if (proto != 0) { Log?.Invoke("Non-Modbus protocol, closing"); return; }
                int remaining = len - 1; // unit id already in header
                int pdu = 7;
                while (remaining > 0)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(pdu, remaining), ct).ConfigureAwait(false);
                    if (n == 0) return;
                    pdu += n;
                    remaining -= n;
                }

                byte unitId = buf[6];
                byte fc = buf[7];
                int pduLen = pdu - 7;
                var pduArr = new byte[pduLen];
                Array.Copy(buf, 7, pduArr, 0, pduLen);
                var response = HandleRequest(unitId, fc, pduArr);

                // Build MBAP + PDU
                var outBuf = new byte[7 + response.Length];
                BinaryPrimitives.WriteUInt16BigEndian(outBuf.AsSpan(0, 2), txId);
                BinaryPrimitives.WriteUInt16BigEndian(outBuf.AsSpan(2, 2), 0);
                BinaryPrimitives.WriteUInt16BigEndian(outBuf.AsSpan(4, 2), (ushort)(1 + response.Length));
                outBuf[6] = unitId;
                response.CopyTo(outBuf.AsSpan(7));
                await stream.WriteAsync(outBuf, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke($"Client {ep} error: {ex.Message}");
        }
        finally
        {
            Log?.Invoke($"Client disconnected: {ep}");
            client.Close();
        }
    }

    private byte[] HandleRequest(byte unitId, byte fc, byte[] pdu)
    {
        try
        {
            return fc switch
            {
                3  => ReadHolding(pdu),
                4  => ReadHolding(pdu),  // same backing store
                6  => WriteSingle(pdu),
                16 => WriteMultiple(pdu),
                _ => ExceptionResponse(fc, 1),  // illegal function
            };
        }
        catch (Exception ex)
        {
            Log?.Invoke("Handler error: " + ex.Message);
            return ExceptionResponse(fc, 4); // slave device failure
        }
    }

    private byte[] ReadHolding(byte[] pdu)
    {
        // PDU: fc(1) startAddr(2) quantity(2). The MX43 cahier uses
        // 1-based Modbus addressing (first holding register = 1), so
        // startAddr is interpreted as a 1-based address and we feed it
        // straight into the store.
        if (pdu.Length < 5) return ExceptionResponse(3, 3); // illegal data value
        ushort start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
        ushort qty   = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
        if (qty == 0 || qty > 125) return ExceptionResponse(3, 3);

        var payload = new byte[1 + qty * 2];
        payload[0] = (byte)(qty * 2);
        var regs = _store.ReadRange(start, qty);
        for (int i = 0; i < qty; i++)
        {
            short v = regs[i];
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1 + i * 2, 2), (ushort)v);
        }
        var resp = new byte[2 + payload.Length];
        resp[0] = 3;       // FC echo
        payload.CopyTo(resp.AsSpan(1));
        return resp;
    }

    private byte[] WriteSingle(byte[] pdu)
    {
        if (pdu.Length < 5) return ExceptionResponse(6, 3);
        ushort addr = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
        ushort val  = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
        _store.WriteRegU(addr, val);   // 1-based
        var resp = new byte[5];
        resp[0] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(1, 2), addr);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(3, 2), val);
        return resp;
    }

    private byte[] WriteMultiple(byte[] pdu)
    {
        // PDU: fc(1) startAddr(2) qty(2) byteCount(1) values(...)
        if (pdu.Length < 6) return ExceptionResponse(16, 3);
        ushort start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
        ushort qty   = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
        byte bc = pdu[5];
        if (bc != qty * 2 || pdu.Length < 6 + qty * 2) return ExceptionResponse(16, 3);
        if (qty == 0 || qty > 123) return ExceptionResponse(16, 3);

        for (int i = 0; i < qty; i++)
        {
            ushort v = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2, 2));
            _store.WriteRegU(start + i, v);
        }
        var resp = new byte[5];
        resp[0] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(1, 2), start);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(3, 2), qty);
        return resp;
    }

    private static byte[] ExceptionResponse(byte fc, byte exCode) => new byte[] { (byte)(fc | 0x80), exCode };
}
