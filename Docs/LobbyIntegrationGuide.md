# Lobby ↔ Dedicated Server ↔ Client Integration Guide (Mockup)

> **Level**: Conceptual / mockup. The goal is to show the flow, the responsibility boundaries, and the integration points — the actual crypto/transport library is the game's choice. The code here is **illustrative of the integration shape**, not a production implementation.
>
> **Reference**: the formal spec is [Specification.md §9.6 Player Identity Handoff](Specification.md).
>
> **Working reference implementations** (the mockups here map to real, tested code):
> - Lobby server — [`Samples/DevLobbyServer/`](../Samples/DevLobbyServer) (ticket issue + SD redeem)
> - P2P validator — [`Samples/IdentityP2pRef/`](../Samples/IdentityP2pRef) (offline signature)
> - SD validator — [`Samples/IdentitySdRef/`](../Samples/IdentitySdRef) (online redeem)
> - Identity keys — [Samples/DevIdentityKeys.md](Samples/DevIdentityKeys.md) (generate / rotate the signing key pair)
>
> **No-library principle**: This guide names no specific crypto/network library. `Sign()` / `Verify()` (signing) and `PostJson()` (lobby calls) are **seams the game fills in**. The only thing the core mandates is the **contract (interface)**.

---

## 1. What Gets Integrated

The Klotho core intentionally **does not contain a lobby / matchmaking / authentication** (3-layer separation, [Specification.md §Network Layer Separation](Specification.md)). All the core provides is the **carriage · validation hook · propagation** of the credential that crosses this boundary; the actual signing, redeem, and crypto live in the **Game Service Layer** (the lobby server + the hooks the game implements).

```
┌────────────────────────────────────────────────┐
│         Lobby / Auth Server (outside core)     │
│  · login auth → account confirmed              │
│  · matchmaking → session (server/room) assign  │
│  · issue ticket (sign)    ─────────────┐       │
│  · (SD) redeem verify API ◄────────┐   │       │
└─────────────────────────────────── │ ──│───────┘
            ▲ (1) login              │   │ (2) ticket + endpoint
            │                        │   ▼
       ┌────┴──────┐          ┌──────┴─────────────────┐
       │  Client   │ (3) join │   Klotho session       │
       │(has ticket)─────────►│   carries ticket in    │
       └───────────┘          │   handshake            │
                              └──────────┬─────────────┘
                                         │ (4) call validation hook
                    ┌────────────────────┴────────────────────┐
                    ▼ SD (dedicated)                          ▼ P2P
        dedicated server verifies online        each host verifies the lobby
        via lobby redeem → account              signature offline → account
                    │                                         │
                    └──────────────┬──────────────────────────┘
                                   ▼
              (5) propagate verified Account/DisplayName to every peer via roster
```

Responsibilities of the three parties:

| Party | Responsibility | Touchpoint with Klotho |
|---|---|---|
| **Lobby server** (game-built) | Login auth, match assignment, **ticket issue (sign)**, (SD) **redeem verify** | None — never talks to the core directly. Called by client/dedicated |
| **Client** (game-built) | Present the lobby-issued ticket at join | `IPlayerIdentityProvider.GetTicket()` |
| **Dedicated server / P2P host** (game-built) | Validate the join ticket → confirm & propagate authoritative account/name | `IPlayerIdentityValidator.BeginValidate(...)` |

---

## 2. End-to-End Sequence (SD / Dedicated)

```
Client               Lobby Server         Dedicated Server      (other clients)
   │                     │                    │                        │
   │── login(authToken) ►│                    │                        │
   │◄ account confirmed ─│                    │                        │
   │                     │                    │                        │
   │── requestMatch ────►│  matchmaking       │                        │
   │                     │  session/server    │                        │
   │◄ {ticket, endpoint, roomId, mode:"SD"} ─ signed ticket            │
   │                     │                    │                        │
   │════════════ Connect(endpoint) + carry ticket in handshake ═══════►│
   │                     │                    │ (4) validation hook:   │
   │                     │◄ redeemTicket ─────│   local 1st (sig/exp)  │
   │                     │  nonce consume/ban │   → lobby redeem       │
   │                     │─ ok{account,name} ►│                        │
   │                     │                    │ reserve slot + roster  │
   │◄══ join accepted + propagate authoritative account/DisplayName ══►│ (5)
   │                     │                    │                        │
   (on failure: send JoinReject(reason), then disconnect — no slot consumed)
```

P2P has no redeem round-trip at (4); the **host verifies the lobby signature offline** with the lobby public key (§5). The rest of the flow is identical.

