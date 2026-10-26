using System.Net;
using System.Net.Sockets;

namespace FerretSharp.Integration.Tests;

/// <summary>What a network path does with TCP urgent data – the way ODP.NET sends a break out of band.</summary>
internal enum UrgentData
{
    /// <summary>Never reaches the server (a proxy that only copies the stream): the server does not notice the break.</summary>
    Lost,

    /// <summary>Reaches the server as an ordinary byte in the stream (a firewall that clears the urgent flag).</summary>
    PassedOnAsData,
}

/// <summary>
/// A TCP proxy in front of the test container that copies the byte stream – the model of a VPN for breaks (backlog
/// "Abbrechen über VPN"). Urgent data goes as <see cref="UrgentData"/> says, never as urgent data.
/// </summary>
internal sealed class BreakProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly UrgentData _urgent;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Socket> _sockets = [];
    private readonly Task _accepting;

    public BreakProxy(string targetHost, int targetPort, UrgentData urgent)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _urgent = urgent;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            var server = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            client.NoDelay = true;
            // Inline, the urgent byte comes with the ordinary data and the copy loop passes it on.
            client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.OutOfBandInline, _urgent == UrgentData.PassedOnAsData);
            await server.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            lock (_sockets)
            {
                _sockets.Add(client);
                _sockets.Add(server);
            }

            _ = CopyAsync(client, server);
            _ = CopyAsync(server, client);
            if (_urgent == UrgentData.Lost)
            {
                _ = Task.Run(() => DiscardUrgent(client));
            }
        }
    }

    private async Task CopyAsync(Socket from, Socket to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                if (read == 0)
                {
                    to.Shutdown(SocketShutdown.Send);
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _stop.Token);
            }
        }
        catch (Exception)
        {
            // Closed on either side or by DisposeAsync: the other direction ends on its own.
        }
    }

    /// <summary>
    /// Takes urgent data off the socket, where Windows reports it as an exceptional condition. Linux keeps an unread urgent
    /// byte aside from the stream and replaces it with the next (it never shows here).
    /// </summary>
    private void DiscardUrgent(Socket client)
    {
        var buffer = new byte[16];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (client.Poll(TimeSpan.FromMilliseconds(50), SelectMode.SelectError))
                {
                    client.Receive(buffer, SocketFlags.OutOfBand);
                }
            }
        }
        catch (Exception)
        {
            // Closed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        lock (_sockets)
        {
            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }
        }

        await _accepting;
        _stop.Dispose();
    }
}
