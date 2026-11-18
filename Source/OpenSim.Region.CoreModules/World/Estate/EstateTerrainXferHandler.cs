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
using OpenSim.Framework;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.CoreModules.World.Estate;


/// <summary>
/// A terrain file the estate tools upload (the viewer's LLXfer_File): numbered packets, each sent once the one before
/// was confirmed, the first starting with the file's size, the last flagged.
/// </summary>
public class EstateTerrainXferHandler
{
    private const uint LastPacket = 0x80000000;

    public delegate void TerrainUploadComplete(string name, byte[] filedata, IClientAPI remoteClient);
    public event TerrainUploadComplete TerrainUploadDone;

    public delegate void TerrainUploadFailure(string reason, IClientAPI remoteClient);
    public event TerrainUploadFailure TerrainUploadFailed;

    private readonly string m_name;
    private readonly long m_maxSize;
    private readonly MemoryStream m_data = new();
    private uint m_nextPacket;
    private bool m_done;

    public ulong mXferID;

    /// <param name="maxSize">The largest file taken, in bytes.</param>
    public EstateTerrainXferHandler(IClientAPI pRemoteClient, string pClientFilename, long maxSize)
    {
        Client = pRemoteClient;
        m_name = pClientFilename;
        m_maxSize = maxSize;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>The estate manager uploading.</summary>
    public IClientAPI Client { get; }

    /// <summary>When the upload started or last received a packet.</summary>
    public DateTime LastActivity { get; private set; }

    public ulong XferID
    {
        get { return mXferID; }
    }

    public void RequestStartXfer(IClientAPI pRemoteClient)
    {
        mXferID = Util.GetNextXferID();
        pRemoteClient.SendXferRequest(mXferID, 0, UUID.Zero, 0, Utils.StringToBytes(m_name));
    }

    /// <summary>
    /// Process transfer data received from the client.
    /// </summary>
    public void XferReceive(IClientAPI remoteClient, ulong xferID, uint packetID, byte[] data)
    {
        if (mXferID != xferID)
            return;

        string failure = null;
        bool complete = false;
        lock (this)
        {
            LastActivity = DateTime.UtcNow;
            uint number = packetID & ~LastPacket;

            // a packet taken already: its confirm was lost and the viewer sent it again. Confirm it again, but adding
            // it a second time would corrupt the file
            if (m_done || number < m_nextPacket)
            {
                remoteClient.SendConfirmXfer(xferID, packetID);
                return;
            }

            // ahead of the one expected: the viewer waits for the confirm of the one before, so it sends that again
            if (number > m_nextPacket)
                return;

            int offset = 0;
            if (number == 0)
            {
                // the first packet starts with the file's size
                if (data.Length < 4)
                    failure = "The terrain file was not sent correctly.";
                else if (Utils.BytesToInt(data, 0) is int declared && (declared < 0 || declared > m_maxSize))
                    failure = $"The terrain file is too large for this region ({declared} bytes, {m_maxSize} at most).";
                offset = 4;
            }

            if (failure is null && m_data.Length + data.Length - offset > m_maxSize)
                failure = $"The terrain file is too large for this region ({m_maxSize} bytes at most).";

            if (failure is null)
            {
                m_data.Write(data, offset, data.Length - offset);
                m_nextPacket++;
                remoteClient.SendConfirmXfer(xferID, packetID);
                if ((packetID & LastPacket) != 0)
                {
                    m_done = true;
                    complete = true;
                }
            }
            else
            {
                m_done = true;
            }
        }

        if (failure is not null)
        {
            remoteClient.SendAbortXferPacket(xferID);
            TerrainUploadFailed?.Invoke(failure, remoteClient);
        }
        else if (complete)
        {
            SendCompleteMessage(remoteClient);
        }
    }

    public void SendCompleteMessage(IClientAPI remoteClient)
    {
        TerrainUploadDone?.Invoke(m_name, m_data.ToArray(), remoteClient);
    }
}
