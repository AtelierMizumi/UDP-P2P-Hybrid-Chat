using Shype_Login_Server_TCP.Services;

namespace Shype_Login_Server_TCP
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Shype P2P Chat System");
            Console.WriteLine("1. Start Server");
            Console.WriteLine("2. Start Client");
            Console.Write("Choose option: ");
            
            var choice = Console.ReadLine();
            
            switch (choice)
            {
                case "1":
                    await StartServerAsync();
                    break;
                case "2":
                    await StartClientAsync();
                    break;
                default:
                    Console.WriteLine("Invalid option");
                    break;
            }
        }

        static async Task StartServerAsync()
        {
            var server = new ShypeServer(8080);
            
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                server.Stop();
                Environment.Exit(0);
            };

            await server.StartAsync();
        }

        static async Task StartClientAsync()
        {
            Console.Write("Enter your username: ");
            var username = Console.ReadLine();
            
            if (string.IsNullOrEmpty(username))
            {
                Console.WriteLine("Username cannot be empty");
                return;
            }

            Console.Write("Enter server address (default: localhost): ");
            var serverAddress = Console.ReadLine();
            if (string.IsNullOrEmpty(serverAddress))
                serverAddress = "localhost";

            var client = new ShypeClient(username);
            string? currentChatUser = null;
            
            // Set up event handlers
            client.OnMessageReceived += message =>
            {
                Console.WriteLine($"\n📨 {message}");
                if (currentChatUser != null)
                {
                    Console.Write($"[{currentChatUser}] ");
                }
                else
                {
                    Console.Write(">> ");
                }
            };
            
            client.OnUserListUpdated += users =>
            {
                Console.WriteLine($"\n👥 Online users: {string.Join(", ", users)}");
                if (currentChatUser != null)
                {
                    Console.Write($"[{currentChatUser}] ");
                }
                else
                {
                    Console.Write(">> ");
                }
            };
            
            client.OnUserDisconnected += disconnectedUser =>
            {
                Console.WriteLine($"\n❌ {disconnectedUser} disconnected");
                if (currentChatUser == disconnectedUser)
                {
                    Console.WriteLine($"💬 Chat with {disconnectedUser} ended (user disconnected)");
                    currentChatUser = null;
                }
                
                if (currentChatUser != null)
                {
                    Console.Write($"[{currentChatUser}] ");
                }
                else
                {
                    Console.Write(">> ");
                }
            };

            if (await client.ConnectToServerAsync(serverAddress, 8080))
            {
                Console.WriteLine("✅ Connected to server successfully!");
                Console.WriteLine("\n📋 Commands:");
                Console.WriteLine("  /chat <username> - Start chatting with a user");
                Console.WriteLine("  /users - Show online users");
                Console.WriteLine("  /end - End current chat");
                Console.WriteLine("  /quit - Exit application");
                Console.WriteLine("  Type normally to send messages when in chat mode\n");

                // Command loop
                while (true)
                {
                    if (currentChatUser != null)
                    {
                        Console.Write($"[{currentChatUser}] ");
                    }
                    else
                    {
                        Console.Write(">> ");
                    }
                    
                    var input = Console.ReadLine();
                    
                    if (string.IsNullOrEmpty(input)) continue;
                    
                    // Handle commands
                    if (input.StartsWith("/"))
                    {
                        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var command = parts[0].ToLower();
                        
                        switch (command)
                        {
                            case "/chat":
                                if (parts.Length < 2)
                                {
                                    Console.WriteLine("❓ Usage: /chat <username>");
                                    break;
                                }
                                
                                var targetUser = parts[1];
                                var users = client.GetConnectedUsers();
                                
                                if (users.Contains(targetUser))
                                {
                                    currentChatUser = targetUser;
                                    Console.WriteLine($"💬 Now chatting with {targetUser}. Type your messages below:");
                                    Console.WriteLine("    Use /end to stop chatting with this user");
                                }
                                else
                                {
                                    Console.WriteLine($"❌ User '{targetUser}' not found or offline");
                                    Console.WriteLine($"👥 Available users: {string.Join(", ", users)}");
                                }
                                break;
                                
                            case "/users":
                                var onlineUsers = client.GetConnectedUsers();
                                if (onlineUsers.Count == 0)
                                {
                                    Console.WriteLine("👥 No other users online");
                                }
                                else
                                {
                                    Console.WriteLine($"👥 Online users: {string.Join(", ", onlineUsers)}");
                                }
                                break;
                                
                            case "/end":
                                if (currentChatUser != null)
                                {
                                    Console.WriteLine($"💬 Ended chat with {currentChatUser}");
                                    currentChatUser = null;
                                }
                                else
                                {
                                    Console.WriteLine("❌ No active chat to end");
                                }
                                break;
                                
                            case "/quit":
                            case "/exit":
                                Console.WriteLine("👋 Logging out...");
                                await client.DisconnectAsync();
                                return;
                                
                            case "/help":
                                Console.WriteLine("\n📋 Available commands:");
                                Console.WriteLine("  /chat <username> - Start chatting with a user");
                                Console.WriteLine("  /users - Show online users");
                                Console.WriteLine("  /end - End current chat");
                                Console.WriteLine("  /help - Show this help");
                                Console.WriteLine("  /quit - Exit application");
                                Console.WriteLine("  Type normally to send messages when in chat mode");
                                break;
                                
                            default:
                                Console.WriteLine($"❓ Unknown command: {command}. Type /help for available commands");
                                break;
                        }
                    }
                    else
                    {
                        // Regular message - send to current chat user
                        if (currentChatUser != null)
                        {
                            await client.SendChatMessageAsync(currentChatUser, input);
                            Console.WriteLine($"✅ You: {input}");
                        }
                        else
                        {
                            Console.WriteLine("❓ No active chat. Use /chat <username> to start chatting with someone");
                            Console.WriteLine("   Use /users to see who's online, or /help for all commands");
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine("❌ Failed to connect to server");
            }
        }
    }
}
