using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Shype_Login_Server_TCP.Models;

namespace Shype_Login_Server_TCP.Services
{
    public class ShypeClient
    {
        private TcpClient? _serverConnection;
        private NetworkStream? _serverStream;
        private TcpListener? _p2pListener;
        private readonly ConcurrentDictionary<string, TcpClient> _p2pConnections = new();
        private readonly ConcurrentDictionary<string, string> _userEndPoints = new();
        private readonly string _username;
        private readonly int _p2pPort;
        private bool _isRunning;

        public event Action<string>? OnMessageReceived;
        public event Action<List<string>>? OnUserListUpdated;
        public event Action<string>? OnUserDisconnected;

        public ShypeClient(string username, int p2pPort = 0)
        {
            _username = username;
            _p2pPort = p2pPort > 0 ? p2pPort : new Random().Next(9000, 10000);
        }

        public async Task<bool> ConnectToServerAsync(string serverAddress, int serverPort)
        {
            try
            {
                _serverConnection = new TcpClient();
                await _serverConnection.ConnectAsync(serverAddress, serverPort);
                _serverStream = _serverConnection.GetStream();

                // Start P2P listener
                await StartP2PListenerAsync();

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

                _isRunning = true;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to connect to server: {ex.Message}");
                return false;
            }
        }

