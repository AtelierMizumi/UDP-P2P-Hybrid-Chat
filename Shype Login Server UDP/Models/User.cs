using System.Net;

namespace Shype_Login_Server_UDP.Models
{
    public class User
    {
        public string Username { get; set; } = string.Empty;
        public IPEndPoint? P2PEndPoint { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsOnline { get; set; } 
        
        public User(string username, IPEndPoint? p2pEndPoint = null)
        {
            Username = username;
            P2PEndPoint = p2pEndPoint;
            LastSeen = DateTime.UtcNow;
            IsOnline = true;
        }
    }

    // DTO for JSON serialization of user list
    public class UserDto
    {
        public string Username { get; set; } = string.Empty;
        public string EndPoint { get; set; } = string.Empty;
    }
}
