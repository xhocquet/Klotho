// Session-control menu (Godot counterpart of the Unity SdMenu): Join/Ready/Stop buttons + IP/Port.
// No Host button — the dedicated server is a separate process (Server/).
using System;
using global::Godot;

namespace xpTURN.Samples.SdSample
{
	public partial class GodotSdMenu : Control
	{
		private Button _joinButton, _readyButton, _stopButton;
		private LineEdit _ipField, _portField, _matchField;

		public event Action OnJoinClicked;
		public event Action OnReadyClicked;
		public event Action OnStopClicked;

		public override void _Ready()
		{
			_joinButton  = GetNode<Button>("VBox/JoinButton");
			_readyButton = GetNode<Button>("VBox/ReadyButton");
			_stopButton  = GetNode<Button>("VBox/StopButton");
			_ipField     = GetNode<LineEdit>("VBox/IpField");
			_portField   = GetNode<LineEdit>("VBox/PortField");
			_matchField  = GetNodeOrNull<LineEdit>("VBox/MatchField");

			_joinButton.Pressed  += () => OnJoinClicked?.Invoke();
			_readyButton.Pressed += () => OnReadyClicked?.Invoke();
			_stopButton.Pressed  += () => OnStopClicked?.Invoke();
		}

		public string Host => string.IsNullOrEmpty(_ipField?.Text) ? "127.0.0.1" : _ipField.Text;
		public int Port => int.TryParse(_portField?.Text, out var p) ? p : 7777;
		/// <summary>Match id typed by the user; empty when the field is blank (caller falls back to a default).</summary>
		public string MatchId => _matchField?.Text ?? string.Empty;

		public void SetInitialHost(string host, int port)
		{
			if (_ipField != null)   _ipField.Text = host;
			if (_portField != null) _portField.Text = port.ToString();
		}

		public void SetReadyEnabled(bool enabled) { if (_readyButton != null) _readyButton.Disabled = !enabled; }
		public void SetStopEnabled(bool enabled)  { if (_stopButton  != null) _stopButton.Disabled  = !enabled; }
	}
}
