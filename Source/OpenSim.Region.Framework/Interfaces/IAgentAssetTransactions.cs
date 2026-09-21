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
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.Framework.Interfaces;

public interface IAgentAssetTransactions
{
    /// <summary>
    /// Apply the asset an xfer transaction uploaded to an inventory item.
    /// </summary>
    /// <returns>
    /// A19: <b>false when the update was refused</b> - the referenced assets did not validate, so the asset was not
    /// stored and the item still points at what it pointed at before. It returns true when the update was applied
    /// and also when the xfer is still in flight, because at that point nothing has been validated and the region
    /// cannot honestly say the save failed; the caller learns of a later refusal the same way the legacy route
    /// does, through the alert and the bulk inventory update the uploader sends.
    /// </returns>
    bool HandleItemUpdateFromTransaction(IClientAPI remoteClient, UUID transactionID,
                                         InventoryItemBase item);

    bool HandleItemCreationFromTransaction(IClientAPI remoteClient, UUID transactionID, UUID folderID,
                                           uint callbackID, string description, string name, sbyte invType,
                                           sbyte type, byte wearableType, uint nextOwnerMask);

    void HandleTaskItemUpdateFromTransaction(
        IClientAPI remoteClient, SceneObjectPart part, UUID transactionID, TaskInventoryItem item);

    void RemoveAgentAssetTransactions(UUID userID);
}