---

## 3. Ticket Structure (Concept)

Ticket = `signed payload`. The lobby fills the payload and signs it with the lobby private key. **The core treats the ticket as opaque bytes** — it never parses it, so the game is free to add fields in its own encoding with zero core changes. Only the minimal set needed for validation and replay defense is part of the contract.

```
LobbyTicket (logical structure; serialization/encoding is the game's choice)
├─ account      : stable account id (invariant across sessions)
├─ displayName  : display name
├─ sessionId    : this match/session identifier (binds ticket ↔ session)
├─ issuedAt     : issue time
├─ expiresAt    : expiry time (replay / late-join defense)
├─ nonce        : one-time value (replay defense)
└─ signature    : signature over the fields above (lobby key)
```

- **Signing scheme**: asymmetric signing recommended — only the lobby *public* key is distributed to clients/servers, so there is no shared-secret leak risk (a symmetric key embedded in every client invites forgery). *The exact algorithm/library is the game's choice.*
- **Carriage encoding**: the handshake carries it as a single `string` (e.g. base64url). The inner serialization (JSON/binary/etc.) is the game's choice.
- **Verified extra fields** (MMR/level, etc.): add them as game fields *outside* the minimal set (the core is opaque, so zero changes). Mutable/unverified game data (character/team/cosmetics) goes through the separate `PlayerConfig` channel — keep it out of identity.

---

## 4-A. Lobby Server (Mockup)

An **external service** that never talks to the core directly. It only needs two endpoints. Transport (gRPC/REST/other) and serialization are the game's choice.

### issueTicket — on login / match success (lobby → client)

```
POST /lobby/issueTicket
  req:  { authToken, matchId }
  res:  { ticket, endpoint:"host:port", roomId, mode:"SD"|"P2P" }   // roomId: SD multi-room assignment (§4-D)
```

```csharp
// Mockup — the game fills in the real auth/matchmaking/signing
IssueTicketResponse IssueTicket(IssueTicketRequest req)
{
    var account = Authenticate(req.AuthToken);          // ← game's auth
    var match   = AssignMatch(account, req.MatchId);    // ← game's matchmaking

    var payload = new TicketPayload {
        Account     = account.Id,
        DisplayName = account.DisplayName,
        SessionId   = match.SessionId,        // the match/session this ticket binds to
        IssuedAt    = Now(),
        ExpiresAt   = Now() + TicketTtl,      // keep short — especially for P2P
        Nonce       = NewNonce(),             // one-time
    };
    var ticket = Encode(payload, Sign(payload, LobbyPrivateKey));   // ← signing seam

    return new IssueTicketResponse {
        Ticket   = ticket,                    // opaque string the client carries in the handshake
        Endpoint = match.ServerEndpoint,      // dedicated address (SD) / host address (P2P)
        RoomId   = match.RoomId,              // SD multi-room: the assigned room (client routes to it) — §4-D
        Mode     = match.Mode,                // "SD" | "P2P"
    };
}
```

### redeemTicket — dedicated server delegating join verification (dedicated → lobby, **SD only**)

```
POST /lobby/redeemTicket
  req:  { ticket, sessionId, serverId, roomId }   // roomId: the room the peer was routed to (§4-D)
  res:  { ok:true, account, displayName } | { ok:false, reason }
```

```csharp
// Mockup — only the SD dedicated server calls this. P2P never does (offline verify).
RedeemResponse RedeemTicket(RedeemRequest req)
{
    var payload = Decode(req.Ticket);
    if (!Verify(payload, payload.Signature, LobbyPublicKey))   // ← verify seam
        return RedeemResponse.Fail("bad-signature");
    if (payload.ExpiresAt <= Now())
        return RedeemResponse.Fail("expired");

    // Match binding: the ticket's own sessionId is authoritative. The lobby checks that this match is
    // assigned to the redeeming (serverId, roomId) — blocks ticket reuse across matches/servers/rooms (§4-D).
    if (!IsMatchAssignedTo(payload.SessionId, req.ServerId, req.RoomId))
        return RedeemResponse.Fail("session-mismatch");   // wire 8 — also covers wrong room

    // nonce consume — idempotency window: a repeat within the window returns the cached result
    // (recovers a player who passed validation but dropped before slot reservation). Beyond it: replay reject.
    if (!ConsumeNonceIdempotent(payload.Nonce, out var cached))
        return RedeemResponse.Fail("replay");
    if (IsBanned(payload.Account))                              // real-time ban applied here
        return RedeemResponse.Fail("banned");

    return RedeemResponse.Ok(payload.Account, payload.DisplayName);
}
```

