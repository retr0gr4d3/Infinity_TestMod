using System;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Infinity_TestMod.Util
{
    /// <summary>
    /// Inject server→client packets locally. Bypasses the network entirely:
    /// construct the same Response object the deserializer would build, then
    /// invoke its Execute() — matching what AEC.Update() does at AEC.cs:107.
    ///
    /// Useful for:
    ///   - Bypassing server permission checks (e.g. faking a SERVER chat
    ///     message — server side returns "you do not have permission" if
    ///     you try to send one via the wire)
    ///   - Probing client rendering without round-tripping a real packet
    ///   - Synthesizing test fixtures for QA scenarios
    ///
    /// Limits: only affects the local client. Other players see nothing.
    /// If you spoof a packet that mutates client state (e.g. updateQuestBits),
    /// your local view will desync from the server until the next real
    /// authoritative packet overwrites it.
    /// </summary>
    public static class Spoof
    {
        /// <summary>
        /// Deserialize <paramref name="json"/> into the Response class
        /// registered for its Cmd field, then dispatch via Execute().
        /// </summary>
        public static (bool ok, string info) Send(string json)
        {
            string cmd;
            try
            {
                var probe = JObject.Parse(json);
                cmd = (string)probe["Cmd"];
                if (string.IsNullOrEmpty(cmd))
                    return (false, "no Cmd field in JSON");
            }
            catch (Exception ex)
            {
                return (false, $"invalid JSON: {ex.Message}");
            }

            Type t = ResponseTypes.Get(cmd);
            if (t == null)
                return (false, $"no Response class registered for Cmd='{cmd}'");

            try
            {
                var resp = (Response)JsonConvert.DeserializeObject(json, t);
                if (resp == null)
                    return (false, "deserialized to null");
                resp.Execute();
                PacketLog.Write("s2c", json, synthetic: true);
                return (true, t.Name);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Spoof] dispatch failed: {ex}");
                return (false, $"Execute threw: {ex.Message}");
            }
        }

        /// <summary>Yellow center banner notification.</summary>
        public static (bool, string) Notify(string msg)
            => Send($"{{\"Cmd\":\"rNotify\",\"msg\":{JsonConvert.SerializeObject(msg)}}}");

        /// <summary>Chat message in any channel, attributed to any name.</summary>
        public static (bool, string) ChatM(string msg, string name, string channel)
            => Send(JsonConvert.SerializeObject(new
            {
                Cmd = "chatm",
                msg,
                Name = name,
                channel
            }));
    }
}
