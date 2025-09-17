// filepath: /home/thuanc177/RiderProjects/Shype Login Server UDP/Shype Login Server UDP/Services/ChatUi.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Terminal.Gui;
using Shype_Login_Server_UDP.Services;
using System.Runtime.InteropServices;
using System.IO;

namespace Shype_Login_Server_UDP.Services
{
    public class ChatUi
    {
        private readonly string _username;
        private readonly ShypeClient _client;

        // UI elements
        private Window? _win;
        private TextView? _messagesView;
        private TextField? _input;
        private ListView? _userListView;
        private StatusBar? _statusBar; // added status bar

        // State
        private readonly List<string> _allMessages = new();
        private readonly Dictionary<string, List<string>> _messagesByPeer = new();
        private readonly Dictionary<string, int> _unreadByPeer = new();
        private readonly SortedSet<string> _peers = new();
        private string _currentView = AllLabel;

        private const string AllLabel = "[All]";

        public ChatUi(string username, ShypeClient client)
        {
            _username = username;
            _client = client;
        }

        public void Run()
        {
            // On Linux, proactively load ncurses from repo-local paths to support NixOS
            TryPreloadNcurses();
            try
            {
                Application.Init();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Terminal UI failed to initialize.");
                Console.WriteLine($"Reason: {ex.Message}");
                Console.WriteLine("This typically means the ncurses wide-character library is missing or not discoverable.");
                Console.WriteLine("Install it or set a path and try again:");
                Console.WriteLine("- Repo-local: put libncursesw.so.6 in './lib' or '<bin dir>/lib' (we try both)");
                Console.WriteLine("- Env var:   export SHYPE_NCURSES_PATH=/absolute/path/to/libncursesw.so.6");
                Console.WriteLine("- Ubuntu:    sudo apt-get install -y libncursesw6  | Fedora: sudo dnf install -y ncurses-compat-libs");
                Console.WriteLine("Exiting client...");
                return;
            }

            _win = new Window($"Shype - {_username}")
            {
                X = 0,
                Y = 0, // use full screen; removed header row
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            // removed the top green hint header to avoid duplication and save space
            // _header = new Label(...)
            // Application.Top.Add(_header);

            // Left panel (75%): messages + input
            var leftPanel = new FrameView("Messages")
            {
                X = 0,
                Y = 0,
                Width = Dim.Percent(75),
                Height = Dim.Fill(),
                ColorScheme = Colors.Dialog
            };
            _messagesView = new TextView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 1, // a bit taller; only reserve 1 line for input
                ReadOnly = true,
                WordWrap = true
            };
            // Defer scrollbar creation until after adding to a parent
            _input = new TextField("")
            {
                X = 0,
                Y = Pos.Bottom(_messagesView),
                Width = Dim.Fill(),
                Height = 1
            };
            _input.KeyPress += async (e) =>
            {
                // Global-ish shortcuts in input
                if (e.KeyEvent.Key == Key.Tab)
                {
                    e.Handled = true;
                    _userListView?.SetFocus();
                    return;
                }
                if (e.KeyEvent.Key == Key.F1)
                {
                    e.Handled = true;
                    ShowHelp();
                    return;
                }
                if (e.KeyEvent.Key == Key.Esc)
                {
                    e.Handled = true;
                    SetCurrentView(AllLabel);
                    return;
                }
                if (e.KeyEvent.Key == (Key.CtrlMask | Key.Q))
                {
                    e.Handled = true;
                    _ = Task.Run(async () => await _client.DisconnectAsync());
                    Application.RequestStop();
                    return;
                }

                if (e.KeyEvent.Key == Key.Enter)
                {
                    e.Handled = true;
                    var text = _input.Text.ToString() ?? string.Empty;
                    _input.Text = string.Empty;
                    if (string.IsNullOrWhiteSpace(text)) return;

                    if (text.StartsWith("/"))
                    {
                        await HandleCommandAsync(text);
                    }
                    else
                    {
                        await SendMessageAsync(text);
                    }
                }
            };
            leftPanel.Add(_messagesView);

            // Now host has a SuperView; safe to create scrollbars for messages view
            var msgScroll = new ScrollBarView(_messagesView, true);
            msgScroll.ChangedPosition += () =>
            {
                _messagesView.TopRow = msgScroll.Position;
                if (_messagesView.TopRow != msgScroll.Position)
                    msgScroll.Position = _messagesView.TopRow;
                _messagesView.SetNeedsDisplay();
            };
            msgScroll.OtherScrollBarView.ChangedPosition += () =>
            {
                _messagesView.LeftColumn = msgScroll.OtherScrollBarView.Position;
                if (_messagesView.LeftColumn != msgScroll.OtherScrollBarView.Position)
                    msgScroll.OtherScrollBarView.Position = _messagesView.LeftColumn;
                _messagesView.SetNeedsDisplay();
            };
            _messagesView.DrawContent += _ =>
            {
                // Compute content size: number of lines and max line length
                int lines = 0;
                int maxWidth = 0;
                try
                {
                    var text = _messagesView.Text?.ToString() ?? string.Empty;
                    var split = text.Split('\n');
                    lines = split.Length;
                    foreach (var line in split)
                    {
                        var len = line.Length; // approximate width; good enough for ASCII/most cases
                        if (len > maxWidth) maxWidth = len;
                    }
                }
                catch { }

                msgScroll.Size = Math.Max(lines, 0);
                msgScroll.Position = _messagesView.TopRow;
                msgScroll.OtherScrollBarView.Size = Math.Max(maxWidth, 0);
                msgScroll.OtherScrollBarView.Position = _messagesView.LeftColumn;
                msgScroll.Refresh();
            };

            leftPanel.Add(_input);

            // Right panel (25%): users list with unread badges
            var rightPanel = new FrameView("Users")
            {
                X = Pos.Right(leftPanel),
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ColorScheme = Colors.Menu
            };
            _userListView = new ListView(new List<string>())
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            rightPanel.Add(_userListView);

            // Now host has a SuperView; safe to create scrollbar for user list
            var userScroll = new ScrollBarView(_userListView, true);
            userScroll.ChangedPosition += () =>
            {
                _userListView.TopItem = userScroll.Position;
                if (_userListView.TopItem != userScroll.Position)
                    userScroll.Position = _userListView.TopItem;
                _userListView.SetNeedsDisplay();
            };
            _userListView.DrawContent += _ =>
            {
                var count = 0;
                try { count = _userListView.Source?.Count ?? 0; } catch { }
                userScroll.Size = count;
                userScroll.Position = _userListView.TopItem;
                userScroll.Refresh();
            };

            _userListView.OpenSelectedItem += (args) =>
            {
                var label = args.Value.ToString() ?? AllLabel;
                var user = ExtractUserFromListItem(label);
                SetCurrentView(user);
                _input?.SetFocus();
            };
            _userListView.KeyPress += (e) =>
            {
                if (e.KeyEvent.Key == Key.Tab)
                {
                    e.Handled = true;
                    _input?.SetFocus();
                    return;
                }
                if (e.KeyEvent.Key == Key.F1)
                {
                    e.Handled = true;
                    ShowHelp();
                    return;
                }
                if (e.KeyEvent.Key == Key.Esc)
                {
                    e.Handled = true;
                    SetCurrentView(AllLabel);
                    _input?.SetFocus();
                    return;
                }
            };
            // removed duplicate: rightPanel.Add(_userListView);

            // Status bar with shortcuts
            _statusBar = new StatusBar(new StatusItem[]
            {
                new StatusItem(Key.F1, "~F1~ Help", () => ShowHelp()),
                new StatusItem(Key.Tab, "~Tab~ Switch Pane", () =>
                {
                    // Directly toggle focus between input and user list
                    if (_input?.HasFocus == true) _userListView?.SetFocus(); else _input?.SetFocus();
                }),
                new StatusItem(Key.Enter, "~Enter~ Send", () => { }),
                new StatusItem(Key.CtrlMask | Key.Q, "~Ctrl+Q~ Quit", () =>
                {
                    _ = Task.Run(async () => await _client.DisconnectAsync());
                    Application.RequestStop();
                })
            });

            // Attach panels to main window (previously missing)
            _win.Add(leftPanel);
            _win.Add(rightPanel);

            Application.Top.Add(_win);
            Application.Top.Add(_statusBar);

            // Hook client events
            _client.OnUserListUpdated += users => Application.MainLoop.Invoke(() => UpdateUserList(users));
            _client.OnUserDisconnected += user => Application.MainLoop.Invoke(() => OnPeerDisconnected(user));
            _client.OnChatReceived += (from, content) => Application.MainLoop.Invoke(() => OnIncomingMessage(from, content));

            // Proactively request user list on UI startup
            _ = Task.Run(async () => { try { await _client.RequestUserListAsync(); } catch { } });

            // Seed initial list with All
            RefreshUserListView();
            RefreshMessagesView();

            // Start with input focused for quick typing
            _input.SetFocus();

            Application.Run();
            Application.Shutdown();
        }

