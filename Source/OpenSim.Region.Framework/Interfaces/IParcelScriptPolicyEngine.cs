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

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// Optional interface for an <see cref="IScriptModule"/> that applies the parcel script rules
/// (No Scripts parcels: ParcelFlags.AllowOtherScripts and AllowGroupScripts) itself.
/// </summary>
/// <remarks>
/// When the engine that will run a script implements this and returns true, the core's start-time parcel check
/// (the permissions module's CanRunScript parcel rule) lets the script start on any parcel, and the engine decides
/// whether it runs, typically by starting it paused and following EventManager.OnGroupCrossedToNewParcel,
/// OnObjectOwnerOrGroupChanged and the land events.
/// The engine is resolved per script: the engine named on the script's first line ("//engine:language") when that
/// engine is loaded, otherwise the default script engine.
/// An engine that does not implement this, or returns false, gets the core's parcel check unchanged.
/// </remarks>
public interface IParcelScriptPolicyEngine
{
    /// <summary>
    /// True if this engine enforces the parcel script rules itself, so the core must not refuse its scripts
    /// on parcel grounds.
    /// </summary>
    bool EnforcesParcelScriptRules { get; }
}
