using xpTURN.Klotho.Network; // IPlayerIdentityProvider

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Client-side ticket carrier for the dedicated-server flow. Unlike the P2P <c>DevIdentityProvider</c> (immutable,
    /// locally-minted at construction), the SD client must FETCH its ticket from the lobby's online Issue
    /// endpoint — an async round-trip — but <see cref="IPlayerIdentityProvider.GetTicket"/> is synchronous
    /// and the session flow builds the provider once at init. So this provider is MUTABLE: the client
    /// awaits <c>IssueAsync</c> on Join, calls <see cref="SetTicket"/>, then connects; <c>GetTicket()</c>
    /// returns the pre-fetched ticket synchronously during the handshake.
    /// <para>It holds NO private key and never signs — it only carries a ticket the lobby issued (the
    /// "client signs its own ticket" anti-pattern stays impossible). Empty until a ticket is set → an empty
    /// ticket reaches the server, which the validator rejects with IdentityRequired(10).</para>
    /// </summary>
    public sealed class SdLobbyIssueProvider : IPlayerIdentityProvider
    {
        // Reference assignment of a string is atomic; the provider is set on the Join path before the
        // synchronous GetTicket() runs on the connect path (no torn read).
        private volatile string _ticket = string.Empty;

        public SdLobbyIssueProvider() { }

        public SdLobbyIssueProvider(string ticket) { _ticket = ticket ?? string.Empty; }

        /// <summary>Stores the lobby-issued ticket fetched on Join, before connecting (issue-then-connect).</summary>
        public void SetTicket(string ticket) => _ticket = ticket ?? string.Empty;

        public string GetTicket() => _ticket;
    }
}