        private async Task HandleCommandAsync(string cmd)
        {
            var parts = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = parts[0].ToLowerInvariant();
            switch (name)
            {
                case "/chat":
                    if (parts.Length < 2)
                    {
                        AppendSystem($"Usage: /chat <username>");
                        return;
                    }
                    SetCurrentView(parts[1]);
                    break;
                case "/end":
                    SetCurrentView(AllLabel);
                    break;
                case "/users":
                    AppendSystem("Requesting users list from server...");
                    try { await _client.RequestUserListAsync(); } catch { }
                    // UI will refresh when event arrives; still refresh now
                    RefreshUserListView();
                    break;
                case "/help":
                    ShowHelp();
                    break;
                case "/quit":
                case "/exit":
                    _ = Task.Run(async () => await _client.DisconnectAsync());
                    Application.RequestStop();
                    break;
                default:
                    AppendSystem($"Unknown command: {name}");
                    break;
            }
        }

        private async Task SendMessageAsync(string text)
        {
            if (_currentView == AllLabel)
            {
                // Broadcast to all peers
                if (_peers.Count == 0)
                {
                    AppendSystem("No peers online to broadcast to");
                    return;
                }

                AppendOwn($"You → All: {text}");
                _allMessages.Add($"{_username}: {text}");

                foreach (var peer in _peers)
                {
                    if (!_messagesByPeer.TryGetValue(peer, out var msgs))
                    {
                        msgs = new List<string>();
                        _messagesByPeer[peer] = msgs;
                    }

                    // Track per-peer and send
                    msgs.Add($"You: {text}");
                    _ = Task.Run(async () => await _client.SendChatMessageAsync(peer, text));
                }

                RefreshMessagesView();
                return; // avoid falling through to single-recipient path
            }

            // Single-recipient path
            var to = _currentView;
            AppendOwn($"You → {to}: {text}");

            // Track in buffers
            _allMessages.Add($"{_username}: {text}");
            if (!_messagesByPeer.TryGetValue(to, out var msgsTo))
            {
                msgsTo = new List<string>();
                _messagesByPeer[to] = msgsTo;
            }

            _ = Task.Run(async () => await _client.SendChatMessageAsync(to, text));
            msgsTo.Add($"You: {text}");

            RefreshMessagesView();
        }