> **Mockup note**: an **idempotency window** instead of strict single-use — a player who passed validation but dropped right before slot reservation must be recoverable on rejoin. Keep the window short, and pair it with "account active in this match" tracking.

---

## 4-B. Client (Mockup)

The client just **holds the ticket and presents it at join**. Carriage, propagation, and UI updates are handled by the core.

### (1) Ticket provider — `IPlayerIdentityProvider`

```csharp
// Game-implemented. The core calls GetTicket() once and puts it in the handshake (opaque).
public sealed class LobbyTicketProvider : IPlayerIdentityProvider
{
    private readonly string _ticket;     // the string kept from the issueTicket response
    public LobbyTicketProvider(string ticket) => _ticket = ticket;
    public string GetTicket() => _ticket;   // null/"" → behaves as "no lobby"
}
```

### (2) Register on the builder → join

```csharp
// after the lobby response: { ticket, endpoint, roomId, mode }
var setup = new KlothoFlowSetupBuilder()
    .WithHandshake(transport, endpoint)              // existing join flow
    .WithLobbyIdentity(new LobbyTicketProvider(ticket))   // ← register the ticket provider
    .Build();

// On Connect the core carries GetTicket()'s result as PlayerJoinMessage.Ticket
await session.Connect(...);
```

### (3) Receive authoritative identity / handle rejection

Once the join is accepted, every peer's authoritative `Account`/`DisplayName` is propagated via the roster and exposed through `IPlayerInfo`. The UI reads that (no local fabrication).

```csharp
foreach (var p in session.Players)
    ShowPlayerRow(p.PlayerId, p.DisplayName, p.Account);   // authoritative values

// On validation failure the core receives a JoinReject → disconnects. Branch on the reason code.
session.OnJoinFailed += reason => {
    // JoinFailReason enum (client-local). See the mapping table in §6.
    ShowError(reason);   // e.g. IdentityExpired → "Your ticket expired. Please re-queue."
};
```

> **No lobby (LAN/dev)**: skip `WithLobbyIdentity` and the ticket is carried empty and the validation hook is not invoked — **identical to current behavior**. If you just want a nickname shown, the client may present an unverified `ClaimedDisplayName` (ignored when a validator is present, so it is spoof-proof in production).

---

## 4-C. Dedicated Server / P2P Host (Mockup)

Where the authority validates the join ticket. Implement `IPlayerIdentityValidator` and register it on the builder. The core invokes the hook **before slot reservation** (a reject consumes no slot), so validation is a one-shot join event.

### Core contract

```csharp
public interface IPlayerIdentityValidator
{
    // Called on the network loop. Returns a pollable handle.
    IIdentityValidation BeginValidate(in IdentityValidationRequest request);
}

public interface IIdentityValidation : IDisposable
{
    bool IsComplete { get; }              // completion (always true for synchronous P2P)
    IdentityValidationOutcome Outcome { get; }   // result once complete
}
// Result: IdentityValidationOutcome.Accept(account, displayName) / Reject(wireCode)
```

`IdentityValidationRequest` carries all the context a validator needs: `Ticket`, `ClaimedDisplayName`, `SessionMagic` (session binding), `PeerId`, `DeviceId`, `IsLateJoin`, `IsHostSelf`, `RoomId` (the room the peer was routed to — SD multi-room; `-1` when not room-scoped, see §4-D).

### SD (dedicated) — online redeem (asynchronous)

A redeem is a network round-trip, so to **not block the loop** it returns an incomplete handle and completes it from a background thread (the core polls `IsComplete` each tick).

```csharp
public sealed class SdRedeemValidator : IPlayerIdentityValidator
{
    public IIdentityValidation BeginValidate(in IdentityValidationRequest req)
    {
        // Do not retain the `in` struct — copy by value (strings are immutable → safe on a bg thread)
        var ticket = req.Ticket;
        var sessionMagic = req.SessionMagic;
        var roomId = req.RoomId;   // routed room (SD multi-room) — carried into redeem for cross-check (§4-D)

        if (string.IsNullOrEmpty(ticket))
            return Completed(Reject(10));   // IdentityRequired

        var handle = new PendingValidation();
        RunBackground(async () => {
            // local 1st pass: signature/expiry (reject obvious forgeries early) — signing lib is a seam
            var payload = Decode(ticket);
            if (!Verify(payload, payload.Signature, LobbyPublicKey)) { handle.Complete(Reject(6)); return; }
            if (payload.ExpiresAt <= Now())                          { handle.Complete(Reject(7)); return; }

            // authority: lobby redeem (nonce consume / ban / match binding) — transport is a seam
            var res = await PostJson("/lobby/redeemTicket",
                new { ticket, sessionId = MapSession(sessionMagic), serverId = ServerId, roomId });

            handle.Complete(res.ok
                ? Accept(res.account, res.displayName)
                : Reject(MapReason(res.reason)));   // out-of-range codes clamp to 9
        });
        return handle;   // incomplete → the core polls it
    }
}
```

