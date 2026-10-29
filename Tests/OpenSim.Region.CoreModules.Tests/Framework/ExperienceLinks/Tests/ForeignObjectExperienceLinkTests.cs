/*
 * Copyright (c) Legion Builds
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

using System.Reflection;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Asset;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.Framework.ExperienceLinks.Tests;

/// <summary>
/// Object data another grid puts on this grid comes with no Experience links: an object asset fetched from another
/// grid's asset server (an object a Hypergrid visitor rezzes from their home inventory or gives to a resident),
/// and the attachments an avatar brings from another grid. The link is cleared both on the script's task item and
/// in YEngine's saved script state, which YEngine restores it from. Data from this grid keeps its links.
/// </summary>
public class ForeignObjectExperienceLinkTests : OpenSimTestCase
{
    private static readonly UUID s_experience = new("2d9a6c13-f4b7-4e08-9b5a-0c7e31f8d264");
    private static readonly UUID s_owner = new("71c0e5a8-36d2-4b9f-8a14-d5f2b09c6e37");
    private const string ForeignAssets = "http://assets.example.org:8002";

    /// <summary>A stand-in for an interface; each call goes to the handler, others return the default.</summary>
    public class Stand<T> : DispatchProxy where T : class
    {
        private Func<string, object?[], object?> m_handler = null!;

        public static T Create(Func<string, object?[], object?> handler)
        {
            T proxy = Create<T, Stand<T>>();
            ((Stand<T>)(object)proxy).m_handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            MethodInfo targetMethod = method ?? throw new InvalidOperationException("The service proxy received no target method.");
            object? result = m_handler(targetMethod.Name, args ?? Array.Empty<object?>());
            if (result is not null)
                return result;
            Type rt = targetMethod.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private static (SceneObjectGroup sog, TaskInventoryItem script) ObjectWithScript(UUID experience)
    {
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, s_owner);
        TaskInventoryItem script = new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            AssetID = UUID.Random(),
            Name = "script",
            Type = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            OwnerID = s_owner,
            CreatorID = s_owner,
            ExperienceID = experience
        };
        sog.RootPart.Inventory.AddInventoryItem(script, false);
        return (sog, script);
    }

    /// <summary>A YEngine state for one script, as an object's saved script states carry it.</summary>
    private static string YEngineState(UUID itemID, UUID experience)
        => $"<State UUID=\"{itemID}\" Engine=\"YEngine\"><ScriptState><State>default</State><Running>True</Running>" +
           $"<ExperienceKey>{experience}</ExperienceKey></ScriptState></State>";

    /// <summary>Object XML with a script whose item and saved state both name an Experience.</summary>
    private static (string xml, TaskInventoryItem script) ObjectXml(UUID experience)
    {
        (SceneObjectGroup sog, TaskInventoryItem script) = ObjectWithScript(experience);
        string xml = SceneObjectSerializer.ToOriginalXmlFormat(sog);
        string states = $"<GroupScriptStates><SavedScriptState UUID=\"{script.ItemID}\">{YEngineState(script.ItemID, experience)}</SavedScriptState></GroupScriptStates>";
        int end = xml.LastIndexOf("</SceneObjectGroup>", StringComparison.Ordinal);
        return (xml.Substring(0, end) + states + xml.Substring(end), script);
    }

    private static UUID ItemLink(string xml, UUID itemID)
        => SceneObjectSerializer.FromOriginalXmlFormat(xml).RootPart.Inventory.GetInventoryItem(itemID).ExperienceID;

    // ---- object assets from another grid's asset server ---------------------------------------------------------

    private sealed class Assets
    {
        public readonly Dictionary<string, AssetBase> Local = new();
        public readonly Dictionary<string, AssetBase> Foreign = new();
        public readonly List<AssetBase> Stored = new();
    }

    private static RegionAssetConnector Connector(Assets assets)
    {
        IAssetService local = Stand<IAssetService>.Create((name, a) => name switch
        {
            "Get" when a.Length == 1 => assets.Local.TryGetValue(Argument<string>(a, 0), out AssetBase? l) && l is not null ? l : null,
            "Store" => Store(assets, Argument<AssetBase>(a, 0)),
            _ => null
        });
        IAssetService foreign = Stand<IAssetService>.Create((name, a) => name switch
        {
            "Get" when a.Length == 3 => assets.Foreign.TryGetValue(Argument<string>(a, 0), out AssetBase? f) && f is not null ? Copy(f) : null,
            _ => null
        });
        RegionAssetConnector module = new RegionAssetConnector();
        FieldInfo localField = typeof(RegionAssetConnector).GetField("m_localConnector", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(typeof(RegionAssetConnector).FullName, "m_localConnector");
        FieldInfo hgField = typeof(RegionAssetConnector).GetField("m_HGConnector", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(typeof(RegionAssetConnector).FullName, "m_HGConnector");
        localField.SetValue(module, local);
        hgField.SetValue(module, foreign);
        return module;
    }

    private static string Store(Assets assets, AssetBase asset)
    {
        assets.Stored.Add(asset);
        assets.Local[asset.ID] = asset;
        return asset.ID;
    }

    private static T Argument<T>(object?[] args, int index)
    {
        if (args.Length <= index || args[index] is not T value)
            throw new InvalidOperationException($"Service proxy argument {index} is not a {typeof(T).Name}.");
        return value;
    }

    private static AssetBase Copy(AssetBase a)
        => new AssetBase(a.FullID, a.Name, a.Type, a.Metadata.CreatorID) { Data = (byte[])a.Data.Clone() };

    private static AssetBase ObjectAsset(string xml)
        => new AssetBase(UUID.Random(), "object", (sbyte)AssetType.Object, s_owner.ToString()) { Data = Encoding.UTF8.GetBytes(xml) };

    [Fact]
    public void AnObjectFetchedFromAnotherGridsAssetServerHasNoExperienceLinks()
    {
        (string xml, TaskInventoryItem script) = ObjectXml(s_experience);
        Assets assets = new();
        AssetBase asset = ObjectAsset(xml);
        assets.Foreign[asset.ID] = asset;

        AssetBase fetched = Connector(assets).Get(asset.ID, ForeignAssets, true);

        string fetchedXml = Encoding.UTF8.GetString(fetched.Data);
        Assert.Equal(UUID.Zero, ItemLink(fetchedXml, script.ItemID));
        Assert.DoesNotContain(s_experience.ToString(), fetchedXml);
        Assert.Contains("ExperienceKey", fetchedXml);   // the state is kept, its link zeroed
        AssetBase stored = Assert.Single(assets.Stored);
        Assert.DoesNotContain(s_experience.ToString(), Encoding.UTF8.GetString(stored.Data));
    }

    [Fact]
    public void ACoalescedObjectFromAnotherGridHasNoExperienceLinks()
    {
        (SceneObjectGroup a, TaskInventoryItem scriptA) = ObjectWithScript(s_experience);
        (SceneObjectGroup b, TaskInventoryItem scriptB) = ObjectWithScript(s_experience);
        CoalescedSceneObjects coa = new CoalescedSceneObjects(s_owner, new[] { a, b });
        Assets assets = new();
        AssetBase asset = ObjectAsset(CoalescedSceneObjectsSerializer.ToXml(coa));
        assets.Foreign[asset.ID] = asset;

        AssetBase fetched = Connector(assets).Get(asset.ID, ForeignAssets, true);

        Assert.True(CoalescedSceneObjectsSerializer.TryFromXml(Encoding.UTF8.GetString(fetched.Data), out CoalescedSceneObjects loaded));
        Assert.Equal(2, loaded.Count);
        foreach (SceneObjectGroup sog in loaded.Objects)
            foreach (TaskInventoryItem item in sog.RootPart.Inventory.GetInventoryItems())
                Assert.Equal(UUID.Zero, item.ExperienceID);
    }

    [Fact]
    public void AnObjectAlreadyOnThisGridKeepsItsExperienceLinks()
    {
        (string xml, TaskInventoryItem script) = ObjectXml(s_experience);
        Assets assets = new();
        AssetBase asset = ObjectAsset(xml);
        assets.Local[asset.ID] = asset;
        assets.Foreign[asset.ID] = asset;
        RegionAssetConnector connector = Connector(assets);

        Assert.Equal(s_experience, ItemLink(Encoding.UTF8.GetString(connector.Get(asset.ID, ForeignAssets, true).Data), script.ItemID));
        Assert.Equal(s_experience, ItemLink(Encoding.UTF8.GetString(connector.Get(asset.ID).Data), script.ItemID));
        Assert.Empty(assets.Stored);
    }

    [Fact]
    public void OtherAssetsFromAnotherGridAreUnchanged()
    {
        Assets assets = new();
        byte[] text = Encoding.UTF8.GetBytes($"<TaskInventoryItem><ExperienceID>{s_experience}</ExperienceID></TaskInventoryItem>");
        AssetBase notecard = new AssetBase(UUID.Random(), "notecard", (sbyte)AssetType.Notecard, s_owner.ToString()) { Data = text };
        assets.Foreign[notecard.ID] = notecard;

        AssetBase fetched = Connector(assets).Get(notecard.ID, ForeignAssets, true);

        Assert.Equal(text, fetched.Data);
    }

    // ---- attachments an avatar brings from another grid -------------------------------------------------------

    private sealed class Arrival
    {
        public Scene Scene = null!;
        public AttachmentsModule Attachments = null!;
        public ScenePresence Presence = null!;
        public readonly List<string> StatesGiven = new();
    }

    private static Arrival ArriveWith(bool throughTheHypergridGatekeeper)
    {
        Arrival arrival = new();
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        Scene scene = new SceneHelpers().SetupScene();
        scene.RegisterModuleInterface<IScriptModule>(Stand<IScriptModule>.Create((name, a) => name switch
        {
            "get_ScriptEngineName" => "YEngine",
            "SetXMLState" => Record(arrival, Argument<string>(a, 1)),
            _ => null
        }));
        // The transfer module accepts the attachments and does nothing else.
        scene.RegisterModuleInterface<IEntityTransferModule>(Stand<IEntityTransferModule>.Create((name, a) => name switch
        {
            "HandleIncomingAttachments" => true,
            _ => null
        }));
        arrival.Attachments = new AttachmentsModule();
        SceneHelpers.SetupSceneModules(scene, config, arrival.Attachments);

        AgentCircuitData acd = SceneHelpers.GenerateAgentData(s_owner);
        arrival.Scene = scene;
        arrival.Presence = SceneHelpers.AddScenePresence(scene, acd);
        // As the Hypergrid gatekeeper marks an avatar it launches into this grid from another one.
        if (throughTheHypergridGatekeeper)
            scene.AuthenticateHandler.GetAgentCircuitData(s_owner).teleportFlags |= (uint)Constants.TeleportFlags.ViaHGLogin;
        return arrival;
    }

    private static object Record(Arrival arrival, string state)
    {
        arrival.StatesGiven.Add(state);
        return true;
    }

    private static (SceneObjectGroup sog, TaskInventoryItem script) Brings(Arrival arrival)
    {
        (SceneObjectGroup sog, TaskInventoryItem script) = ObjectWithScript(s_experience);
        AgentData ad = new AgentData
        {
            AttachmentObjects = new List<ISceneObject> { sog },
            AttachmentObjectStates = new List<string>
            {
                $"<ScriptData><ScriptStates>{YEngineState(script.ItemID, s_experience)}</ScriptStates></ScriptData>"
            }
        };
        arrival.Attachments.CopyAttachments(ad, arrival.Presence);
        return (sog, script);
    }

    [Fact]
    public void AttachmentsAnAvatarBringsFromAnotherGridHaveNoExperienceLinks()
    {
        Arrival arrival = ArriveWith(throughTheHypergridGatekeeper: true);

        (SceneObjectGroup sog, TaskInventoryItem script) = Brings(arrival);

        Assert.Equal(UUID.Zero, sog.RootPart.Inventory.GetInventoryItem(script.ItemID).ExperienceID);
        string state = Assert.Single(arrival.StatesGiven);
        Assert.DoesNotContain(s_experience.ToString(), state);
        Assert.Contains($"<ExperienceKey>{UUID.Zero}</ExperienceKey>", state);
    }

    [Fact]
    public void AttachmentsMovedWithinThisGridKeepTheirExperienceLinks()
    {
        Arrival arrival = ArriveWith(throughTheHypergridGatekeeper: false);

        (SceneObjectGroup sog, TaskInventoryItem script) = Brings(arrival);

        Assert.Equal(s_experience, sog.RootPart.Inventory.GetInventoryItem(script.ItemID).ExperienceID);
        Assert.Contains(s_experience.ToString(), Assert.Single(arrival.StatesGiven));
    }
}
