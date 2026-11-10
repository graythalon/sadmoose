using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Drift.Protocol;

namespace Drift.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PairedPeer> _peers = new();
    private readonly HubServer _hub = new();
    private string? _pairingCode;
    private bool _sharing;
    private bool _mouseSharing = true;
    private bool _keyboardSharing = true;
    private string? _focusedPeerId;
    private readonly HashSet<ForwardedKey> _forwardedKeys = new();
    private readonly HashSet<ForwardedButton> _forwardedButtons = new();

    public MainWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _peers;
        RefreshHostAddress();
        TargetPicker.Items.Add("This PC");
        TargetPicker.SelectedIndex = 0;
        _hub.PeerConnected += OnPeerConnected;
        _hub.PeerDisconnected += OnPeerDisconnected;
        _hub.PeerMessage += OnPeerMessage;
        _pairingCode = WindowsSecretStore.Load();
        if (_pairingCode is not null)
        {
            _hub.SetPairingCode(_pairingCode);
            _hub.Start();
            PairCode.Text = $"Pairing key: {_pairingCode}";
            CopyCodeButton.IsEnabled = true;
            StatusText.Text = "Ready to connect · sharing paused";
        }
        WindowsInputAdapter.Current = new WindowsInputAdapter();
        WindowsInputAdapter.Current.EventCaptured += OnLocalInput;
        Closing += OnClosing;
    }

    private void RefreshHostAddress()
    {
        IPAddress? address;
        try { address = Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)); }
        catch (SocketException) { address = null; }
        HostAddress.Text = $"Host address: {address?.ToString() ?? "No local IPv4 address"}  ·  port 42857";
    }

    private void CreateCode_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Create a local pairing key? Anyone who has this key and your PC address can connect until you rotate it.", "Create pairing key", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        _pairingCode = SecureJsonConnection.NewPairingCode();
        WindowsSecretStore.Save(_pairingCode);
        PairCode.Text = $"Pairing key: {_pairingCode}";
        CopyCodeButton.IsEnabled = true;
        _hub.SetPairingCode(_pairingCode);
        _hub.Start();
        StatusText.Text = "Ready to pair · sharing paused";
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_pairingCode is not null) Clipboard.SetText(_pairingCode);
    }

    private void StartSharing_Click(object sender, RoutedEventArgs e)
    {
        if (_pairingCode is null)
        {
            MessageBox.Show("Create a pairing key first, then connect a device.", "Drift", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var nextSharing = !_sharing;
        if (_sharing && !nextSharing)
        {
            WindowsInputAdapter.Current?.ReleaseRemoteKeys();
            ReleaseForwardedKeys(_focusedPeerId ?? "windows");
            ReleaseForwardedButtons(_focusedPeerId ?? "windows");
            ChangeFocus(null);
        }
        _sharing = nextSharing;
        WindowsInputAdapter.Current?.SetSharing(_sharing, MouseSharing.IsChecked == true, KeyboardSharing.IsChecked == true);
        if (!_sharing)
        {
            _focusedPeerId = null;
            TargetPicker.SelectedIndex = 0;
        }
        WindowsInputAdapter.Current?.SetFocusedPeer(_sharing ? _focusedPeerId : null);
        _ = BroadcastSharingStateAsync();
        if (_sharing) _ = _hub.BroadcastAsync(new { kind = "focus", target = _focusedPeerId ?? "windows", x = _focusedPeerId is null ? 0.5 : 0.02, y = 0.5 });
        StartSharingButton.Content = _sharing ? "Pause sharing" : "Start sharing";
        StatusText.Text = _sharing ? "Sharing is on" : "Sharing paused";
    }

    private void SharingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || MouseSharing is null || KeyboardSharing is null) return;
        var previousMouse = _mouseSharing;
        var previousKeyboard = _keyboardSharing;
        _mouseSharing = MouseSharing.IsChecked == true;
        _keyboardSharing = KeyboardSharing.IsChecked == true;
        if (_sharing && previousKeyboard && !_keyboardSharing)
        {
            WindowsInputAdapter.Current?.ReleaseRemoteKeys();
            foreach (var target in _forwardedKeys.Select(key => key.Destination).Distinct().ToArray()) ReleaseForwardedKeys(target);
        }
        if (_sharing && previousMouse && !_mouseSharing)
            foreach (var target in _forwardedButtons.Select(button => button.Destination).Distinct().ToArray()) ReleaseForwardedButtons(target);
        WindowsInputAdapter.Current?.SetSharing(_sharing, MouseSharing.IsChecked == true, KeyboardSharing.IsChecked == true);
        if (_sharing && !_mouseSharing && !_keyboardSharing && _focusedPeerId is not null)
        {
            ChangeFocus(null);
        }
        if (_sharing) _ = BroadcastSharingStateAsync();
    }

    private void TargetPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var peer = TargetPicker.SelectedItem as PairedPeer;
        ChangeFocus(peer?.PeerId);
    }

    internal string? SelectedPeerId => (TargetPicker.SelectedItem as PairedPeer)?.PeerId;

    private Task BroadcastSharingStateAsync() => _hub.BroadcastAsync(new { kind = "sharing", enabled = _sharing, mouse = _mouseSharing, keyboard = _keyboardSharing });

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EmptyDevices.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DisconnectDeviceButton.IsEnabled = DeviceList.SelectedItem is PairedPeer;
    }

    private void DisconnectDevice_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is PairedPeer peer) _hub.RemovePeer(peer.PeerId);
    }

    private void OnPeerConnected(PairedPeer peer)
    {
        Dispatcher.Invoke(() =>
        {
            var previous = _peers.FirstOrDefault(p => p.PeerId == peer.PeerId);
            if (previous is not null) { _peers.Remove(previous); TargetPicker.Items.Remove(previous); }
            _peers.Add(peer);
            TargetPicker.Items.Add(peer);
            EmptyDevices.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Connected · {peer.DisplayName}";
            _ = _hub.SendToPeerAsync(peer.PeerId, new { kind = "focus", target = _focusedPeerId ?? "windows", x = 0.02, y = 0.5 });
            _ = _hub.SendToPeerAsync(peer.PeerId, new { kind = "sharing", enabled = _sharing, mouse = _mouseSharing, keyboard = _keyboardSharing });
        });
    }

    private void OnPeerDisconnected(string peerId)
    {
        Dispatcher.Invoke(() =>
        {
            ReleaseForwardedInputFrom(peerId);
            if (_focusedPeerId == peerId) ChangeFocus(null);
            var peer = _peers.FirstOrDefault(p => p.PeerId == peerId);
            if (peer is not null) { _peers.Remove(peer); TargetPicker.Items.Remove(peer); }
            EmptyDevices.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void OnLocalInput(object? sender, JsonElement message)
    {
        if (!_sharing) return;
        if (!message.TryGetProperty("kind", out var kind)) return;
        if (kind.GetString() == "edge")
        {
            ChangeFocus(null);
            return;
        }
        if (kind.GetString() == "focus")
        {
            var target = message.GetProperty("target").GetString();
            ChangeFocus(target == "windows" ? null : target, message.TryGetProperty("x", out var xValue) ? xValue.GetDouble() : 0.5);
            return;
        }
        if (_focusedPeerId is null) return;
        var destination = _focusedPeerId;
        if (kind.GetString() == "key")
        {
            destination = TrackForwardedKey("windows", destination, message);
            if (destination is null) return;
        }
        else if (kind.GetString() == "pointer" && message.TryGetProperty("action", out var action) && action.GetString() == "button")
        {
            destination = TrackForwardedButton("windows", destination, message);
            if (destination is null) return;
        }
        _ = _hub.SendToPeerAsync(destination, message);
    }

    private void OnPeerMessage(string peerId, JsonElement message)
    {
        var copy = message.Clone();
        Dispatcher.BeginInvoke(() => HandlePeerMessage(peerId, copy));
    }

    private void HandlePeerMessage(string peerId, JsonElement message)
    {
        var kind = message.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
        if (kind == "edge")
        {
            var edge = message.TryGetProperty("edge", out var edgeValue) ? edgeValue.GetString() ?? "" : "";
            if (_sharing && _focusedPeerId == peerId) MoveFocusAcrossPeers(peerId, edge);
            return;
        }
        if (!_sharing || kind is not ("pointer" or "key" or "text")) return;
        if ((kind == "pointer" && !_mouseSharing) || ((kind == "key" || kind == "text") && !_keyboardSharing)) return;
        var destination = _focusedPeerId ?? "windows";
        if (destination == peerId) return;
        if (kind == "key")
        {
            var routedDestination = TrackForwardedKey(peerId, destination, message);
            if (routedDestination is null) return;
            destination = routedDestination;
        }
        else if (kind == "pointer" && message.TryGetProperty("action", out var action) && action.GetString() == "button")
        {
            var routedDestination = TrackForwardedButton(peerId, destination, message);
            if (routedDestination is null) return;
            destination = routedDestination;
        }
        if (destination == "windows") WindowsInputAdapter.Current?.Inject(message);
        else if (destination != peerId) _ = _hub.SendToPeerAsync(destination, message);
    }

    private void MoveFocusAcrossPeers(string sourcePeerId, string edge)
    {
        var devices = new List<string> { "windows" };
        devices.AddRange(_peers.Select(peer => peer.PeerId));
        var current = devices.IndexOf(sourcePeerId);
        if (current < 0) return;
        var next = edge switch { "left" => current - 1, "right" => current + 1, _ => 0 };
        if (next < 0 || next >= devices.Count) next = 0;
        ChangeFocus(devices[next] == "windows" ? null : devices[next], next > current ? 0.02 : 0.98);
    }

    private void ChangeFocus(string? nextPeerId, double entryX = 0.5)
    {
        if (nextPeerId is not null && _peers.All(peer => peer.PeerId != nextPeerId)) nextPeerId = null;
        var oldTarget = _focusedPeerId ?? "windows";
        var newTarget = nextPeerId ?? "windows";
        if (oldTarget != newTarget)
        {
            if (_sharing) WindowsInputAdapter.Current?.ReleaseRemoteKeys();
            ReleaseForwardedKeys(oldTarget);
            ReleaseForwardedButtons(oldTarget);
        }
        _focusedPeerId = nextPeerId;
        var selected = _peers.FirstOrDefault(p => p.PeerId == nextPeerId);
        TargetPicker.SelectedItem = selected is null ? TargetPicker.Items[0] : selected;
        WindowsInputAdapter.Current?.SetFocusedPeer(_sharing ? nextPeerId : null);
        if (_sharing) _ = _hub.BroadcastAsync(new { kind = "focus", target = newTarget, x = entryX, y = 0.5 });
    }

    private string? TrackForwardedKey(string sourcePeerId, string destination, JsonElement message)
    {
        var platformCode = message.TryGetProperty("platformCode", out var platform) ? platform.GetString() : null;
        var keyCode = message.TryGetProperty("vk", out var vk) ? vk.GetInt32() :
            platformCode == "android" && message.TryGetProperty("keyCode", out var androidCode) ? AndroidToVirtualKey(androidCode.GetInt32()) : 0;
        if (keyCode == 0 || !message.TryGetProperty("down", out var down)) return null;
        var existing = _forwardedKeys.FirstOrDefault(key => key.Source == sourcePeerId && key.VirtualKey == keyCode);
        if (down.GetBoolean())
        {
            if (existing != default) return existing.Destination;
            _forwardedKeys.Add(new ForwardedKey(sourcePeerId, destination, keyCode));
            return destination;
        }
        if (existing == default) return null;
        _forwardedKeys.Remove(existing);
        return existing.Destination;
    }

    private void ReleaseForwardedKeys(string destination)
    {
        foreach (var key in _forwardedKeys.Where(key => key.Destination == destination).ToArray())
        {
            var release = JsonSerializer.SerializeToElement(new { kind = "key", vk = key.VirtualKey, down = false });
            if (destination == "windows") WindowsInputAdapter.Current?.Inject(release);
            else _ = _hub.SendToPeerAsync(destination, release);
            _forwardedKeys.Remove(key);
        }
    }

    private string? TrackForwardedButton(string sourcePeerId, string destination, JsonElement message)
    {
        if (!message.TryGetProperty("button", out var buttonValue) || !message.TryGetProperty("down", out var down)) return null;
        var button = buttonValue.GetString();
        if (button is null || button == "wheel") return destination;
        var existing = _forwardedButtons.FirstOrDefault(value => value.Source == sourcePeerId && value.Button == button);
        if (down.GetBoolean())
        {
            if (existing != default) return existing.Destination;
            _forwardedButtons.Add(new ForwardedButton(sourcePeerId, destination, button));
            return destination;
        }
        if (existing == default) return null;
        _forwardedButtons.Remove(existing);
        return existing.Destination;
    }

    private void ReleaseForwardedButtons(string destination)
    {
        foreach (var button in _forwardedButtons.Where(value => value.Destination == destination).ToArray())
        {
            var release = JsonSerializer.SerializeToElement(new { kind = "pointer", action = "button", button = button.Button, down = false });
            if (destination == "windows") WindowsInputAdapter.Current?.Inject(release);
            else _ = _hub.SendToPeerAsync(destination, release);
            _forwardedButtons.Remove(button);
        }
    }

    private void ReleaseForwardedInputFrom(string sourcePeerId)
    {
        foreach (var key in _forwardedKeys.Where(value => value.Source == sourcePeerId).ToArray())
        {
            var release = JsonSerializer.SerializeToElement(new { kind = "key", vk = key.VirtualKey, down = false });
            if (key.Destination == "windows") WindowsInputAdapter.Current?.Inject(release);
            else _ = _hub.SendToPeerAsync(key.Destination, release);
            _forwardedKeys.Remove(key);
        }
        foreach (var button in _forwardedButtons.Where(value => value.Source == sourcePeerId).ToArray())
        {
            var release = JsonSerializer.SerializeToElement(new { kind = "pointer", action = "button", button = button.Button, down = false });
            if (button.Destination == "windows") WindowsInputAdapter.Current?.Inject(release);
            else _ = _hub.SendToPeerAsync(button.Destination, release);
            _forwardedButtons.Remove(button);
        }
    }

    private static int AndroidToVirtualKey(int keyCode) => keyCode switch
    {
        >= 29 and <= 54 => 0x41 + keyCode - 29,
        >= 7 and <= 16 => keyCode == 7 ? 0x30 : 0x31 + keyCode - 8,
        66 => 0x0D, 61 => 0x09, 67 => 0x08, 111 or 4 => 0x1B, 21 => 0x25, 19 => 0x26, 22 => 0x27, 20 => 0x28,
        74 => 0xBA, 70 => 0xBB, 55 => 0xBC, 69 => 0xBD, 56 => 0xBE, 76 => 0xBF, 68 => 0xC0, 71 => 0xDB, 73 => 0xDC, 72 => 0xDD, 75 => 0xDE,
        59 or 60 => 0x10, 113 or 114 => 0x11, 57 or 58 => 0x12, 115 => 0x14, 117 => 0x5B, 118 => 0x5C, 62 => 0x20,
        122 => 0x24, 123 => 0x23, 92 => 0x21, 93 => 0x22, 112 => 0x2E, 120 => 0x2C, >= 131 and <= 142 => 0x70 + keyCode - 131, _ => 0
    };

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownInProgress) return;
        e.Cancel = true;
        _shutdownInProgress = true;
        var pendingSends = new List<Task>();
        foreach (var key in _forwardedKeys.ToArray())
        {
            var release = JsonSerializer.SerializeToElement(new { kind = "key", vk = key.VirtualKey, down = false });
            if (key.Destination == "windows") WindowsInputAdapter.Current?.Inject(release);
            else pendingSends.Add(_hub.SendToPeerAsync(key.Destination, release));
        }
        _forwardedKeys.Clear();
        foreach (var button in _forwardedButtons.ToArray())
        {
            var release = JsonSerializer.SerializeToElement(new { kind = "pointer", action = "button", button = button.Button, down = false });
            if (button.Destination == "windows") WindowsInputAdapter.Current?.Inject(release);
            else pendingSends.Add(_hub.SendToPeerAsync(button.Destination, release));
        }
        _forwardedButtons.Clear();
        WindowsInputAdapter.Current?.ClearForwardedKeys();
        _sharing = false;
        WindowsInputAdapter.Current?.SetSharing(false, false, false);
        WindowsInputAdapter.Current?.SetFocusedPeer(null);
        await Task.WhenAll(pendingSends);
        await _hub.DisposeAsync();
        Close();
    }

    private bool _shutdownInProgress;
}

