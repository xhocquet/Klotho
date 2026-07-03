using System;
using System.Globalization;
using System.Text;

namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>
    /// Ticket wire codec. Wire = <c>base64url(payloadJson) "." base64url(sig)</c>.
    /// <para>
    /// verify-over-wire: the validator verifies the signature over the EXACT decoded payload bytes
    /// and only THEN parses — it never re-serializes (the classic Ed25519 footgun). JSON so a
    /// language-agnostic lobby can emit it; <see cref="ParsePayload"/> ignores unknown keys for
    /// forward-compatibility. Minimal hand-rolled JSON avoids a Unity dependency (netstandard2.1 ships no
    /// System.Text.Json; JsonUtility is too limited) — BC carries no JSON.
    /// </para>
    /// </summary>
    public static class LobbyTicketCodec
    {
        // ── wire split: exactly two non-empty segments separated by a single '.' ──
        public static bool TrySplitWire(string wire, out string payloadSeg, out string sigSeg)
        {
            payloadSeg = null;
            sigSeg = null;
            if (string.IsNullOrEmpty(wire)) return false;
            int dot = wire.IndexOf('.');
            if (dot <= 0) return false;                       // no '.' or empty payload segment
            if (dot != wire.LastIndexOf('.')) return false;   // more than one '.'
            if (dot == wire.Length - 1) return false;         // empty signature segment
            payloadSeg = wire.Substring(0, dot);
            sigSeg = wire.Substring(dot + 1);
            return true;
        }

        // ── base64url (no padding) ──
        public static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] Base64UrlDecode(string s)
        {
            string t = s.Replace('-', '+').Replace('_', '/');
            switch (t.Length % 4)
            {
                case 2: t += "=="; break;
                case 3: t += "="; break;
                case 1: throw new FormatException("invalid base64url length");
            }
            return Convert.FromBase64String(t); // throws FormatException on non-base64 chars
        }

        // ── encode (issuer side; canonical fixed-order JSON) ──
        public static byte[] EncodePayload(in LobbyTicket t)
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            AppendString(sb, "account", t.Account); sb.Append(',');
            AppendString(sb, "displayName", t.DisplayName); sb.Append(',');
            AppendString(sb, "sessionId", t.SessionId); sb.Append(',');
            AppendNumber(sb, "issuedAt", t.IssuedAt); sb.Append(',');
            AppendNumber(sb, "expiresAt", t.ExpiresAt); sb.Append(',');
            AppendString(sb, "nonce", t.Nonce); sb.Append(',');
            // The entitlement is appended last. This canonical field order is the signature input, so the
            // issuer and validator must agree on it. JSON carries no binary, so the bytes are encoded as a
            // base64url string; a null or empty entitlement is written as "".
            AppendString(sb, "entitlement",
                (t.Entitlement != null && t.Entitlement.Length > 0) ? Base64UrlEncode(t.Entitlement) : string.Empty);
            sb.Append('}');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static void AppendString(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":");
            AppendJsonString(sb, value);
        }

        private static void AppendNumber(StringBuilder sb, string key, long value)
        {
            sb.Append('"').Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ── parse (validator side; tolerant of order/whitespace/unknown keys; throws FormatException on malformed) ──
        public static LobbyTicket ParsePayload(byte[] payloadBytes)
        {
            string json = Encoding.UTF8.GetString(payloadBytes);
            var r = new JsonReader(json);
            string account = string.Empty, displayName = string.Empty, sessionId = string.Empty, nonce = string.Empty;
            long issuedAt = 0, expiresAt = 0;
            byte[] entitlement = null; // an absent key parses to null, so older tickets remain compatible

            r.SkipWs();
            r.Expect('{');
            r.SkipWs();
            if (r.Peek() == '}')
            {
                r.Next();
            }
            else
            {
                while (true)
                {
                    r.SkipWs();
                    string key = r.ReadString();
                    r.SkipWs();
                    r.Expect(':');
                    r.SkipWs();
                    switch (key)
                    {
                        case "account":     account = r.ReadString(); break;
                        case "displayName": displayName = r.ReadString(); break;
                        case "sessionId":   sessionId = r.ReadString(); break;
                        case "nonce":       nonce = r.ReadString(); break;
                        case "issuedAt":    issuedAt = r.ReadLong(); break;
                        case "expiresAt":   expiresAt = r.ReadLong(); break;
                        case "entitlement":
                        {
                            // Decode the base64url string back to bytes; an empty string maps to null (an
                            // identity-only ticket). Malformed base64 throws FormatException, so the caller
                            // rejects a signed-but-malformed ticket.
                            string b64 = r.ReadString();
                            entitlement = string.IsNullOrEmpty(b64) ? null : Base64UrlDecode(b64);
                            break;
                        }
                        default:            r.SkipValue(); break; // forward-compat: ignore unknown keys
                    }
                    r.SkipWs();
                    char c = r.Next();
                    if (c == ',') continue;
                    if (c == '}') break;
                    throw new FormatException("expected ',' or '}'");
                }
            }
            return new LobbyTicket(account, displayName, sessionId, issuedAt, expiresAt, nonce, entitlement);
        }

        // Minimal JSON cursor — supports string/number/object/array/bool/null; only the 7 known keys are
        // materialised, unknown values are skipped. Throws FormatException on any malformed input.
        private sealed class JsonReader
        {
            private readonly string _s;
            private int _i;

            public JsonReader(string s) { _s = s; _i = 0; }

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                    else break;
                }
            }

            public char Peek()
            {
                if (_i >= _s.Length) throw new FormatException("unexpected end of input");
                return _s[_i];
            }

            public char Next()
            {
                if (_i >= _s.Length) throw new FormatException("unexpected end of input");
                return _s[_i++];
            }

            public void Expect(char c)
            {
                if (Next() != c) throw new FormatException("expected '" + c + "'");
            }

            public string ReadString()
            {
                if (Next() != '"') throw new FormatException("expected string");
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new FormatException("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') break;
                    if (c == '\\')
                    {
                        if (_i >= _s.Length) throw new FormatException("bad escape");
                        char e = _s[_i++];
                        switch (e)
                        {
                            case '"':  sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/':  sb.Append('/'); break;
                            case 'b':  sb.Append('\b'); break;
                            case 'f':  sb.Append('\f'); break;
                            case 'n':  sb.Append('\n'); break;
                            case 'r':  sb.Append('\r'); break;
                            case 't':  sb.Append('\t'); break;
                            case 'u':
                                if (_i + 4 > _s.Length) throw new FormatException("bad \\u escape");
                                sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                _i += 4;
                                break;
                            default: throw new FormatException("bad escape");
                        }
                    }
                    else sb.Append(c);
                }
                return sb.ToString();
            }

            public long ReadLong()
            {
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                if (_i == start || (_i == start + 1 && (_s[start] == '-' || _s[start] == '+')))
                    throw new FormatException("expected integer");
                return long.Parse(_s.Substring(start, _i - start), CultureInfo.InvariantCulture);
            }

            public void SkipValue()
            {
                SkipWs();
                char c = Peek();
                switch (c)
                {
                    case '"': ReadString(); return;
                    case '{': SkipObject(); return;
                    case '[': SkipArray(); return;
                    case 't': Expect('t'); Expect('r'); Expect('u'); Expect('e'); return;
                    case 'f': Expect('f'); Expect('a'); Expect('l'); Expect('s'); Expect('e'); return;
                    case 'n': Expect('n'); Expect('u'); Expect('l'); Expect('l'); return;
                    default:
                        if (c == '-' || c == '+' || (c >= '0' && c <= '9'))
                        {
                            _i++;
                            while (_i < _s.Length)
                            {
                                char d = _s[_i];
                                if ((d >= '0' && d <= '9') || d == '.' || d == 'e' || d == 'E' || d == '+' || d == '-') _i++;
                                else break;
                            }
                            return;
                        }
                        throw new FormatException("unexpected value");
                }
            }

            private void SkipObject()
            {
                Expect('{');
                SkipWs();
                if (Peek() == '}') { Next(); return; }
                while (true)
                {
                    SkipWs();
                    ReadString();
                    SkipWs();
                    Expect(':');
                    SkipValue();
                    SkipWs();
                    char c = Next();
                    if (c == ',') continue;
                    if (c == '}') return;
                    throw new FormatException("expected ',' or '}' in object");
                }
            }

            private void SkipArray()
            {
                Expect('[');
                SkipWs();
                if (Peek() == ']') { Next(); return; }
                while (true)
                {
                    SkipValue();
                    SkipWs();
                    char c = Next();
                    if (c == ',') continue;
                    if (c == ']') return;
                    throw new FormatException("expected ',' or ']' in array");
                }
            }
        }
    }
}