        private void OnIncomingMessage(string from, string content)
        {
            _allMessages.Add($"{from}: {content}");

            if (!_messagesByPeer.TryGetValue(from, out var msgsFrom))
            {
                msgsFrom = new List<string>();
                _messagesByPeer[from] = msgsFrom;
            }

            // Increment unread if we're not currently viewing this peer
            if (_currentView != from)
            {
                _unreadByPeer[from] = (_unreadByPeer.TryGetValue(from, out var c) ? c : 0) + 1;
            }

            msgsFrom.Add($"{from}: {content}");

            RefreshMessagesView();
            RefreshUserListView();
        }

        private void UpdateUserList(IEnumerable<string> users)
        {
            // Materialize to avoid multiple enumeration issues
            var newSet = new SortedSet<string>(users);

            // Compute changes
            bool changed = !_peers.SetEquals(newSet);
            if (!changed) return;

            // Remove peers that disappeared
            var removed = _peers.Except(newSet).ToList();
            foreach (var u in removed)
            {
                _unreadByPeer.Remove(u);
                _messagesByPeer.Remove(u);
            }

            // Replace set atomically
            _peers.Clear();
            foreach (var u in newSet) _peers.Add(u);

            // Ensure current view is valid
            if (_currentView != AllLabel && !_peers.Contains(_currentView))
            {
                _currentView = AllLabel;
                RefreshMessagesView();
            }

            RefreshUserListView();
        }

        private void OnPeerDisconnected(string user)
        {
            if (_peers.Remove(user))
            {
                _unreadByPeer.Remove(user);
                if (_currentView == user)
                {
                    _currentView = AllLabel;
                    AppendSystem($"{user} disconnected. Switched to {AllLabel} view.");
                    RefreshMessagesView();
                }
                RefreshUserListView();
            }
        }

        private void SetCurrentView(string user)
        {
            _currentView = user;
            if (user != AllLabel)
            {
                _unreadByPeer.Remove(user);
            }
            RefreshUserListView();
            RefreshMessagesView();
        }

