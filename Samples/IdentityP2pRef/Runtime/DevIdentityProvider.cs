using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>
    /// Client-side ticket carrier. Represents "the client that received a
    /// ticket from the lobby": it only RETURNS a ticket it was handed. It does NOT sign and holds NO
    /// private key — signing belongs to the lobby (see <see cref="DevLobbyTicketIssuer"/>). This
    /// separation keeps the reference from modelling the "client signs its own ticket" anti-pattern.
    /// </summary>
    public sealed class DevIdentityProvider : IPlayerIdentityProvider
    {
        private readonly string _ticket;

        public DevIdentityProvider(string ticket)
        {
            _ticket = ticket ?? string.Empty;
        }

        public string GetTicket() => _ticket;
    }
}