        private async Task StartP2PListenerAsync()
        {
            _p2pListener = new TcpListener(IPAddress.Any, _p2pPort);
            _p2pListener.Start();
            
            Console.WriteLine($"P2P listener started on port {_p2pPort}");
            
            _ = Task.Run(async () =>
            {
                while (_isRunning)
                {
                    try
                    {
                        var client = await _p2pListener.AcceptTcpClientAsync();
                        _ = Task.Run(() => HandleIncomingP2PConnectionAsync(client));
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            });
        }

        private async Task HandleIncomingP2PConnectionAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            string? peerUsername = null;

            try
            {
                while (client.Connected && _isRunning)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    var messageJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Console.WriteLine($"Received P2P message: {messageJson}"); // Debug log
                    
                    var message = Message.FromJson(messageJson);
                    
                    if (message == null) 
                    {
                        Console.WriteLine("Failed to parse incoming P2P message");
                        continue;
                    }

                    if (peerUsername == null)
                    {
                        peerUsername = message.Sender;
                        _p2pConnections[peerUsername] = client;
                        Console.WriteLine($"P2P connection established with {peerUsername}");
                    }

                    if (message.Type == MessageType.Chat)
                    {
                        // Skip handshake messages
                        if (message.Content == "__HANDSHAKE__")
                        {
                            Console.WriteLine($"Handshake received from {message.Sender}");
                            continue;
                        }
                        
                        Console.WriteLine($"Processing chat message from {message.Sender}: {message.Content}");
                        OnMessageReceived?.Invoke($"{message.Sender}: {message.Content}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"P2P connection error: {ex.Message}");
            }
            finally
            {
                if (peerUsername != null)
                {
                    _p2pConnections.TryRemove(peerUsername, out _);
                    OnUserDisconnected?.Invoke(peerUsername);
                    Console.WriteLine($"P2P connection with {peerUsername} closed");
                }
                client.Close();
            }
        }

        private async Task ListenToServerAsync()
        {
            if (_serverStream == null) return;

            var buffer = new byte[4096];
            try
            {
                while (_isRunning && _serverConnection?.Connected == true)
                {
                    var bytesRead = await _serverStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    var messageJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var message = Message.FromJson(messageJson);
                    
                    if (message == null) continue;

                    switch (message.Type)
                    {
                        case MessageType.Login:
                            Console.WriteLine($"Server: {message.Content}");
                            if (message.Content == "Login successful")
                            {
                                await RequestUserListAsync();
                            }
                            break;

                        case MessageType.UserList:
                            await HandleUserListUpdate(message);
                            break;
                        case MessageType.ServerShutdown:
                            Console.WriteLine("Server is shutting down");
                            _isRunning = false;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server connection error: {ex.Message}");
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
                    
                    // Request user list immediately after successful login
                    await Task.Delay(50);
                    
                    if (userList != null)
                    {
                        _userEndPoints.Clear();
                        var usernames = new List<string>();
                        
                        foreach (var user in userList)
                        {
                            if (!string.IsNullOrEmpty(user.Username) && !string.IsNullOrEmpty(user.EndPoint) && user.Username != _username)
                            {
                                _userEndPoints[user.Username] = user.EndPoint;
                                usernames.Add(user.Username);
                            }
                        }
                        
                        OnUserListUpdated?.Invoke(usernames);
                        Console.WriteLine($"User list updated: {string.Join(", ", usernames)}");
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
            // Try existing P2P connection first
            if (_p2pConnections.TryGetValue(targetUsername, out var existingConnection) && existingConnection.Connected)
            {
                try
                {
                    var message = new Message
                    {
                        Type = MessageType.Chat,
                        Sender = _username,
                        Receiver = targetUsername,
                        Content = messageContent,
                        Timestamp = DateTime.UtcNow
                    };

                    await SendP2PMessageAsync(existingConnection, message);
                    Console.WriteLine($"Sending to {targetUsername}: {messageContent}");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to send via existing connection: {ex.Message}");
                    _p2pConnections.TryRemove(targetUsername, out _);
                    existingConnection.Close();
                }
            }

            // Establish new P2P connection
            if (_userEndPoints.TryGetValue(targetUsername, out var endPointStr))
            {
                try
                {
                    var endPoint = IPEndPoint.Parse(endPointStr);
                    var p2pClient = new TcpClient();
                    
                    // Set a reasonable connection timeout
                    p2pClient.ReceiveTimeout = 5000;
                    p2pClient.SendTimeout = 5000;
                    
                    await p2pClient.ConnectAsync(endPoint);
                    Console.WriteLine($"Established P2P connection to {targetUsername} at {endPoint}");
                    
                    _p2pConnections[targetUsername] = p2pClient;
                    
                    // Send initial handshake message to identify ourselves
                    var handshakeMessage = new Message
                    {
                        Type = MessageType.Chat,
                        Sender = _username,
                        Receiver = targetUsername,
                        Content = "__HANDSHAKE__",
                        Timestamp = DateTime.UtcNow
                    };
                    
                    await SendP2PMessageAsync(p2pClient, handshakeMessage);
                    
                    // Wait a bit for handshake to be processed
                    await Task.Delay(100);
                    
                    // Now send the actual message
                    var message = new Message
                    {
                        Type = MessageType.Chat,
                        Sender = _username,
                        Receiver = targetUsername,
                        Content = messageContent,
                        Timestamp = DateTime.UtcNow
                    };

                    await SendP2PMessageAsync(p2pClient, message);
                    Console.WriteLine($"Sending to {targetUsername}: {messageContent}");

                    // Start handling this P2P connection for future messages
                    _ = Task.Run(() => HandleOutgoingP2PConnectionAsync(p2pClient, targetUsername));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to establish P2P connection with {targetUsername}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"User {targetUsername} not found in user list");
            }
        }

        private async Task HandleOutgoingP2PConnectionAsync(TcpClient client, string peerUsername)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];

            try
            {
                while (client.Connected && _isRunning)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    var messageJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var message = Message.FromJson(messageJson);
                    
                    if (message?.Type == MessageType.Chat && message.Content != "__HANDSHAKE__")
                    {
                        Console.WriteLine($"Received reply from {message.Sender}: {message.Content}");
                        OnMessageReceived?.Invoke($"{message.Sender}: {message.Content}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"P2P connection error with {peerUsername}: {ex.Message}");
            }
            finally
            {
                _p2pConnections.TryRemove(peerUsername, out _);
                OnUserDisconnected?.Invoke(peerUsername);
                client.Close();
            }
        }

        private async Task SendP2PMessageAsync(TcpClient client, Message message)
        {
            var json = message.ToJson();
            var data = Encoding.UTF8.GetBytes(json);
            Console.WriteLine($"Sending P2P message: {json}"); // Debug log
            await client.GetStream().WriteAsync(data, 0, data.Length);
            await client.GetStream().FlushAsync(); // Ensure message is sent immediately
        }

        private async Task SendToServerAsync(Message message)
        {
            if (_serverStream != null)
            {
                var json = message.ToJson();
                var data = Encoding.UTF8.GetBytes(json);
                await _serverStream.WriteAsync(data, 0, data.Length);
            }
        }

        private async Task RequestUserListAsync()
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
            _isRunning = false;

            // Send logout message to server
            if (_serverStream != null)
            {
                var logoutMessage = new Message
                {
                    Type = MessageType.Logout,
                    Sender = _username,
                    Timestamp = DateTime.UtcNow
                };
                await SendToServerAsync(logoutMessage);
            }

            // Close all P2P connections
            foreach (var connection in _p2pConnections.Values)
            {
                connection.Close();
            }
            _p2pConnections.Clear();

            // Close server connection
            _serverConnection?.Close();
            _p2pListener?.Stop();
        }

        public List<string> GetConnectedUsers()
        {
            return _userEndPoints.Keys.ToList();
        }
    }
}