        private void RefreshUserListView()
        {
            if (_userListView == null) return;

            var items = new List<string> { AllLabel };
            foreach (var u in _peers)
            {
                if (_unreadByPeer.TryGetValue(u, out var c) && c > 0)
                {
                    // show count; if large, cap display
                    var badge = c > 99 ? "99+" : c.ToString();
                    items.Add($"{u} ({badge})");
                }
                else
                {
                    items.Add(u);
                }
            }

            // Highlight current selection
            _userListView.SetSource(items);
            var idx = items.FindIndex(i => ExtractUserFromListItem(i) == _currentView);
            if (idx < 0) idx = 0;
            _userListView.SelectedItem = idx;
            // EnsureVisible not available in Terminal.Gui 1.14 for ListView; rely on selection and scrollbar
        }

        private static string ExtractUserFromListItem(string label)
        {
            // Strip badges like "name (3)" or "name (99+)"
            var paren = label.IndexOf('(');
            var core = paren > 0 ? label[..(paren - 1)].TrimEnd() : label;
            return core;
        }

        private void RefreshMessagesView()
        {
            if (_messagesView == null) return;

            List<string> lines;
            if (_currentView == AllLabel)
            {
                lines = _allMessages;
            }
            else
            {
                if (!_messagesByPeer.TryGetValue(_currentView, out var msgsCurr))
                {
                    msgsCurr = new List<string>();
                    _messagesByPeer[_currentView] = msgsCurr;
                }
                lines = msgsCurr;
            }

            UpdateTitle();
            _messagesView.Text = string.Join('\n', lines.TakeLast(500));
        }

        private void UpdateTitle()
        {
            if (_win == null) return;
            _win.Title = _currentView == AllLabel ? $"Messages - All" : $"Messages - {_currentView}";
        }

        private void AppendSystem(string line)
        {
            var msg = $"[System] {line}";
            _allMessages.Add(msg);
            if (_currentView == AllLabel)
            {
                _messagesView!.Text = string.Join('\n', _allMessages.TakeLast(500));
            }
        }

        private void AppendOwn(string line)
        {
            _allMessages.Add(line);
            if (_currentView == AllLabel)
            {
                _messagesView!.Text = string.Join('\n', _allMessages.TakeLast(500));
            }
        }

        private void ShowHelp()
        {
            var help = "Commands:\n" +
                       "/chat <user>  - Focus a user chat\n" +
                       "/end          - Switch back to All view\n" +
                       "/users        - Refresh user list\n" +
                       "/quit|/exit   - Quit client\n\n" +
                       "Shortcuts:\n" +
                       "F1 Help, Tab Switch Pane, Enter Send, Esc All, Ctrl+Q Quit";
            MessageBox.Query(50, 14, "Help", help, "OK");
        }

        private void TryPreloadNcurses()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

                // If an explicit override is provided, try that first
                var overridePath = Environment.GetEnvironmentVariable("SHYPE_NCURSES_PATH");
                if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                {
                    NativeLibrary.Load(overridePath);
                    return;
                }

                var candidates = new List<string>();

                // Gather likely roots to search: baseDir, its parents, and CWD
                var baseDir = AppContext.BaseDirectory?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
                var cwd = Directory.GetCurrentDirectory();
                var roots = new HashSet<string>(StringComparer.Ordinal)
                {
                    baseDir
                };
                if (!string.IsNullOrEmpty(cwd)) roots.Add(cwd);
                // Walk up a few parents from baseDir to catch repo root
                var dir = baseDir;
                for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
                {
                    var parent = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(parent) || parent == dir) break;
                    roots.Add(parent);
                    dir = parent;
                }

                foreach (var root in roots)
                {
                    candidates.Add(Path.Combine(root, "lib", "libncursesw.so.6"));
                    candidates.Add(Path.Combine(root, "lib", "libncursesw.so.5"));
                    candidates.Add(Path.Combine(root, "lib", "libncursesw.so"));
                }

                // Also consider LD_LIBRARY_PATH entries
                var ld = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
                if (!string.IsNullOrWhiteSpace(ld))
                {
                    foreach (var p in ld.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        candidates.Add(Path.Combine(p, "libncursesw.so.6"));
                        candidates.Add(Path.Combine(p, "libncursesw.so.5"));
                        candidates.Add(Path.Combine(p, "libncursesw.so"));
                    }
                }

                foreach (var path in candidates)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            NativeLibrary.Load(path);
                            return;
                        }
                    }
                    catch { /* try next */ }
                }
                // As a last resort, attempt by name in case loader paths are already set
                try { NativeLibrary.Load("libncursesw.so.6"); return; } catch { }
                try { NativeLibrary.Load("libncursesw.so.5"); return; } catch { }
                try { NativeLibrary.Load("libncursesw.so"); return; } catch { }
            }
            catch
            {
                // Ignore; Application.Init will report a clearer error if load fails
            }
        }
    }
}