`PendingValidation` writes the outcome first, then sets `IsComplete` with a release write (the threading contract). `Dispose` cancels an in-flight redeem (peer dropped before the nonce was consumed) — idempotently.

### P2P (host) — offline signature verify (synchronous)

No network call. Returns an already-complete handle.

```csharp
public sealed class P2pSignatureValidator : IPlayerIdentityValidator
{
    private readonly HashSet<string> _seenNonces = new();   // session-scoped nonce

    public IIdentityValidation BeginValidate(in IdentityValidationRequest req)
    {
        if (string.IsNullOrEmpty(req.Ticket)) return Completed(Reject(10));

        // verify-over-wire: verify the bytes as transmitted (no re-serialization — avoids sig mismatch)
        var payload = Decode(req.Ticket);
        if (!Verify(payload, payload.Signature, LobbyPublicKey)) return Completed(Reject(6));  // signature/pubkey seam
        if (payload.ExpiresAt <= Now())                          return Completed(Reject(7));  // expired
        if (payload.SessionId != ExpectedSessionId(req))         return Completed(Reject(8));  // session mismatch
        if (!_seenNonces.Add(payload.Nonce))                     return Completed(Reject(9));  // in-session replay

        return Completed(Accept(payload.Account, payload.DisplayName));
    }
}
```

> **Semi-trust**: in P2P the host is the single verifier and guests trust the host's verdict (the original ticket is not propagated — only 2 strings are propagated, keeping it simple/lightweight). Guest re-validation (full zero-trust) can be added later, non-destructively, by attaching the original ticket to the notification.

### Builder registration

```csharp
// dedicated (SD)
new KlothoFlowSetupBuilder()
    .WithHandshake(transport, listenEndpoint)
    .WithIdentityValidator(new SdRedeemValidator())
    .Build();

// P2P host
new KlothoFlowSetupBuilder()
    .WithHandshake(transport, listenEndpoint)
    .WithIdentityValidator(new P2pSignatureValidator())
    .Build();
```

---

## 4-D. roomId Trust (SD Multi-Room)

On a multi-room dedicated server (one server hosting several independent matches), the client must say **which room** it is joining. That room id travels an **untrusted path**, so it is treated as a routing hint — the *authority* is the lobby's assignment ledger, not the client's claim.

**Flow**

```
(1) lobby assigns the match → picks a (serverId, roomId), records  sessionId → (serverId, roomId),
    and returns roomId in the issueTicket response
(2) client routes:  RoomHandshakeMessage{ roomId }   ← FIRST message, pre-join, NO access control
(3) the routed room reaches the validator as  IdentityValidationRequest.RoomId
(4) the SD validator carries it into the redeem:  redeemTicket{ ..., roomId }
(5) lobby cross-checks the routed roomId against the room bound to sessionId
        match    → accept
        mismatch → Reject(8), no slot consumed (the hook runs before slot reservation)
```

**Why roomId is not trusted on its own**: routing (`RoomHandshake`) happens **before** validation (`PlayerJoin` + ticket), and the router applies no access control — a client can route to any in-range room id (an unknown one is even lazily created). This is the same client-asserted problem as a spoofed display name. The defense has the same shape as cross-match defense, narrowed to room granularity: trust flows from the **signed** `sessionId` bound to a `(serverId, roomId)`.

**Tamper outcomes**

| Client behavior | Result |
|---|---|
| routes to the assigned roomId | normal join |
| routes to a different room (same server) with its own ticket | redeem (server, room) mismatch → **Reject(8)**, no slot consumed |
| routes to an in-range room the server hasn't created yet | room is lazily created, but the binding mismatch still rejects at redeem |
| routes to an out-of-range roomId | router rejects (RoomNotFound) before redeem |
| no lobby (validator off) | no defense — any room (LAN/dev intent) |

