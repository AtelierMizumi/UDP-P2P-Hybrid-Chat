using System.Text.Json;

namespace Shype_Login_Server_TCP.Models
{
    public enum MessageType
    {
        Login,
        Logout,
        Chat,
        UserList,
        P2PRequest,
        P2PResponse,
        ServerShutdown,
        Presence // P2P presence: Join/Leave/Ping
    }

    public class Message
    {
        public MessageType Type { get; set; }
        public string Sender { get; set; } = string.Empty;
        public string Receiver { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
        public DateTime Timestamp { get; set; }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        public static Message? FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<Message>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
            }
            catch
            {
                return null;
            }
        }
    }
}
