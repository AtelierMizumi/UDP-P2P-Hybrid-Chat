using Shype_Login_Server_UDP.Services;

namespace Shype_Login_Server_UDP
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Shype P2P Chat System made by thuanc177");
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

            if (await client.ConnectToServerAsync(serverAddress, 8080))
            {
                // Start the Terminal UI
                var ui = new ChatUi(username, client);
                ui.Run();

                // Ensure disconnect on exit
                await client.DisconnectAsync();
            }
            else
            {
                Console.WriteLine("❌ Failed to connect to server");
            }
        }
    }
}
