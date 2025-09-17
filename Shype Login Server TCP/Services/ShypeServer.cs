using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Shype_Login_Server_TCP.Models;

namespace Shype_Login_Server_TCP.Services
{
    public class ShypeServer(int port = 8080)
    {
        private readonly TcpListener _listener = new(IPAddress.Any, port);
        private readonly ConcurrentDictionary<string, User> _users = new();
        private readonly ConcurrentDictionary<string, TcpClient> _clients = new();
        private bool _isRunning;

        public async Task StartAsync()
        {
            _listener.Start();
            _isRunning = true;
            Console.WriteLine($"Shype server started on port {((IPEndPoint)_listener.LocalEndpoint).Port}");

            while (_isRunning)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            string? username = null;

            try
            {
                while (client.Connected && _isRunning)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    var messageJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var message = Message.FromJson(messageJson);
                    
                    if (message == null) continue;

                    switch (message.Type)
                    {
                        case MessageType.Login:
                            username = await HandleLoginAsync(message, client, stream);
                            break;
                        case MessageType.Logout:
                            await HandleLogoutAsync(message);
                            return;
                        case MessageType.UserList:
                            await SendUserListAsync(stream);
                            break;
                        case MessageType.P2PRequest:
                            await HandleP2PRequestAsync(message, stream);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client error: {ex.Message}");
            }
            finally
            {
                if (username != null)
                {
                    await HandleDisconnectionAsync(username);
                }
                client.Close();
            }
        }

        private async Task<string?> HandleLoginAsync(Message message, TcpClient client, NetworkStream stream)
        {
            var username = message.Sender;
            int p2pPort = 0;
            
            // Properly handle JsonElement for P2PPort
            if (message.Data.ContainsKey("P2PPort"))
            {
                var portValue = message.Data["P2PPort"];
                if (portValue is JsonElement jsonElement)
                {
                    p2pPort = jsonElement.GetInt32();
                }
                else if (portValue is int intValue)
                {
                    p2pPort = intValue;
                }
            }

            if (string.IsNullOrEmpty(username) || _users.ContainsKey(username))
            {
                var errorResponse = new Message
                {
                    Type = MessageType.Login,
                    Content = "Login failed: Username already taken or invalid",
                    Timestamp = DateTime.UtcNow
                };
                await SendMessageAsync(stream, errorResponse);
                return null;
            }

            var clientEndPoint = (IPEndPoint)client.Client.RemoteEndPoint!;
            var p2pEndPoint = new IPEndPoint(clientEndPoint.Address, p2pPort);

            var user = new User(username, p2pEndPoint);
            _users[username] = user;
            _clients[username] = client;

            Console.WriteLine($"User {username} logged in with P2P endpoint {p2pEndPoint}");

            var response = new Message
            {
                Type = MessageType.Login,
                Content = "Login successful",
                Timestamp = DateTime.UtcNow
            };
            await SendMessageAsync(stream, response);

            // Add small delay to ensure client is ready to receive messages
            await Task.Delay(100);
            
            // Send current user list to the newly connected client first
            await SendUserListAsync(stream);
            
            // Add another small delay before broadcasting to other clients
            await Task.Delay(50);
            
            // Then notify all other clients about the new user
            await BroadcastUserListUpdateAsync();
            
            return username;
        }

        private async Task HandleLogoutAsync(Message message)
        {
            await HandleDisconnectionAsync(message.Sender);
        }

        private async Task HandleDisconnectionAsync(string username)
        {
            if (_users.TryRemove(username, out _))
            {
                _clients.TryRemove(username, out _);
                Console.WriteLine($"User {username} disconnected");
                await BroadcastUserListUpdateAsync();
            }
        }

        private async Task SendUserListAsync(NetworkStream stream)
        {
            var userList = _users.Values.Where(u => u.IsOnline).Select(u => new UserDto
            {
                Username = u.Username,
                EndPoint = u.P2PEndPoint?.ToString() ?? ""
            }).ToList();

            var response = new Message
            {
                Type = MessageType.UserList,
                Content = "User list",
                Data = new Dictionary<string, object> { ["Users"] = userList },
                Timestamp = DateTime.UtcNow
            };

            await SendMessageAsync(stream, response);
        }

        private async Task HandleP2PRequestAsync(Message message, NetworkStream stream)
        {
            var targetUsername = message.Receiver;
            if (_users.TryGetValue(targetUsername, out var targetUser))
            {
                var response = new Message
                {
                    Type = MessageType.P2PResponse,
                    Content = "User found",
                    Data = new Dictionary<string, object>
                    {
                        ["Username"] = targetUser.Username,
                        ["EndPoint"] = targetUser.P2PEndPoint?.ToString() ?? ""
                    },
                    Timestamp = DateTime.UtcNow
                };
                await SendMessageAsync(stream, response);
            }
            else
            {
                var response = new Message
                {
                    Type = MessageType.P2PResponse,
                    Content = "User not found",
                    Timestamp = DateTime.UtcNow
                };
                await SendMessageAsync(stream, response);
            }
        }

        private async Task BroadcastUserListUpdateAsync()
        {
            var userList = _users.Values.Where(u => u.IsOnline).Select(u => new UserDto
            {
                Username = u.Username,
                EndPoint = u.P2PEndPoint?.ToString() ?? ""
            }).ToList();

            var message = new Message
            {
                Type = MessageType.UserList,
                Content = "User list updated",
                Data = new Dictionary<string, object> { ["Users"] = userList },
                Timestamp = DateTime.UtcNow
            };

            var tasks = _clients.Values.Select(async client =>
            {
                try
                {
                    await SendMessageAsync(client.GetStream(), message);
                }
                catch
                {
                    // Client may be disconnected
                }
            });

            await Task.WhenAll(tasks);
        }

        private async Task SendMessageAsync(NetworkStream stream, Message message)
        {
            var json = message.ToJson();
            var data = Encoding.UTF8.GetBytes(json);
            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync(); // Ensure message is sent immediately
        }

        public void Stop()
        {
            _isRunning = false;
            _listener?.Stop();
            
            // Notify all clients about server shutdown
            var shutdownMessage = new Message
            {
                Type = MessageType.ServerShutdown,
                Content = "Server is shutting down",
                Timestamp = DateTime.UtcNow
            };

            var tasks = _clients.Values.Select(async client =>
            {
                try
                {
                    await SendMessageAsync(client.GetStream(), shutdownMessage);
                    client.Close();
                }
                catch { }
            });

            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));
        }
    }
}
