// Session-control menu (Godot counterpart of the Unity P2pMenu): Host/Join/Ready/Stop buttons + IP/Port.
using System;
using global::Godot;

namespace xpTURN.Samples.P2pSample
{
    public partial class GodotP2pMenu : Control
    {
        private Button _hostButton, _joinButton, _readyButton, _stopButton;
        private LineEdit _ipField, _portField;

        public event Action OnHostClicked;
        public event Action OnJoinClicked;
        public event Action OnReadyClicked;
        public event Action OnStopClicked;

        public override void _Ready()
        {
            _hostButton  = GetNode<Button>("VBox/HostButton");
            _joinButton  = GetNode<Button>("VBox/JoinButton");
            _readyButton = GetNode<Button>("VBox/ReadyButton");
            _stopButton  = GetNode<Button>("VBox/StopButton");
            _ipField     = GetNode<LineEdit>("VBox/IpField");
            _portField   = GetNode<LineEdit>("VBox/PortField");

            _hostButton.Pressed  += () => OnHostClicked?.Invoke();
            _joinButton.Pressed  += () => OnJoinClicked?.Invoke();
            _readyButton.Pressed += () => OnReadyClicked?.Invoke();
            _stopButton.Pressed  += () => OnStopClicked?.Invoke();
        }

        public string Host => string.IsNullOrEmpty(_ipField?.Text) ? "127.0.0.1" : _ipField.Text;
        public int Port => int.TryParse(_portField?.Text, out var p) ? p : 7777;

        public void SetInitialHost(string host, int port)
        {
            if (_ipField != null)   _ipField.Text = host;
            if (_portField != null) _portField.Text = port.ToString();
        }

        public void SetReadyEnabled(bool enabled) { if (_readyButton != null) _readyButton.Disabled = !enabled; }
        public void SetStopEnabled(bool enabled)  { if (_stopButton  != null) _stopButton.Disabled  = !enabled; }
    }
}
