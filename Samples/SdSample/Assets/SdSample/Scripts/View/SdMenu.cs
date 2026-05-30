using System;
using UnityEngine;
using UnityEngine.UI;

namespace xpTURN.Samples.SdSample
{
    public class SdMenu : MonoBehaviour
    {
        [SerializeField] private Button _joinButton;
        [SerializeField] private Button _readyButton;
        [SerializeField] private Button _stopButton;
        [SerializeField] private InputField _ipField;
        [SerializeField] private InputField _portField;

        public event Action OnJoinClicked;
        public event Action OnReadyClicked;
        public event Action OnStopClicked;

        public string Host => _ipField != null ? _ipField.text : "localhost";
        public int Port
        {
            get
            {
                if (_portField == null) return 7777;
                return int.TryParse(_portField.text, out var p) ? p : 7777;
            }
        }

        private void Awake()
        {
            if (_joinButton != null) _joinButton.onClick.AddListener(() => OnJoinClicked?.Invoke());
            if (_readyButton != null) _readyButton.onClick.AddListener(() => OnReadyClicked?.Invoke());
            if (_stopButton != null) _stopButton.onClick.AddListener(() => OnStopClicked?.Invoke());
        }

        public void SetInitialHost(string host, int port)
        {
            if (_ipField != null) _ipField.text = host;
            if (_portField != null) _portField.text = port.ToString();
        }

        public void SetReadyEnabled(bool enabled)
        {
            if (_readyButton != null) _readyButton.interactable = enabled;
        }

        public void SetStopEnabled(bool enabled)
        {
            if (_stopButton != null) _stopButton.interactable = enabled;
        }
    }
}