internal readonly record struct ForwardedKey(string Source, string Destination, int VirtualKey);
internal readonly record struct ForwardedButton(string Source, string Destination, string Button);

public sealed record PairedPeer(string PeerId, string DisplayName, SecureJsonConnection Connection)
{
    public override string ToString() => DisplayName;
}

internal sealed class HubServer : IAsyncDisposable
{
    private const int Port = 42857;
    private readonly Dictionary<string, PairedPeer> _peers = new();
    private readonly object _gate = new();
    private TcpListener? _listener;
    private string? _pairingCode;
    private CancellationTokenSource _stop = new();

    public event Action<PairedPeer>? PeerConnected;
    public event Action<string>? PeerDisconnected;
    public event Action<string, JsonElement>? PeerMessage;

    public void Start()
    {
        if (_listener is not null) return;
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _ = AcceptLoopAsync(_stop.Token);
    }

    public void SetPairingCode(string code)
    {
        PairedPeer[] removed;
        lock (_gate)
        {
            removed = _pairingCode is not null && _pairingCode != code ? _peers.Values.ToArray() : Array.Empty<PairedPeer>();
            foreach (var peer in removed) _peers.Remove(peer.PeerId);
            _pairingCode = code;
        }
        foreach (var peer in removed) { peer.Connection.Close(); PeerDisconnected?.Invoke(peer.PeerId); }
    }