**Granularity**: the binding is **per match** — `sessionId` (= matchId) binds the whole match's players to one `(server, room)`, so the 2nd..Nth player of the same match share the room (one room serves one match). Single-room SD binds to room `0`; P2P is not multi-room and passes a `-1` sentinel the validator ignores (the `RoomId` field is append-only opt-in — a validator that does not read it is unaffected).

> **Mockup note**: the lobby's cross-check is authoritative on its own. A local fast-reject in the validator (rejecting before the redeem round-trip) would need the *expected* room in the ticket/redeem result — not carried today, so it is deferred. The redeem cross-check already closes the hole.

---

## 5. SD vs P2P at a Glance

| Aspect | SD (dedicated) | P2P (host) |
|---|---|---|
| Verifier | dedicated server | host (verifies) → guests trust the host (semi-trust) |
| Method | lobby redeem (online) + local 1st-pass signature | signature only (offline) |
| Trust anchor | server + lobby | lobby signature |
| Real-time ban | yes (redeem) | no — relies on expiry (keep `expiresAt` short) |
| Replay defense | nonce idempotency window + (server, room) binding | `expiresAt` + session-scoped nonce |
| account source | redeem response | ticket payload (verified) |
| Klotho hook | async handle (polled) | sync handle (immediately complete) |

---

## 6. Reject Reason Codes

On failure the authority returns `Reject(wireCode)` → the core sends `JoinReject` then disconnects. The **wire byte** and the client-local `JoinFailReason` enum are **distinct numbering schemes**, mapped by `JoinFailReason.FromJoinReject`. An out-of-range code (buggy redeem) is clamped to `9` (IdentityRejected).

| Meaning | Wire byte | `JoinFailReason` enum |
|---|---|---|
| (room reasons: RoomFull, etc.) | 1~5 | 7~11 |
| IdentityInvalid (bad signature / format / empty-or-oversize account) | 6 | 12 |
| IdentityExpired | 7 | 13 |
| IdentitySessionMismatch (cross-match / wrong room) | 8 | 14 |
| IdentityRejected (redeem deny / ban / consumed nonce) | 9 | 15 |
| IdentityRequired (validator present, no ticket) | 10 | 16 |
| IdentityValidationFailed (transport fault / redeem timeout) | 11 | 17 |

---

## 7. No Lobby (opt-in OFF) — No Regression

With neither provider nor validator registered:

| Aspect | Behavior |
|---|---|
| `PlayerJoinMessage.Ticket` | empty string (not carried) |
| Validation hook | not invoked |
| `Account` | `""` (never interchanged with DeviceId — reconnect fingerprint ≠ account) |
| `DisplayName` | client-claimed value (if any, unverified) → otherwise a fabricated fallback (`"Host"`/`Player{id}`) |
| Reject | does not occur |
| Overall | **100% identical to current code** |

LAN / prototype / samples keep working as-is; only production turns on the provider (client) + validator (server/host).

---

## 8. Integration Checklist

**Lobby server team**
- [ ] `issueTicket` — after login auth + match assignment, issue a signed ticket + endpoint + mode
- [ ] `redeemTicket` (SD) — verify signature/expiry/match-binding + idempotent nonce consume + ban check
- [ ] SD multi-room: return the assigned `roomId` from `issueTicket`; cross-check the redeem `(serverId, roomId)` against the binding → wrong room = Reject(8) (§4-D)
- [ ] If asymmetric signing, **distribute the public key to clients/dedicated servers**

**Client team**
- [ ] Implement `IPlayerIdentityProvider` (return the stored ticket)
- [ ] Register `WithLobbyIdentity(provider)` then Connect to the endpoint
- [ ] UI shows `IPlayerInfo.DisplayName`/`Account` (authoritative); remove local fabrication
- [ ] Branch on `OnJoinFailed` reason codes (expired → re-queue, etc.)

**Dedicated / host team**
- [ ] Implement `IPlayerIdentityValidator` — SD = async redeem, P2P = sync signature
- [ ] Register `WithIdentityValidator(validator)`
- [ ] SD: incomplete handle + background completion + `Dispose` cancel (idempotent)
- [ ] Reject with `Reject(wireCode 6~11)`, accept with `Accept(account, displayName)`
- [ ] SD multi-room: carry `IdentityValidationRequest.RoomId` into the redeem so the lobby can cross-check the routed room (§4-D)

**Common**
- [ ] Keep identity (verified, invariant) and game data (PlayerConfig, unverified, mutable) **separate**
- [ ] Authoritative fields that affect the simulation (team/loadout) must not be injected out-of-band — use a deterministic path (GameStart seed / ReliableCommand)
