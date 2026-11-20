/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;
using OSDArray = OpenMetaverse.StructuredData.OSDArray;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The reply to a script saved through the UpdateScriptTask capability (a script inside an object) carries the id of
/// the asset the save stored in new_asset, as the UpdateScriptAgent reply does. The LL viewer reads new_asset as the
/// new asset id in LLBufferedAssetUploadInfo::finishUpload (indra/newview/llviewerassetupload.cpp) and hands it to
/// LLLiveLSLEditor::finishLSLUpload (indra/newview/llpreviewscript.cpp), which sets the editor's item and asset to it.
/// A save that stored nothing answers in the error form: LLViewerAssetUpload::AssetInventoryUploadCoproc treats any
/// reply whose "state" is not "complete" as a failed upload and shows its "message".
/// The tests drive the real capability handler and the real uploader it registers, as a viewer would.
/// </summary>
public class TaskScriptSaveReplyTests : OpenSimTestCase
{
    private static readonly IPAddress ViewerAddress = IPAddress.Parse("192.0.2.1");

    /// <summary>Answers every interface call with a default, except the calls a test overrides.</summary>
    public class Fake<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object[], (bool Handled, object Result)> OnCall = (_, _) => (false, null);

        public static (T Proxy, Fake<T> Control) Create()
        {
            T proxy = DispatchProxy.Create<T, Fake<T>>();
            return (proxy, (Fake<T>)(object)proxy);
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            var (handled, result) = OnCall(method, args);
            if (handled)
                return result;
            Type type = method.ReturnType;
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class Rig
    {
        public readonly Scene Scene;
        public readonly UUID AgentId = UUID.Random();
        public readonly BunchOfCaps Caps;
        public readonly Dictionary<string, ISimpleStreamHandler> Handlers = new();
        public SceneObjectPart Part;
        public TaskInventoryItem Item;

        public Rig(bool agentInventory = false)
        {
            var config = new IniConfigSource();
            Scene = new SceneHelpers().SetupScene("Unit test region", UUID.Random(), 1000, 1000, config);
            if (agentInventory)
            {
                config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
                SceneHelpers.SetupSceneModules(Scene, config, new BasicInventoryAccessModule());
                UserAccountHelpers.CreateUserWithInventory(Scene, AgentId);
            }
            SceneHelpers.AddScenePresence(Scene, AgentId);

            Part = SceneHelpers.AddSceneObject(Scene).RootPart;
            Item = TaskInventoryHelpers.AddScript(Scene.AssetService, Part, "script", "default { state_entry() { } }");

            var (server, control) = Fake<IHttpServer>.Create();
            control.OnCall = (method, args) =>
            {
                if (method.Name == nameof(IHttpServer.AddSimpleStreamHandler))
                {
                    var handler = (ISimpleStreamHandler)args[0];
                    Handlers[handler.Path] = handler;
                    return (true, null);
                }
                return (false, null);
            };
            var caps = new Caps(server, "example.org", 9000, "/CAPS/" + UUID.Random() + "/", AgentId, "Example Region");
            Caps = new BunchOfCaps(Scene, AgentId, caps, new BunchOfCapsConfigOptions());
        }

        private static IOSHttpRequest Request(byte[] body)
        {
            var (request, control) = Fake<IOSHttpRequest>.Create();
            control.OnCall = (method, _) => method.Name switch
            {
                "get_HttpMethod" => (true, "POST"),
                "get_InputStream" => (true, body is null ? null : new MemoryStream(body)),
                "get_RemoteIPEndPoint" => (true, new IPEndPoint(ViewerAddress, 0)),
                _ => (false, null),
            };
            return request;
        }

        private static OSDMap Reply(TestOSHttpResponse response)
        {
            Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(response.RawBuffer);
            return (OSDMap)OSDParser.DeserializeLLSDXml(response.RawBuffer);
        }

        /// <summary>The viewer's first request: asks the capability where to upload, and returns that path.</summary>
        public string AskForUploader(Action<IOSHttpRequest, IOSHttpResponse, OSDMap> capability, OSDMap body)
        {
            var response = new TestOSHttpResponse();
            capability(Request(null), response, body);
            OSDMap reply = Reply(response);
            Assert.Equal("upload", reply["state"].AsString());
            string path = new Uri(reply["uploader"].AsString()).AbsolutePath;
            Assert.True(Handlers.ContainsKey(path), "the uploader is registered on the listener");
            return path;
        }

        /// <summary>The viewer's second request: posts the script text to the uploader and returns its reply.</summary>
        public OSDMap Upload(string path, string text)
        {
            var response = new TestOSHttpResponse();
            Handlers[path].Handle(Request(Encoding.UTF8.GetBytes(text)), response);
            return Reply(response);
        }

        public string AskForTaskUploader(UUID experience = default)
            => AskForUploader(Caps.UpdateScriptTaskInventory, new OSDMap
            {
                ["item_id"] = Item.ItemID,
                ["task_id"] = Part.UUID,
                ["is_script_running"] = false,
                ["experience"] = experience,
            });

        public OSDMap SaveInObject(string text, UUID experience = default) => Upload(AskForTaskUploader(experience), text);

        /// <summary>
        /// Replaces TaskScriptUpdatedCall the way a region module that knows only that field would, with a callback
        /// that does the scene's save; returns how many times it was called.
        /// </summary>
        public Func<int> ReplaceTheOlderCallback()
        {
            int calls = 0;
            Caps.TaskScriptUpdatedCall = (user, item, prim, running, experience, data, target) =>
            {
                calls++;
                return Scene.CapsUpdateTaskInventoryScriptAsset(user, item, prim, running, experience, data, target);
            };
            return () => calls;
        }

        public UUID StoredAsset => Part.Inventory.GetInventoryItem(Item.ItemID).AssetID;
    }

    [Fact]
    public void SavingAScriptInAnObjectAnswersTheStoredAssetId()
    {
        var rig = new Rig();
        UUID oldAsset = rig.StoredAsset;

        OSDMap reply = rig.SaveInObject("default { state_entry() { llSay(0, \"one\"); } }");

        Assert.Equal("complete", reply["state"].AsString());
        UUID stored = rig.StoredAsset;
        Assert.NotEqual(oldAsset, stored);
        Assert.Equal(stored, reply["new_asset"].AsUUID());
        Assert.NotEqual(rig.Item.ItemID, reply["new_asset"].AsUUID());
        // the stored asset is the text the viewer sent
        Assert.Equal("default { state_entry() { llSay(0, \"one\"); } }",
            Encoding.UTF8.GetString(rig.Scene.AssetService.Get(stored.ToString()).Data));
    }

    [Fact]
    public void EachSaveAnswersItsOwnNewAsset()
    {
        var rig = new Rig();

        OSDMap first = rig.SaveInObject("default { state_entry() { } }");
        UUID afterFirst = rig.StoredAsset;
        OSDMap second = rig.SaveInObject("default { state_entry() { } }");
        UUID afterSecond = rig.StoredAsset;

        Assert.NotEqual(afterFirst, afterSecond);
        Assert.Equal(afterFirst, first["new_asset"].AsUUID());
        Assert.Equal(afterSecond, second["new_asset"].AsUUID());
    }

    public enum Cause { ItemRemoved, ObjectDeleted, PermissionWithdrawn }

    [Theory]
    [InlineData(Cause.ItemRemoved, false)]
    [InlineData(Cause.ObjectDeleted, false)]
    [InlineData(Cause.PermissionWithdrawn, false)]
    [InlineData(Cause.ItemRemoved, true)]
    [InlineData(Cause.ObjectDeleted, true)]
    [InlineData(Cause.PermissionWithdrawn, true)]
    public void ASaveThatStoredNothingAnswersAnError(Cause cause, bool olderCallbackReplaced)
    {
        var rig = new Rig();
        Func<int> calls = olderCallbackReplaced ? rig.ReplaceTheOlderCallback() : null;
        bool mayEdit = true;
        rig.Scene.Permissions.OnEditScript += (_, _, _) => mayEdit;
        UUID oldAsset = rig.StoredAsset;
        string path = rig.AskForTaskUploader();

        // the change comes between the viewer's two requests, so the save has nothing to store into or may not store
        switch (cause)
        {
            case Cause.ItemRemoved: rig.Part.Inventory.RemoveInventoryItem(rig.Item.ItemID); break;
            case Cause.ObjectDeleted: rig.Scene.DeleteSceneObject(rig.Part.ParentGroup, false); break;
            case Cause.PermissionWithdrawn: mayEdit = false; break;
        }

        OSDMap reply = rig.Upload(path, "default { state_entry() { } }");

        AssertSaveFailed(reply, rig.Item.ItemID);
        if (cause == Cause.PermissionWithdrawn)
            Assert.Equal(oldAsset, rig.StoredAsset);
        if (olderCallbackReplaced)
            Assert.Equal(1, calls());
    }

    [Fact]
    public void AModuleThatReplacedTheOlderCallbackStillDoesTheSave()
    {
        var rig = new Rig();
        Func<int> calls = rig.ReplaceTheOlderCallback();

        OSDMap reply = rig.SaveInObject("default { state_entry() { } }");

        Assert.Equal(1, calls());
        Assert.Equal("complete", reply["state"].AsString());
        Assert.Equal(rig.StoredAsset, reply["new_asset"].AsUUID());
        Assert.NotEqual(rig.Item.ItemID, reply["new_asset"].AsUUID());
    }

    [Fact]
    public void AModuleThatClearedTheOlderCallbackStopsTheSave()
    {
        var rig = new Rig();
        UUID oldAsset = rig.StoredAsset;
        rig.Caps.TaskScriptUpdatedCall = null;

        OSDMap reply = rig.SaveInObject("default { state_entry() { } }");

        AssertSaveFailed(reply, rig.Item.ItemID);
        Assert.Equal(oldAsset, rig.StoredAsset);
    }

    [Fact]
    public void ASaveRefusedTheExperienceStillStoresAndAnswersTheAssetWithTheError()
    {
        var rig = new Rig();
        var (experiences, control) = Fake<IExperienceModule>.Create();
        control.OnCall = (method, _) => method.Name == nameof(IExperienceModule.IsExperienceContributor) ? (true, false) : (false, null);
        rig.Scene.RegisterModuleInterface(experiences);
        UUID oldAsset = rig.StoredAsset;

        OSDMap reply = rig.SaveInObject("default { state_entry() { } }", UUID.Random());

        // Scene.CapsUpdateTaskInventoryScriptAsset stores the text without the experience and reports the refusal
        UUID stored = rig.StoredAsset;
        Assert.NotEqual(oldAsset, stored);
        Assert.Equal("complete", reply["state"].AsString());
        Assert.Equal(stored, reply["new_asset"].AsUUID());
        Assert.False(reply["compiled"].AsBoolean());
        Assert.Contains("Access denied to experience!", ((OSDArray)reply["errors"]).Select(e => e.AsString()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASaveThatThrowsAnswersTheAssetItStoredOrAnError(bool storedFirst)
    {
        var rig = new Rig();
        rig.Caps.TaskScriptSavedCall = (UUID user, UUID item, UUID prim, bool running, UUID experience, byte[] data, string target, out UUID newAsset) =>
        {
            newAsset = UUID.Zero;
            if (storedFirst)
                rig.Scene.CapsUpdateTaskInventoryScriptAsset(user, item, prim, running, experience, data, target, out newAsset);
            throw new InvalidOperationException("the script could not be started");
        };
        UUID oldAsset = rig.StoredAsset;

        OSDMap reply = rig.SaveInObject("default { state_entry() { } }");

        if (storedFirst)
        {
            // the item points at the stored script, so the viewer is told that asset, with the error
            Assert.NotEqual(oldAsset, rig.StoredAsset);
            Assert.Equal("complete", reply["state"].AsString());
            Assert.Equal(rig.StoredAsset, reply["new_asset"].AsUUID());
            Assert.False(reply["compiled"].AsBoolean());
            Assert.NotEmpty((OSDArray)reply["errors"]);
        }
        else
        {
            Assert.Equal(oldAsset, rig.StoredAsset);
            Assert.NotEqual("complete", reply["state"].AsString());
            Assert.False(reply.ContainsKey("new_asset"));
        }
    }

    private static void AssertSaveFailed(OSDMap reply, UUID itemId)
    {
        // AssetInventoryUploadCoproc: a state other than "complete" is a failed upload, and "message" is what it shows
        Assert.NotEqual("complete", reply["state"].AsString());
        Assert.False(string.IsNullOrEmpty(reply["message"].AsString()));
        Assert.False(reply.ContainsKey("new_asset"), "a failed save names no asset");
        Assert.False(reply.ContainsKey("compiled"), "a failed save does not say it compiled");
        Assert.Equal(itemId, reply["identifier"].AsUUID());
    }

    [Fact]
    public void SavingAScriptInTheAvatarsInventoryAnswersAsBefore()
    {
        var rig = new Rig(agentInventory: true);
        AssetBase asset = AssetHelpers.CreateNotecardAsset();
        asset.Type = (sbyte)AssetType.LSLText;
        rig.Scene.AssetService.Store(asset);
        InventoryFolderBase folder = rig.Scene.InventoryService.GetFolderForType(rig.AgentId, FolderType.LSLText);
        var item = new InventoryItemBase
        {
            Name = "agent script",
            AssetID = asset.FullID,
            ID = UUID.Random(),
            Owner = rig.AgentId,
            AssetType = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            BasePermissions = (uint)OpenMetaverse.PermissionMask.All,
            CurrentPermissions = (uint)OpenMetaverse.PermissionMask.All,
            Folder = folder.ID,
        };
        rig.Scene.AddInventoryItem(item);

        string path = rig.AskForUploader(rig.Caps.UpdateScriptItemAsset, new OSDMap { ["item_id"] = item.ID });
        OSDMap reply = rig.Upload(path, "default { state_entry() { } }");

        UUID stored = rig.Scene.InventoryService.GetItem(rig.AgentId, item.ID).AssetID;
        Assert.NotEqual(asset.FullID, stored);
        Assert.Equal("complete", reply["state"].AsString());
        Assert.Equal(stored, reply["new_asset"].AsUUID());
        Assert.Equal(item.ID, reply["new_inventory_item"].AsUUID());
    }
}