    public async Task SendToPeerAsync(string peerId, JsonElement message)
    {
        PairedPeer? peer;
        lock (_gate) _peers.TryGetValue(peerId, out peer);
        if (peer is not null)
        {
            try { await peer.Connection.SendAsync(message, _stop.Token); }
            catch { Remove(peerId); }
        }
    }

    public async Task BroadcastAsync<T>(T message)
    {
        PairedPeer[] peers;
        lock (_gate) peers = _peers.Values.ToArray();
        foreach (var peer in peers)
        {
            try { await peer.Connection.SendAsync(message, _stop.Token); }
            catch { Remove(peer.PeerId); }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                _ = HandlePeerAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { await Task.Delay(250, cancellationToken).ContinueWith(_ => { }); }
        }
    }

    private async Task HandlePeerAsync(TcpClient client, CancellationToken cancellationToken)
    {
        SecureJsonConnection? connection = null;
        string? peerId = null;
        var registered = false;
        try
        {
            string? code;
            lock (_gate) code = _pairingCode;
            if (code is null) { client.Dispose(); return; }
            var accepted = await SecureJsonConnection.AcceptAsync(client, code, cancellationToken);
            connection = accepted.Connection;
            peerId = accepted.PeerId;
            var peer = new PairedPeer(peerId, accepted.DisplayName, connection);
            lock (_gate)
            {
                if (_pairingCode != code) throw new InvalidOperationException("The pairing key changed during connection.");
                if (_peers.ContainsKey(peerId)) throw new InvalidOperationException("This device is already connected.");
                _peers[peerId] = peer;
                registered = true;
            }
            PeerConnected?.Invoke(peer);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var document = await connection.ReceiveAsync(cancellationToken);
                if (document is null) break;
                PeerMessage?.Invoke(peerId, document.RootElement.Clone());
            }
        }
        catch { }
        finally
        {
            if (registered && peerId is not null) Remove(peerId);
            if (connection is null) client.Dispose();
            else await connection.DisposeAsync();
        }
    }

    private void Remove(string peerId)
    {
        PairedPeer? peer;
        lock (_gate) { _peers.Remove(peerId, out peer); }
        if (peer is not null) { peer.Connection.Close(); PeerDisconnected?.Invoke(peerId); }
    }

    public void RemovePeer(string peerId) => Remove(peerId);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener?.Stop();
        PairedPeer[] peers;
        lock (_gate) peers = _peers.Values.ToArray();
        foreach (var peer in peers) await peer.Connection.DisposeAsync();
        _stop.Dispose();
    }
}
