/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using OpenMetaverse;
using MimeKit;
using System.Collections.Generic;

namespace OpenSim.Region.Framework.Interfaces
{
    //public class Email
    //{
    //    public string time;
    //    public string sender;
    //    public string subject;
    //    public string message;
    //    public int numLeft;
    //}

    public interface ISMTPModule
    {
        /// <summary>
        /// Fetch the email address on record for the Agent specified by the provided agentID.
        /// </summary>
        /// <param name="agentID"></param>
        string FormatAgentAddress(UUID agentID);

        /// <summary>
        /// Format an email address for the specified object id.
        /// </summary>
        /// <param name="objectID"></param>
        string FormatObjectAddress(UUID objectID);

        /// <summary>
        /// Format a MimeMessage using system SMTP configuration.  Validate the from and to using a 
        /// Regex to make sure they are a valid email address.
        /// </summary>
        /// <param name="from">Who this message is from (an agent or object in string format)</param>
        /// <param name="to">The reciver of the message specified by a valid email address</param>
        /// <param name="subject"></param>
        /// <param name="body"></param>
        MimeMessage FormatMessage(string from, string to, string subject, string body);

        /// <summary>
        /// Send one or more formatted MimeMessages given the SMTP parameters configured
        /// </summary>
        /// <param name="messages"></param>
        void SendMessages(IList<MimeMessage> messages);
    }
}
