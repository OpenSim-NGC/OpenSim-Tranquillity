/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Live evidence that the sim's session client reaches a real Janus mixer over https, on the gateway API and
 * the admin API, through JanusTls. JanusTlsTests proves the certificate rules
 * against an in-process server; this proves the same handler against the mixer image with TLS switched on.
 *
 * EXPLICIT and opt-in: it never runs in a normal test run and does nothing unless LVM_TLS_VOICE_URL is set.
 * It only sends a create-session request and an admin info request, and destroys the session; point it at a scratch
 * mixer, never at a grid's.
 *
 *   LVM_TLS_VOICE_URL     the gateway API over https, e.g. https://127.0.0.1:34224/voice
 *   LVM_TLS_ADMIN_URL     the Admin API over https, e.g. https://127.0.0.1:34226/voiceAdmin
 *   LVM_TLS_CA_FILE       the PEM file of the CA (or the self-signed certificate) the mixer presents
 *   LVM_TLS_API_SECRET    the mixer's API secret (a throwaway value)
 *   LVM_TLS_ADMIN_SECRET  its admin secret (a throwaway value)
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    [TestFixture, Explicit("live scratch mixer; see the file header")]
    [NonParallelizable]
    public class JanusTlsLiveMixerTests
    {
        private static string Env(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
                Assert.Ignore(name + " is not set");
            return value;
        }

        private static async Task<(bool Created, string AdminBody)> Reach(string voiceUrl, string adminUrl, string caFile, string api, string admin)
        {
            using var session = new JanusSession(voiceUrl, api, adminUrl, admin, false, JanusTls.CreateHandler(caFile));
            bool created = await session.CreateSession();
            var reply = await session.SendToJanusAdmin(new JanusMessageReq("info"));
            if (created)
                await session.DestroySession();
            return (created, reply?.ReturnCode);
        }

        [Test]
        public async Task TheApiAndTheAdminApiAreReachedOverHttpsWhenTheKeyNamesTheCertificate()
        {
            string voice = Env("LVM_TLS_VOICE_URL"), admin = Env("LVM_TLS_ADMIN_URL"), ca = Env("LVM_TLS_CA_FILE");
            Assert.That(voice, Does.StartWith("https://"));
            Assert.That(admin, Does.StartWith("https://"));

            var (created, body) = await Reach(voice, admin, ca, Env("LVM_TLS_API_SECRET"), Env("LVM_TLS_ADMIN_SECRET"));

            Assert.That(created, Is.True, "a Janus session was created over https");
            Assert.That(body, Is.Not.Null, "the admin API answered over https");
        }

        [Test]
        public async Task TheMixersCertificateIsRefusedWhenTheKeyIsUnset()
        {
            string voice = Env("LVM_TLS_VOICE_URL"), admin = Env("LVM_TLS_ADMIN_URL");

            var (created, body) = await Reach(voice, admin, null, Env("LVM_TLS_API_SECRET"), Env("LVM_TLS_ADMIN_SECRET"));

            Assert.That(created, Is.False, "an unknown certificate is not accepted by the session client");
            Assert.That(body, Is.Null, "nor on the admin API: nothing was received");
        }
    }
}
