using System.Collections;
using System.Net;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework.Servers.HttpServer;
using Microsoft.Extensions.Logging;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

namespace OpenSim.Region.ClientStack.LindenCaps;

public delegate UUID UpdateItem(UUID itemID, UUID objectID, byte[] data);
public delegate UUID ItemUpdatedCallback(UUID userID, UUID itemID, UUID objectID, byte[] data, string compileTarget);

public partial class BunchOfCaps
{
    public void UpdateNotecardItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
    {
        UpdateInventoryItemAsset(httpRequest, httpResponse, map, (byte)AssetType.Notecard);
    }

    public void UpdateAnimSetItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
    {
        //UpdateInventoryItemAsset(httpRequest, httpResponse, map, CustomInventoryType.AnimationSet);
    }

    public void UpdateScriptItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
    {
        UpdateInventoryItemAsset(httpRequest, httpResponse, map, (byte)AssetType.LSLText);
    }

    public void UpdateSettingsItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
    {
        UpdateInventoryItemAsset(httpRequest, httpResponse, map, (byte)AssetType.Settings);
    }

    public void UpdateMaterialItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
    {
        UpdateInventoryItemAsset(httpRequest, httpResponse, map, (byte)AssetType.Material);
    }

    public void UpdateGestureItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
    {
        UpdateInventoryItemAsset(httpRequest, httpResponse, map, (byte)AssetType.Gesture);
    }

