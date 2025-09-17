using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Linq;
using Shype_Login_Server_TCP.Models;

namespace Shype_Login_Server_TCP.Services
{
    public class ShypeClient
    {
        // UDP to server
        private UdpClient? _serverClient;
        private IPEndPoint? _serverEndPoint;
        // UDP for P2P listening/sending
        private UdpClient? _p2pClient;

        // Track known peers' P2P endpoints (username -> endpoint)
        private readonly ConcurrentDictionary<string, IPEndPoint> _p2pPeers = new();
        private readonly ConcurrentDictionary<string, string> _userEndPoints = new();
        private readonly ConcurrentDictionary<string, DateTime> _peerLastSeen = new();
        private readonly ConcurrentDictionary<string, bool> _sentJoinTo = new();

        private readonly string _username;
        private readonly int _p2pPort;
        private bool _isRunning;
        private bool _serverOnline;

        // Debounce/dedupe for user list notifications
        private readonly TimeSpan _userListDebounce = TimeSpan.FromMilliseconds(300);
        private DateTime _lastUserListPublish = DateTime.MinValue;
        private HashSet<string> _lastPublishedUsers = new();

        // Presence tuning
        private readonly TimeSpan _presencePingInterval = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _peerTimeout = TimeSpan.FromSeconds(15);
        private CancellationTokenSource? _cts;

        public event Action<string>? OnMessageReceived;
        public event Action<List<string>>? OnUserListUpdated;
        public event Action<string>? OnUserDisconnected;
        public event Action<string, string>? OnChatReceived;

        public ShypeClient(string username, int p2pPort = 0)
        {
            _username = username;
            _p2pPort = p2pPort > 0 ? p2pPort : new Random().Next(9000, 10000);
        }

        public async Task<bool> ConnectToServerAsync(string serverAddress, int serverPort)
        {
            try
            {
                // Prefer IPv4 address for compatibility with IPv4 Any binding on server
                var hostEntry = Dns.GetHostEntry(serverAddress);
                var serverIp = hostEntry.AddressList.First(a => a.AddressFamily == AddressFamily.InterNetwork);
                _serverEndPoint = new IPEndPoint(serverIp, serverPort);
                _serverClient = new UdpClient();
                // Connect filters inbound to server only; also sets default remote for SendAsync
                _serverClient.Connect(_serverEndPoint);

                // Set running before spinning listeners
                _isRunning = true;
                _serverOnline = true;
                _cts = new CancellationTokenSource();

                // Start P2P UDP listener and maintenance
                await StartP2PListenerAsync();
                _ = Task.Run(() => PeerMaintenanceLoopAsync(_cts.Token));

                // Send login message
                var loginMessage = new Message
                {
                    Type = MessageType.Login,
                    Sender = _username,
                    Data = new Dictionary<string, object> { ["P2PPort"] = _p2pPort },
                    Timestamp = DateTime.UtcNow
                };

                await SendToServerAsync(loginMessage);

                // Start listening for server messages
                _ = Task.Run(ListenToServerAsync);

                // Proactively request user list to avoid relying on a single UDP packet
                await RequestUserListAsync();

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to connect to server (UDP): {ex.Message}");
                // Still start P2P to allow decentralized operation with previously known peers
                _isRunning = true;
                _serverOnline = false;
                _cts = new CancellationTokenSource();
                await StartP2PListenerAsync();
                _ = Task.Run(() => PeerMaintenanceLoopAsync(_cts.Token));
                return true;
            }
        }

        private async Task StartP2PListenerAsync()
        {
            _p2pClient = new UdpClient(new IPEndPoint(IPAddress.Any, _p2pPort));
            Console.WriteLine($"P2P UDP listener started on port {_p2pPort}");

            _ = Task.Run(async () =>
            {
                while (_isRunning && _p2pClient != null)
                {
                    try
                    {
                        var result = await _p2pClient.ReceiveAsync();
                        _ = Task.Run(() => HandleIncomingP2PDatagramAsync(result));
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"P2P UDP receive error: {ex.Message}");
                    }
                }
            });
        }

