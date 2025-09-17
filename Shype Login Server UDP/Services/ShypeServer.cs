using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Linq;
using Shype_Login_Server_UDP.Models;

namespace Shype_Login_Server_UDP.Services
{
    public class ShypeServer(int port = 8080)
    {
        // UDP server bound to a port
        private readonly UdpClient _udpServer = new(new IPEndPoint(IPAddress.Any, port));
        // Online users and endpoints
        private readonly ConcurrentDictionary<string, User> _users = new();
        private readonly ConcurrentDictionary<string, IPEndPoint> _serverEndpoints = new();
        private bool _isRunning;

        public async Task StartAsync()
        {
            _isRunning = true;
            Console.WriteLine($"Shype UDP server listening on port {((IPEndPoint)_udpServer.Client.LocalEndPoint!).Port}");

            while (_isRunning)
            {
                try
                {
                    var result = await _udpServer.ReceiveAsync();
                    _ = Task.Run(() => HandleDatagramAsync(result));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Server receive error: {ex.Message}");
                }
            }
        }

        private async Task HandleDatagramAsync(UdpReceiveResult result)
        {
            var remoteEndPoint = result.RemoteEndPoint;
            var json = Encoding.UTF8.GetString(result.Buffer);
            var message = Message.FromJson(json);
            if (message == null) return;

            try
            {
                switch (message.Type)
                {
                    case MessageType.Login:
                        await HandleLoginAsync(message, remoteEndPoint);
                        break;
                    case MessageType.Logout:
                        await HandleLogoutAsync(message);
                        break;
                    case MessageType.UserList:
                        await SendUserListAsync(remoteEndPoint);
                        break;
                    case MessageType.P2PRequest:
                        await HandleP2PRequestAsync(message, remoteEndPoint);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Datagram handling error: {ex.Message}");
            }
        }

        private async Task HandleLoginAsync(Message message, IPEndPoint remoteEndPoint)
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
                await SendMessageAsync(remoteEndPoint, errorResponse);
                return;
            }

            var p2pEndPoint = new IPEndPoint(remoteEndPoint.Address, p2pPort);

            var user = new User(username, p2pEndPoint);
            _users[username] = user;
            _serverEndpoints[username] = remoteEndPoint;

            Console.WriteLine($"User {username} logged in with P2P endpoint {p2pEndPoint}");

            var response = new Message
            {
                Type = MessageType.Login,
                Content = "Login successful",
                Timestamp = DateTime.UtcNow
            };
            await SendMessageAsync(remoteEndPoint, response);

            // Small delay to help client be ready
            await Task.Delay(100);

            // Send current user list to the newly connected client first
            await SendUserListAsync(remoteEndPoint);

            // Then notify all other clients about the new user
            await Task.Delay(50);
            await BroadcastUserListUpdateAsync();
        }

        private async Task HandleLogoutAsync(Message message)
        {
            await HandleDisconnectionAsync(message.Sender);
        }

        private async Task HandleDisconnectionAsync(string username)
        {
            if (_users.TryRemove(username, out _))
            {
                _serverEndpoints.TryRemove(username, out _);
                Console.WriteLine($"User {username} disconnected");
                await BroadcastUserListUpdateAsync();
            }
        }

        private async Task SendUserListAsync(IPEndPoint destination)
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

            await SendMessageAsync(destination, response);
        }

        private async Task HandleP2PRequestAsync(Message message, IPEndPoint destination)
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
                await SendMessageAsync(destination, response);
            }
            else
            {
                var response = new Message
                {
                    Type = MessageType.P2PResponse,
                    Content = "User not found",
                    Timestamp = DateTime.UtcNow
                };
                await SendMessageAsync(destination, response);
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

            var tasks = _serverEndpoints.Values.Select(async ep =>
            {
                try
                {
                    await SendMessageAsync(ep, message);
                }
                catch
                {
                    // Ignore send errors (client may be gone)
                }
            });

            await Task.WhenAll(tasks);
        }

        private async Task SendMessageAsync(IPEndPoint destination, Message message)
        {
            var json = message.ToJson();
            var data = Encoding.UTF8.GetBytes(json);
            await _udpServer.SendAsync(data, data.Length, destination);
        }

        public void Stop()
        {
            _isRunning = false;

            // Notify all clients about server shutdown
            var shutdownMessage = new Message
            {
                Type = MessageType.ServerShutdown,
                Content = "Server is shutting down",
                Timestamp = DateTime.UtcNow
            };

            var tasks = _serverEndpoints.Values.Select(async ep =>
            {
                try
                {
                    await SendMessageAsync(ep, shutdownMessage);
                }
                catch { }
            }).ToArray();

            try
            {
                Task.WaitAll(tasks, TimeSpan.FromSeconds(3));
            }
            catch { }

            _udpServer.Close();
        }
    }
}
