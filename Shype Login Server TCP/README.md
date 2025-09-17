# Shype P2P Messaging System

A hybrid P2P messaging application similar to Skype that uses TCP protocol for communication.

## Features

✅ **Server-assisted connection discovery**: Server helps clients find and connect to each other  
✅ **P2P messaging**: Direct client-to-client communication after connection establishment  
✅ **Dynamic port allocation**: Each user gets a unique P2P port for direct connections  
✅ **Fallback mechanisms**: Continues working even when server is unavailable  
✅ **User management**: Login, logout, and user list functionality  
✅ **Real-time messaging**: Instant message delivery via server or P2P  
✅ **Graceful degradation**: Switches to P2P-only mode when server is unavailable  

## Architecture

### Hybrid P2P Model
1. **Server Role**: Facilitates initial user discovery and connection establishment
2. **Client Role**: Can communicate via server or directly P2P
3. **Fallback**: When server is unavailable, clients can still chat P2P

### Communication Flow
1. Client logs in to server with username
2. Server assigns dynamic P2P port and shares user list
3. Clients can chat via server relay or establish direct P2P connections
4. If server goes down, existing P2P connections continue working

## How to Use

### Starting the Server
```bash
dotnet run
# Choose option 1 (Start Server)
# Enter port (default: 8888)
```

### Starting a Client
```bash
dotnet run
# Choose option 2 (Start Client) 
# Enter server address (default: 127.0.0.1)
# Enter server port (default: 8888)
# Enter your username
```

### Client Commands
- `help` - Show available commands
- `users` - List all known users
- `chat <username> <message>` - Send message to a user
- `status` - Show connection status
- `quit` - Exit application

## Example Usage

### Terminal 1 - Server
```
=== Shype P2P Messaging System ===
1. Start Server
2. Start Client
Choose option (1 or 2): 1
Enter server port (default 8888): 
Shype Server started on port 8888
Waiting for client connections...
```

### Terminal 2 - Client 1 (Alice)
```
=== Shype P2P Messaging System ===
1. Start Server
2. Start Client
Choose option (1 or 2): 2
Enter server address (default 127.0.0.1): 
Enter server port (default 8888): 
Enter your username: Alice
Connecting to server...
*** Connected to server ***
Login successful! Your P2P port: 12345

--- Online Users (0) ---
------------------------

Enter command (or 'help' for commands): 
```

### Terminal 3 - Client 2 (Bob)
```
=== Shype P2P Messaging System ===
1. Start Server
2. Start Client
Choose option (1 or 2): 2
Enter server address (default 127.0.0.1): 
Enter server port (default 8888): 
Enter your username: Bob
Connecting to server...
*** Connected to server ***
Login successful! Your P2P port: 54321

--- Online Users (1) ---
- Alice (Online) [P2P Port: 12345]
------------------------

Enter command (or 'help' for commands): chat Alice Hello Alice!
Sending to Alice: Hello Alice!
```

### Chatting Example
Alice will see:
```
[14:30:25] Bob: Hello Alice!
Enter command (or 'help' for commands): chat Bob Hi Bob! How are you?
Sending to Bob: Hi Bob! How are you?
```

## Technical Implementation

### Message Types
- `Login` - User authentication and P2P port assignment
- `Logout` - User disconnection
- `UserList` - Request/receive list of online users
- `Chat` - Text messages between users
- `P2PRequest` - Request direct P2P connection
- `P2PResponse` - Response to P2P connection request
- `Heartbeat` - Keep connection alive
- `ServerShutdown` - Server graceful shutdown notification

### Network Architecture
- **Server Port**: Default 8888 (configurable)
- **P2P Ports**: Dynamically assigned (10000-65535 range)
- **Protocol**: TCP with JSON message format
- **Fallback**: Server relay → P2P direct → Connection failed

### Resilience Features
- Automatic P2P connection establishment
- Server reconnection attempts
- Graceful handling of network failures
- Heartbeat mechanism for connection health
- Seamless transition between server and P2P modes

## Project Structure
```
├── Program.cs              # Main application entry point
├── Models/
│   ├── User.cs            # User data model
│   └── Message.cs         # Message protocol definition
└── Services/
    ├── ShypeServer.cs     # Server implementation
    └── ShypeClient.cs     # Client implementation
```

## Requirements
- .NET 9.0
- TCP network connectivity
- Available ports for P2P communication

## Testing Scenarios

1. **Normal Operation**: Start server, connect multiple clients, exchange messages
2. **Server Shutdown**: Stop server while clients are connected - they should continue P2P
3. **Network Interruption**: Disconnect/reconnect clients to test resilience
4. **P2P Direct**: Test direct messaging between clients without server relay
