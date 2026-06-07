using Newtonsoft.Json;
using UnityEngine;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Unity
{
    /// <summary>
    /// PlayerPrefs-backed store for cold-start Reconnect credentials.
    /// Single-key serialization via Newtonsoft.Json.
    /// </summary>
    public class PlayerPrefsReconnectCredentialsStore : IReconnectCredentialsStore
    {
        private const string KEY = "Klotho.ReconnectCredentials";

        public void Save(PersistedReconnectCredentials creds)
        {
            if (creds == null)
            {
                Clear();
                return;
            }
            string json = JsonConvert.SerializeObject(creds);
            PlayerPrefs.SetString(KEY, json);
            PlayerPrefs.Save();
        }

        public PersistedReconnectCredentials Load()
        {
            if (!PlayerPrefs.HasKey(KEY))
                return null;
            string json = PlayerPrefs.GetString(KEY);
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                return JsonConvert.DeserializeObject<PersistedReconnectCredentials>(json);
            }
            catch
            {
                Clear();
                return null;
            }
        }

        public void Clear()
        {
            if (PlayerPrefs.HasKey(KEY))
            {
                PlayerPrefs.DeleteKey(KEY);
                PlayerPrefs.Save();
            }
        }

        public bool IsValid(PersistedReconnectCredentials creds, long nowUnixMs, string currentAppVersion)
        {
            if (creds == null)
                return false;
            if (creds.AppVersion != currentAppVersion)
                return false;
            if (nowUnixMs - creds.SavedAtUnixMs > creds.ReconnectTimeoutMs)
                return false;
            return true;
        }
    }
}