        private async Task HandleIncomingP2PDatagramAsync(UdpReceiveResult result)
        {
            var buffer = result.Buffer;
            var messageJson = Encoding.UTF8.GetString(buffer);

            try
            {
                var message = Message.FromJson(messageJson);
                if (message == null)
                {
                    Console.WriteLine("Failed to parse incoming P2P UDP message");
                    return;
                }

                var peerUsername = message.Sender;
                if (!string.IsNullOrEmpty(peerUsername))
                {
                    _p2pPeers[peerUsername] = result.RemoteEndPoint;
                    _peerLastSeen[peerUsername] = DateTime.UtcNow;
                }

                switch (message.Type)
                {
                    case MessageType.Presence:
                        await HandlePresenceAsync(peerUsername, message, result.RemoteEndPoint);
                        break;

                    case MessageType.Chat:
                        if (message.Content == "__HANDSHAKE__")
                        {
                            // Ignore handshake in logs, update last-seen only
                            return;
                        }
                        OnChatReceived?.Invoke(message.Sender, message.Content);
                        OnMessageReceived?.Invoke($"{message.Sender}: {message.Content}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"P2P UDP message error: {ex.Message}");
            }
        }

        private async Task HandlePresenceAsync(string peerUsername, Message msg, IPEndPoint remoteEp)
        {
            var action = msg.Content;
            if (string.IsNullOrEmpty(peerUsername)) return;

            switch (action)
            {
                case "Join":
                    _p2pPeers[peerUsername] = remoteEp;
                    _peerLastSeen[peerUsername] = DateTime.UtcNow;
                    // Ack back
                    await SendPresenceAsync(remoteEp, "Ack");
                    PublishUserListIfChanged();
                    break;
                case "Leave":
                    if (_p2pPeers.TryRemove(peerUsername, out _))
                    {
                        _peerLastSeen.TryRemove(peerUsername, out _);
                        _userEndPoints.TryRemove(peerUsername, out _);
                        _sentJoinTo.TryRemove(peerUsername, out _);
                        OnUserDisconnected?.Invoke(peerUsername);
                        PublishUserListIfChanged();
                    }
                    break;
                case "Ping":
                    _peerLastSeen[peerUsername] = DateTime.UtcNow;
                    // Optional: reply with Ack
                    await SendPresenceAsync(remoteEp, "Ack");
                    break;
                case "Ack":
                    _peerLastSeen[peerUsername] = DateTime.UtcNow;
                    break;
            }
        }

        private async Task SendPresenceAsync(IPEndPoint destination, string action)
        {
            if (_p2pClient == null) return;
            var message = new Message
            {
                Type = MessageType.Presence,
                Sender = _username,
                Content = action,
                Timestamp = DateTime.UtcNow
            };
            var json = message.ToJson();
            var data = Encoding.UTF8.GetBytes(json);
            await _p2pClient.SendAsync(data, data.Length, destination);
        }

        private async Task PeerMaintenanceLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Send ping to all peers
                    foreach (var kvp in _p2pPeers.ToArray())
                    {
                        await SendPresenceAsync(kvp.Value, "Ping");
                    }

                    // Check timeouts
                    var now = DateTime.UtcNow;
                    foreach (var kvp in _peerLastSeen.ToArray())
                    {
                        if (now - kvp.Value > _peerTimeout)
                        {
                            if (_p2pPeers.TryRemove(kvp.Key, out _))
                            {
                                _userEndPoints.TryRemove(kvp.Key, out _);
                                _sentJoinTo.TryRemove(kvp.Key, out _);
                                _peerLastSeen.TryRemove(kvp.Key, out _);
                                OnUserDisconnected?.Invoke(kvp.Key);
                                PublishUserListIfChanged();
                            }
                        }
                    }
                }
                catch { }

                await Task.Delay(_presencePingInterval, token).ContinueWith(_ => { });
            }
        }

        private async Task ListenToServerAsync()
        {
            if (_serverClient == null) return;

            try
            {
                while (_isRunning)
                {
                    var result = await _serverClient.ReceiveAsync();
                    var messageJson = Encoding.UTF8.GetString(result.Buffer);
                    var message = Message.FromJson(messageJson);

                    if (message == null) continue;

                    switch (message.Type)
                    {
                        case MessageType.Login:
                            Console.WriteLine($"Server: {message.Content}");
                            // Don't request user list here; server will send it
                            break;

                        case MessageType.UserList:
                            await HandleUserListUpdate(message);
                            break;
                        case MessageType.ServerShutdown:
                            Console.WriteLine("Server is shutting down (continuing in P2P mode)");
                            _serverOnline = false;
                            try { _serverClient?.Close(); } catch { }
                            _serverClient = null;
                            break;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // normal on shutdown
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server UDP receive error: {ex.Message}");
            }
        }

        private void PublishUserListIfChanged()
        {
            var usernames = _p2pPeers.Keys.Where(u => u != _username).OrderBy(u => u).ToList();
            var currentSet = new HashSet<string>(usernames);
            var now = DateTime.UtcNow;

            bool changed = !_lastPublishedUsers.SetEquals(currentSet);
            bool debounced = now - _lastUserListPublish >= _userListDebounce;

            if (changed && debounced)
            {
                _lastPublishedUsers = currentSet;
                _lastUserListPublish = now;
                OnUserListUpdated?.Invoke(usernames);
            }
        }

        private async Task HandleUserListUpdate(Message message)
        {
            try
            {
                if (message.Data.ContainsKey("Users"))
                {
                    var usersJsonElement = (JsonElement)message.Data["Users"];
                    var usersJson = usersJsonElement.GetRawText();
                    var userList = JsonSerializer.Deserialize<List<UserDto>>(usersJson, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });

                    if (userList != null)
                    {
                        var changed = false;
                        foreach (var user in userList)
                        {
                            if (!string.IsNullOrEmpty(user.Username) && !string.IsNullOrEmpty(user.EndPoint) && user.Username != _username)
                            {
                                _userEndPoints[user.Username] = user.EndPoint;
                                try
                                {
                                    var ep = IPEndPoint.Parse(user.EndPoint);
                                    if (!_p2pPeers.TryGetValue(user.Username, out var existing) || !Equals(existing, ep))
                                    {
                                        _p2pPeers[user.Username] = ep;
                                        changed = true;
                                    }

                                    // Send join presence once per peer
                                    if (_sentJoinTo.TryAdd(user.Username, true))
                                    {
                                        await SendPresenceAsync(ep, "Join");
                                    }
                                }
                                catch { }
                            }
                        }
                        if (changed) PublishUserListIfChanged();

                        // Always publish current list to UI so the latest-connected client updates immediately
                        var names = _p2pPeers.Keys.Where(u => u != _username).OrderBy(u => u).ToList();
                        OnUserListUpdated?.Invoke(names);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing user list: {ex.Message}");
            }
        }

        public async Task SendChatMessageAsync(string targetUsername, string messageContent)
        {
            // Resolve endpoint from known peers or user list
            if (!_p2pPeers.TryGetValue(targetUsername, out var peerEp))
            {
                if (_userEndPoints.TryGetValue(targetUsername, out var endPointStr))
                {
                    try
                    {
                        peerEp = IPEndPoint.Parse(endPointStr);
                        _p2pPeers[targetUsername] = peerEp;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Invalid endpoint for {targetUsername}: {endPointStr} ({ex.Message})");
                        return;
                    }
                }
                else
                {
                    Console.WriteLine($"User {targetUsername} not found in user list");
                    return;
                }
            }

            if (_p2pClient == null)
            {
                Console.WriteLine("P2P client not initialized");
                return;
            }

            try
            {
                // Optionally send handshake first if we haven't seen this peer recently
                await SendPresenceAsync(peerEp, "Ping");
                await Task.Delay(20);

                // Send actual message
                var message = new Message
                {
                    Type = MessageType.Chat,
                    Sender = _username,
                    Receiver = targetUsername,
                    Content = messageContent,
                    Timestamp = DateTime.UtcNow
                };

                await SendP2PMessageAsync(peerEp, message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send UDP message to {targetUsername}: {ex.Message}");
            }
        }

        private async Task SendP2PMessageAsync(IPEndPoint destination, Message message)
        {
            if (_p2pClient == null) return;
            var json = message.ToJson();
            var data = Encoding.UTF8.GetBytes(json);
            await _p2pClient.SendAsync(data, data.Length, destination);
        }

        private async Task SendToServerAsync(Message message)
        {
            if (_serverClient != null)
            {
                var json = message.ToJson();
                var data = Encoding.UTF8.GetBytes(json);
                await _serverClient.SendAsync(data, data.Length);
            }
        }

        // Make this public so UI can trigger refresh and connect can call it
        public async Task RequestUserListAsync()
        {
            var message = new Message
            {
                Type = MessageType.UserList,
                Sender = _username,
                Timestamp = DateTime.UtcNow
            };
            await SendToServerAsync(message);
        }

        public async Task DisconnectAsync()
        {
            // Broadcast leave to peers first
            foreach (var kvp in _p2pPeers.ToArray())
            {
                try { await SendPresenceAsync(kvp.Value, "Leave"); } catch { }
            }

            // Send logout message to server
            var logoutMessage = new Message
            {
                Type = MessageType.Logout,
                Sender = _username,
                Timestamp = DateTime.UtcNow
            };
            await SendToServerAsync(logoutMessage);

            _isRunning = false;
            _cts?.Cancel();

            // Close UDP clients
            try { _serverClient?.Close(); } catch { }
            try { _p2pClient?.Close(); } catch { }
        }

        public List<string> GetConnectedUsers()
        {
            return _p2pPeers.Keys.Where(u => u != _username).OrderBy(u => u).ToList();
        }
    }
}