    private void UpdateInventoryItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map, byte atype, bool taskSript = false)
    {
        m_log.LogDebug("[CAPS]: UpdateInventoryItemAsset Request in region: " + m_regionName + "\n");

        httpResponse.StatusCode = (int)HttpStatusCode.OK;

        UUID itemID = UUID.Zero;
        UUID objectID = UUID.Zero;
        string compileTarget = null;

        try
        {
            if (map.TryGetValue("item_id", out OSD itmp))
                itemID = itmp;
            if (map.TryGetValue("task_id", out OSD tmp))
                objectID = tmp;
            // a script saved from the viewer's editor says which compiler was chosen: mono, lsl2, lsl-luau or luau
            if (atype == (byte)AssetType.LSLText && map.TryGetValue("target", out OSD target))
                compileTarget = target.AsString();
        }
        catch { }

        if (itemID.IsZero())
        {
            LLSDAssetUploadError error = new LLSDAssetUploadError();
            error.message = "failed to recode request";
            error.identifier = UUID.Zero;
            httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
            return;
        }

        if (!objectID.IsZero())
        {
            SceneObjectPart sop = m_Scene.GetSceneObjectPart(objectID);
            if (sop == null)
            {
                LLSDAssetUploadError error = new LLSDAssetUploadError();
                error.message = "object not found";
                error.identifier = UUID.Zero;
                httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                return;
            }

            if (!m_Scene.Permissions.CanEditObjectInventory(objectID, m_AgentID))
            {
                LLSDAssetUploadError error = new LLSDAssetUploadError();
                error.message = "No permissions to edit objec";
                error.identifier = UUID.Zero;
                httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                return;
            }
        }

        // Each of these caps saves one type of item: the notecard cap a notecard, the script cap a script, and so
        // on. The checks that follow the upload are chosen by the item's type, so an item of another type is refused.
        sbyte itemType = ItemAssetType(itemID, objectID);
        if (itemType != (sbyte)atype)
        {
            LLSDAssetUploadError error = new LLSDAssetUploadError();
            error.message = itemType == sbyte.MinValue ? "item not found" : "item is not of this type";
            error.identifier = UUID.Zero;
            httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
            return;
        }

        string uploaderPath = GetNewCapPath();

        string protocol = m_HostCapsObj.SSLCaps ? "https://" : "http://";
        string uploaderURL = protocol + m_HostCapsObj.HostName + ":" + m_HostCapsObj.Port.ToString() + uploaderPath;
        LLSDAssetUploadResponse uploadResponse = new LLSDAssetUploadResponse();
        uploadResponse.uploader = uploaderURL;
        uploadResponse.state = "upload";

        ItemUpdater uploader = new ItemUpdater(itemID, objectID, atype, uploaderPath, m_HostCapsObj.HttpListener, m_dumpAssetsToFile);
        uploader.m_remoteAdress = httpRequest.RemoteIPEndPoint.Address;

        uploader.OnUpLoad += (id, oid, data) => ItemUpdated(id, oid, data, compileTarget);

        var uploaderHandler = new SimpleBinaryHandler("POST", uploaderPath, uploader.process);

        uploaderHandler.MaxDataSize = 10000000; // change per asset type?
        
        m_HostCapsObj.HttpListener.AddSimpleStreamHandler(uploaderHandler);

        // m_log.LogInformation("[CAPS]: UpdateAgentInventoryAsset response: {0}",
        //                             LLSDHelpers.SerialiseLLSDReply(uploadResponse)));

        httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(uploadResponse));
    }

    /// <summary>
    /// The asset type of the item the caller names: in their inventory, or in the object's when
    /// <paramref name="objectID"/> is set. <see cref="sbyte.MinValue"/> when there is no such item.
    /// </summary>
    private sbyte ItemAssetType(UUID itemID, UUID objectID)
    {
        if (objectID.IsZero())
        {
            InventoryItemBase item = m_Scene.InventoryService.GetItem(m_AgentID, itemID);
            return item is null || item.Owner.NotEqual(m_AgentID) ? sbyte.MinValue : (sbyte)item.AssetType;
        }

        TaskInventoryItem taskItem = m_Scene.GetSceneObjectPart(objectID)?.Inventory.GetInventoryItem(itemID);
        return taskItem is null ? sbyte.MinValue : (sbyte)taskItem.Type;
    }

    /// <summary>
    /// Called when new asset data for an inventory item update has been uploaded.
    /// </summary>
    /// <param name="itemID">Item to update</param>
    /// <param name="data">New asset data</param>
    /// <returns></returns>
    public UUID ItemUpdated(UUID itemID, UUID objectID, byte[] data, string compileTarget = null)
    {
        if (ItemUpdatedCall != null)
        {
            return ItemUpdatedCall(m_HostCapsObj.AgentID, itemID, objectID, data, compileTarget);
        }
        return UUID.Zero;
    }

    /// <summary>
    /// The SetScriptEngine capability: {task_id, item_id, target} chooses the engine of a script in an object without
    /// saving its text (<see cref="Scene.SetScriptEngine"/>); an empty or missing target clears the choice. The reply is
    /// {state: "complete", result, EngineChoice} when the choice is in place (result "Changed" or "Unchanged"), and
    /// {state: "error", result, error} otherwise, where result names the <see cref="ScriptEngineChoiceResult"/>.
    /// </summary>
    public void SetScriptEngineRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
    {
        httpResponse.StatusCode = (int)HttpStatusCode.OK;
        UUID itemID = UUID.Zero;
        UUID objectID = UUID.Zero;
        string target = null;
        if (map.TryGetValue("item_id", out OSD tmp))
            itemID = tmp.AsUUID();
        if (map.TryGetValue("task_id", out tmp))
            objectID = tmp.AsUUID();
        if (map.TryGetValue("target", out tmp))
            target = tmp.AsString();

        OSDMap reply = new();
        if (itemID.IsZero() || objectID.IsZero())
        {
            reply["state"] = "error";
            reply["result"] = "BadRequest";
            reply["error"] = "task_id and item_id are needed";
        }
        else
        {
            ScriptEngineChoiceResult result = m_Scene.SetScriptEngine(m_AgentID, objectID, itemID, target);
            reply["result"] = result.ToString();
            if (result == ScriptEngineChoiceResult.Changed || result == ScriptEngineChoiceResult.Unchanged)
            {
                reply["state"] = "complete";
                reply["EngineChoice"] = m_Scene.GetScriptEngineChoice(objectID, itemID);
            }
            else
            {
                reply["state"] = "error";
                reply["error"] = result switch
                {
                    ScriptEngineChoiceResult.NotFound => "script not found",
                    ScriptEngineChoiceResult.NotAllowed => "no permission to choose this script's engine",
                    ScriptEngineChoiceResult.Disabled => "this region keeps no script engine choices",
                    ScriptEngineChoiceResult.UnknownEngine => "no such script engine",
                    ScriptEngineChoiceResult.NotSwitched => "the script cannot run on that engine in this region",
                    _ => result.ToString(),
                };
            }
        }
        httpResponse.RawBuffer = OSDParser.SerializeLLSDXmlBytes(reply);
    }

    /// <summary>
    /// Called by the script task update handler.  Provides a URL to which the client can upload a new asset.
    /// </summary>
    /// <param name="httpRequest">HTTP request header object</param>
    /// <param name="httpResponse">HTTP response header object</param>
    /// <returns></returns>
    public void UpdateScriptTaskInventory(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
    {
        httpResponse.StatusCode = (int)HttpStatusCode.OK;

        try
        {
            //m_log.LogDebug("[CAPS]: ScriptTaskInventory Request in region: " + m_regionName);
            //m_log.LogDebug("[CAPS]: request: {0}, path: {1}, param: {2}", request, path, param);

            UUID itemID = UUID.Zero;
            UUID objectID = UUID.Zero;
            bool is_script_running = false;
            UUID experience_key = UUID.Zero;
            string compileTarget = null;
            OSD tmp;
            try
            {
                if (map.TryGetValue("item_id", out tmp))
                    itemID = tmp;
                if (map.TryGetValue("task_id", out tmp))
                    objectID = tmp;
                if (map.TryGetValue("is_script_running", out tmp))
                    is_script_running = tmp;
                if (map.TryGetValue("experience", out tmp))
                    experience_key = tmp;
                if (map.TryGetValue("target", out tmp))
                    compileTarget = tmp.AsString();
            }
            catch { }

            if (itemID.IsZero() || objectID.IsZero())
            {
                LLSDAssetUploadError error = new LLSDAssetUploadError();
                error.message = "failed to recode request";
                error.identifier = UUID.Zero;
                httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                return;
            }

            SceneObjectPart sop = m_Scene.GetSceneObjectPart(objectID);
            if (sop == null)
            {
                LLSDAssetUploadError error = new LLSDAssetUploadError();
                error.message = "object not found";
                error.identifier = UUID.Zero;
                httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                return;
            }

            if (!m_Scene.Permissions.CanEditObjectInventory(objectID, m_AgentID))
            {
                LLSDAssetUploadError error = new LLSDAssetUploadError();
                error.message = "No permissions to edit objec";
                error.identifier = UUID.Zero;
                httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                return;
            }

            if (!m_Scene.Permissions.CanEditScript(itemID, objectID, m_AgentID))
            {
                LLSDAssetUploadError error = new LLSDAssetUploadError();
                error.message = "No permissions to edit script";
                error.identifier = UUID.Zero;
                httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                return;
            }

            string uploaderPath = GetNewCapPath();
            string protocol = m_HostCapsObj.SSLCaps ? "https://" : "http://";
            string uploaderURL = protocol + m_HostCapsObj.HostName + ":" + m_HostCapsObj.Port.ToString() + uploaderPath;
            LLSDAssetUploadResponse uploadResponse = new LLSDAssetUploadResponse();
            uploadResponse.uploader = uploaderURL;
            uploadResponse.state = "upload";

            TaskInventoryScriptUpdater uploader = new TaskInventoryScriptUpdater(itemID, objectID, is_script_running, experience_key,
                    uploaderPath, m_HostCapsObj.HttpListener, httpRequest.RemoteIPEndPoint.Address, m_dumpAssetsToFile, compileTarget);
            uploader.OnSave += TaskScriptUpdated;

            var uploaderHandler = new SimpleBinaryHandler("POST", uploaderPath, uploader.process);

            uploaderHandler.MaxDataSize = 10000000; // change per asset type?

            m_HostCapsObj.HttpListener.AddSimpleStreamHandler(uploaderHandler);

            // m_log.LogInformation("[CAPS]: " +
            //    "ScriptTaskInventory response: {0}",
            //       LLSDHelpers.SerialiseLLSDReply(uploadResponse)));

            httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(uploadResponse));
        }
        catch (Exception e)
        {
            m_log.LogError("[UpdateScriptTaskInventory]: " + e.ToString());
        }
    }
    /// <summary>
    /// Called when new asset data for an agent inventory item update has been uploaded.
    /// </summary>
    /// <param name="itemID">Item to update</param>
    /// <param name="primID">Prim containing item to update</param>
    /// <param name="isScriptRunning">Signals whether the script to update is currently running</param>
    /// <param name="data">New asset data</param>
    public void TaskScriptUpdated(UUID itemID, UUID primID, bool isScriptRunning, UUID experience, byte[] data, string compileTarget, ref ArrayList errors)
    {
        if (TaskScriptUpdatedCall != null)
        {
            ArrayList e = TaskScriptUpdatedCall(m_HostCapsObj.AgentID, itemID, primID, isScriptRunning, experience, data, compileTarget);
            foreach (Object item in e)
                errors.Add(item);
        }
    }

    /// <summary>
    /// The task script save, giving the id of the asset the save stored in <paramref name="newAssetID"/> (UUID.Zero
    /// when it stored nothing). It uses <see cref="TaskScriptSavedCall"/>, unless a module replaced
    /// <see cref="TaskScriptUpdatedCall"/> and left <see cref="TaskScriptSavedCall"/> as it was: then the replaced
    /// callback does the save, as it did before <see cref="TaskScriptSavedCall"/> existed, and the item's asset is read
    /// before and after it; an asset id that did not change means nothing was stored.
    /// </summary>
    public void TaskScriptUpdated(UUID itemID, UUID primID, bool isScriptRunning, UUID experience, byte[] data, string compileTarget,
                                  ref ArrayList errors, out UUID newAssetID)
    {
        newAssetID = UUID.Zero;
        ArrayList e = null;
        bool updatedReplaced = TaskScriptUpdatedCall != m_defaultTaskScriptUpdatedCall;
        bool savedReplaced = TaskScriptSavedCall != m_defaultTaskScriptSavedCall;
        if (TaskScriptSavedCall != null && (savedReplaced || !updatedReplaced))
        {
            e = TaskScriptSavedCall(m_HostCapsObj.AgentID, itemID, primID, isScriptRunning, experience, data, compileTarget, out newAssetID);
        }
        else if (TaskScriptUpdatedCall != null)
        {
            UUID before = ItemAsset(itemID, primID);
            e = TaskScriptUpdatedCall(m_HostCapsObj.AgentID, itemID, primID, isScriptRunning, experience, data, compileTarget);
            UUID after = ItemAsset(itemID, primID);
            if (after != before)
                newAssetID = after;
        }
        if (e != null)
        {
            foreach (Object item in e)
                errors.Add(item);
        }
    }

    private UUID ItemAsset(UUID itemID, UUID primID)
        => m_Scene.GetSceneObjectPart(primID)?.Inventory.GetInventoryItem(itemID)?.AssetID ?? UUID.Zero;

    static public bool ValidateAssetData(byte assetType, byte[] data)
    {
        return true;
    }

    /// <summary>
    /// This class is a callback invoked when a client sends asset data to
    /// an agent inventory notecard update url
    /// </summary>
    public class ItemUpdater : ExpiringCapBase
    {
        public event UpdateItem OnUpLoad = null;
        private UUID m_inventoryItemID;
        private UUID m_objectID;
        private bool m_dumpAssetToFile;
        public IPAddress m_remoteAdress;
        private byte m_assetType;

        public ItemUpdater(UUID inventoryItem, UUID objectid, byte aType, string path, IHttpServer httpServer, bool dumpAssetToFile):
            base(httpServer, path)
        {
            m_dumpAssetToFile = dumpAssetToFile;

            m_inventoryItemID = inventoryItem;
            m_objectID = objectid;
            m_httpListener = httpServer;
            m_assetType = aType;

            Start(30000);
        }

        /// <summary>
        /// Handle raw uploaded asset data.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <returns></returns>
        public void process(IOSHttpRequest request, IOSHttpResponse response, byte[] data)
        {
            Stop();

            if (!request.RemoteIPEndPoint.Address.Equals(m_remoteAdress))
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            string res = String.Empty;

            if (OnUpLoad == null)
            {
                response.StatusCode = (int)HttpStatusCode.Gone;
                return;
            }

            if (!BunchOfCaps.ValidateAssetData(m_assetType, data))
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            UUID assetID = OnUpLoad(m_inventoryItemID, m_objectID, data);

            if (assetID.IsZero())
            {
                LLSDAssetUploadError uperror = new LLSDAssetUploadError();
                uperror.message = "Failed to update inventory item asset";
                uperror.identifier = m_inventoryItemID;
                res = LLSDHelpers.SerialiseLLSDReply(uperror);
            }
            else
            {
                LLSDAssetUploadComplete uploadComplete = new LLSDAssetUploadComplete();
                uploadComplete.new_asset = assetID.ToString();
                uploadComplete.new_inventory_item = m_inventoryItemID;
                uploadComplete.state = "complete";
                res = LLSDHelpers.SerialiseLLSDReply(uploadComplete);
            }

            if (m_dumpAssetToFile)
            {
                Util.SaveAssetToFile("updateditem" + Random.Shared.Next(1, 1000) + ".dat", data);
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.RawBuffer = Util.UTF8NBGetbytes(res);
        }
    }

    /// <summary>
    /// This class is a callback invoked when a client sends asset data to
    /// a task inventory script update url
    /// </summary>
    public class TaskInventoryScriptUpdater : ExpiringCapBase
    {
        public event UpdateTaskScript OnUpLoad;

        /// <summary>
        /// Used instead of <see cref="OnUpLoad"/> when set: its reply names the stored asset, or is an error when the
        /// save stored nothing.
        /// </summary>
        public event SaveTaskScript OnSave;
        private UUID m_inventoryItemID;
        private UUID m_primID;
        private bool m_isScriptRunning;
        private UUID m_experienceKey;
        private string m_compileTarget;
        private bool m_dumpAssetToFile;
        public IPAddress m_remoteAddress;

        public TaskInventoryScriptUpdater(UUID inventoryItemID, UUID primID, bool isScriptRunning, UUID experience_id,
                                            string path, IHttpServer httpServer, IPAddress address,
                                            bool dumpAssetToFile, string compileTarget = null) : base(httpServer, path)
        {
            m_compileTarget = compileTarget;
            m_dumpAssetToFile = dumpAssetToFile;
            m_inventoryItemID = inventoryItemID;
            m_primID = primID;
            m_isScriptRunning = isScriptRunning;
            m_experienceKey = experience_id;
            m_remoteAddress = address;
            Start(30000);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="data"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <returns></returns>
        public void process(IOSHttpRequest request, IOSHttpResponse response, byte[] data)
        {
            Stop();

            if (!request.RemoteIPEndPoint.Address.Equals(m_remoteAddress))
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            if (OnUpLoad == null && OnSave == null)
            {
                response.StatusCode = (int)HttpStatusCode.Gone;
                return;
            }

            if (!BunchOfCaps.ValidateAssetData((byte)AssetType.LSLText, data))
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            response.StatusCode = (int)HttpStatusCode.OK;

            // new_asset is the id of the asset the save stored, as in the UpdateScriptAgent reply: the LL viewer
            // reads it as the script's new asset id (LLBufferedAssetUploadInfo::finishUpload, llviewerassetupload.cpp).
            // The save sets it as soon as the asset is stored, so it is known even if starting the script throws.
            UUID newAsset = UUID.Zero;
            SaveTaskScript save = OnSave;
            try
            {
                string res = String.Empty;
                LLSDTaskScriptUploadComplete uploadComplete = new LLSDTaskScriptUploadComplete();

                ArrayList errors = new ArrayList();
                if (save != null)
                {
                    save(m_inventoryItemID, m_primID, m_isScriptRunning, m_experienceKey, data, m_compileTarget, ref errors, out newAsset);
                    if (newAsset.IsZero())
                    {
                        // Nothing was stored. The viewer treats a reply whose state is not "complete" as a failed
                        // upload and shows its message (LLViewerAssetUpload::AssetInventoryUploadCoproc).
                        LLSDAssetUploadError failed = new LLSDAssetUploadError();
                        failed.message = "could not update script";
                        failed.identifier = m_inventoryItemID;
                        response.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(failed));
                        return;
                    }
                }
                else
                {
                    OnUpLoad?.Invoke(m_inventoryItemID, m_primID, m_isScriptRunning, m_experienceKey, data, m_compileTarget, ref errors);
                    newAsset = m_inventoryItemID;
                }

                uploadComplete.new_asset = newAsset;
                uploadComplete.compiled = errors.Count > 0 ? false : true;
                uploadComplete.state = "complete";
                uploadComplete.errors = new OpenSim.Framework.Capabilities.OSDArray();
                uploadComplete.errors.Array = errors;

                res = LLSDHelpers.SerialiseLLSDReply(uploadComplete);

                if (m_dumpAssetToFile)
                {
                    Util.SaveAssetToFile("updatedtaskscript" + Random.Shared.Next(1, 1000) + ".dat", data);
                }

                // m_log.LogInformation("[CAPS]: TaskInventoryScriptUpdater.uploaderCaps res: {0}", res);
                response.RawBuffer = Util.UTF8NBGetbytes(res);
            }
            catch
            {
                if (save != null && !newAsset.IsZero())
                {
                    // The script was stored and the item points at it; only what came after failed. Say so, so the
                    // viewer's editor takes the stored asset and shows the error.
                    LLSDTaskScriptUploadComplete stored = new LLSDTaskScriptUploadComplete();
                    stored.new_asset = newAsset;
                    stored.compiled = false;
                    stored.state = "complete";
                    stored.errors = new OpenSim.Framework.Capabilities.OSDArray();
                    stored.errors.Array = new ArrayList { "could not compile script" };
                    response.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(stored));
                    return;
                }
                LLSDAssetUploadError error = new LLSDAssetUploadError();
                error.message = "could not compile script";
                error.identifier = UUID.Zero;
                response.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                return;
            }
        }
    }
}