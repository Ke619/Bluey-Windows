using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bluey;

internal sealed class PhoneClient
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    public string? Name { get; set; }

    public PhoneClient(Stream stream)
    {
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
    }

    public void Send(Packet packet)
    {
        var line = JsonSerializer.Serialize(packet, Json.Options);
        lock (_gate)
        {
            try { _writer.Write(line); _writer.Write('\n'); _writer.Flush(); }
            catch { /* the phone went away; the read loop cleans up */ }
        }
    }
}

/// Listens for the iPhone on the local network and advertises itself with Bonjour (mDNS) as _googly._tcp.
internal sealed class PhoneServer
{
    private TcpListener? _listener;
    private Makaretu.Dns.ServiceDiscovery? _discovery;
    private readonly List<PhoneClient> _phones = new();
    private FaceState? _lastSent;
    private DateTime _lastSentAt = DateTime.MinValue;

    public int Port { get; private set; }
    public event Action<IReadOnlyList<string>>? PhonesChanged;
    public event Action<PhoneClient, Packet>? PacketReceived;

    public IReadOnlyList<string> PhoneNames
    {
        get { lock (_phones) return _phones.Select(p => p.Name ?? "iPhone").ToList(); }
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.IPv6Any, 0);
        _listener.Server.DualMode = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();

        try
        {
            _discovery = new Makaretu.Dns.ServiceDiscovery();
            var profile = new Makaretu.Dns.ServiceProfile(Environment.MachineName, "_googly._tcp", (ushort)Port);
            _discovery.Advertise(profile);
            _discovery.Announce(profile);
        }
        catch (Exception ex)
        {
            Log.Write("mDNS advertise failed: " + ex);
        }
    }

    public void Stop()
    {
        try { _discovery?.Dispose(); } catch { }
        try { _listener?.Stop(); } catch { }
    }

    private async Task AcceptLoop()
    {
        while (_listener != null)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Serve(tcp);
        }
    }

    private async Task Serve(TcpClient tcp)
    {
        tcp.NoDelay = true;
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        var stream = tcp.GetStream();
        var client = new PhoneClient(stream);
        lock (_phones) _phones.Add(client);
        client.Send(new Packet { Hello = Environment.MachineName });
        if (_lastSent != null) client.Send(new Packet { Face = _lastSent });
        PhonesChanged?.Invoke(PhoneNames);

        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                Packet? packet;
                try { packet = JsonSerializer.Deserialize<Packet>(line, Json.Options); }
                catch { continue; }
                if (packet == null) continue;
                if (packet.Hello != null) { client.Name = packet.Hello; PhonesChanged?.Invoke(PhoneNames); }
                if (packet.Command != null) PacketReceived?.Invoke(client, packet);
            }
        }
        catch { /* connection dropped */ }
        finally
        {
            lock (_phones) _phones.Remove(client);
            try { tcp.Close(); } catch { }
            PhonesChanged?.Invoke(PhoneNames);
        }
    }

    public void Broadcast(Packet packet)
    {
        PhoneClient[] copy;
        lock (_phones) copy = _phones.ToArray();
        foreach (var p in copy) p.Send(packet);
    }

    /// Sends the face to every phone, skipping updates too small to see.
    public void Send(FaceState face)
    {
        var now = DateTime.UtcNow;
        if (_lastSent != null && (now - _lastSentAt).TotalSeconds < 1
            && _lastSent.Mood == face.Mood
            && Math.Abs(_lastSent.GazeX - face.GazeX) < 0.004
            && Math.Abs(_lastSent.GazeY - face.GazeY) < 0.004
            && Math.Abs(_lastSent.Talk - face.Talk) < 0.02)
            return;
        _lastSent = face;
        _lastSentAt = now;
        Broadcast(new Packet { Face = face });
    }
}
