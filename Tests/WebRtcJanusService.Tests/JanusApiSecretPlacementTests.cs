/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Where the Janus API secret travels, sim side, pinned by test.
 *
 * Every request the sim POSTs to Janus carries the secret in the JSON body: JanusSession.AddJanusHeaders
 * calls JanusMessageReq.AddAPIToken, which sets m_message["apisecret"] (JanusMessages.cs:92-95), and the core
 * reads it from the parsed body. The ONE place it goes into a URL is the Janus session long poll,
 * JanusSession.GetFromJanus (`pURI += "?apisecret=" + _JanusAPIToken`), and that is not a choice: Janus's HTTP
 * transport reads a GET's secret only from query arguments,
 *   MHD_lookup_connection_value(connection, MHD_GET_ARGUMENT_KIND, "apisecret")
 * (janus-gateway src/transports/janus_http.c:1601; `token` auth is read the same way on the next line).
 *
 * These tests are a ratchet: the first fails if a POST stops carrying it in the body, the second fails if a
 * second URL site appears anywhere in the Janus addon - which is how the secret's exposure in URLs would quietly widen.
 */

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JanusApiSecretPlacementTests
    {
        [Test]
        public void APostCarriesTheSecretInTheBody()
        {
            var req = new JanusMessageReq("keepalive");
            req.AddAPIToken("sekret");

            var body = OSDParser.DeserializeJson(req.ToJson()) as OSDMap;
            Assert.That(body, Is.Not.Null, "the request serialises to a JSON object");
            Assert.That(body.ContainsKey("apisecret"), Is.True, "the API secret must be in the POST body");
            Assert.That(body["apisecret"].AsString(), Is.EqualTo("sekret"));
        }

        [Test]
        public void TheSessionLongPollIsTheOnlyPlaceTheSecretGoesIntoAUrl()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Addons")))
                dir = dir.Parent;
            Assert.That(dir, Is.Not.Null, "could not find the repository root from " + AppContext.BaseDirectory);

            string janus = Path.Combine(dir.FullName, "Addons", "os-webrtc-janus");
            var sites = Directory.EnumerateFiles(janus, "*.cs", SearchOption.AllDirectories)
                                 .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                          && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                                 .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\?apisecret=")
                                                       .Select(_ => Path.GetFileName(f)))
                                 .ToList();

            Assert.That(sites, Is.EqualTo(new[] { "JanusSession.cs" }),
                "the query-string API secret must stay confined to the session long poll in "
                + "JanusSession.GetFromJanus, which Janus gives no alternative to. Found: "
                + (sites.Count == 0 ? "(none - did GetFromJanus change?)" : string.Join(", ", sites)));
        }
    }
}
