using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InWorldz.Phlox.Util;
using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Defines a list of supported events with their signatures
    /// </summary>
    public class SupportedEventList
    {
        public enum Events
        {
            AT_ROT_TARGET = 1,
            AT_TARGET,
            ATTACH,
            CHANGED,
            COLLISION,
            COLLISION_END,
            COLLISION_START,
            CONTROL,
            DATASERVER,
            EMAIL,
            HTTP_RESPONSE,
            HTTP_REQUEST,
            LAND_COLLISION,
            LAND_COLLISION_END,
            LAND_COLLISION_START,
            LINK_MESSAGE,
            LISTEN,
            MONEY,
            MOVING_END,
            MOVING_START,
            NO_SENSOR,
            NOT_AT_ROT_TARGET,
            NOT_AT_TARGET,
            OBJECT_REZ,
            ON_REZ,
            REMOTE_DATA,
            RUN_TIME_PERMISSIONS,
            SENSOR,
            STATE_ENTRY,
            STATE_EXIT,
            TIMER,
            TOUCH,
            TOUCH_START,
            TOUCH_END,
            BOT_UPDATE,
            TRANSACTION_RESULT,
            LINKSET_DATA,
            EXPERIENCE_PERMISSIONS,
            EXPERIENCE_PERMISSIONS_DENIED
        }

        private Dictionary<string, FunctionSig> _supportedEvents = new Dictionary<string, FunctionSig>()
        {
            {"at_rot_target", new FunctionSig { 
                FunctionName = "at_rot_target",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Rotation, VarType.Rotation},
                TableIndex = (int) Events.AT_ROT_TARGET
            }},

            {"at_target", new FunctionSig { 
                FunctionName = "at_target",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Vector, VarType.Vector},
                TableIndex = (int) Events.AT_TARGET
            }},

            {"attach", new FunctionSig { 
                FunctionName = "attach",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key},
                TableIndex = (int) Events.ATTACH
            }},

            {"changed", new FunctionSig { 
                FunctionName = "changed",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer},
                TableIndex = (int) Events.CHANGED
            }},

            {"collision", new FunctionSig { 
                FunctionName = "collision",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer},
                TableIndex = (int) Events.COLLISION
            }},

            {"collision_end", new FunctionSig { 
                FunctionName = "collision_end",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer},
                TableIndex = (int) Events.COLLISION_END
            }},

            {"collision_start", new FunctionSig { 
                FunctionName = "collision_start",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer},
                TableIndex = (int) Events.COLLISION_START
            }},

            {"control", new FunctionSig { 
                FunctionName = "control",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key, VarType.Integer, VarType.Integer},
                TableIndex = (int) Events.CONTROL
            }},

            {"dataserver", new FunctionSig { 
                FunctionName = "dataserver",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key, VarType.String},
                TableIndex = (int) Events.DATASERVER
            }},

            {"email", new FunctionSig { 
                FunctionName = "email",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.String, VarType.String,
                    VarType.String, VarType.String, VarType.Integer},
                TableIndex = (int) Events.EMAIL
            }},

            {"http_response", new FunctionSig { 
                FunctionName = "http_response",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key, VarType.Integer, VarType.List, VarType.String},
                TableIndex = (int) Events.HTTP_RESPONSE
            }},

            {"http_request", new FunctionSig { 
                FunctionName = "http_request",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key, VarType.String, VarType.String},
                TableIndex = (int) Events.HTTP_REQUEST
            }},

            {"land_collision", new FunctionSig { 
                FunctionName = "land_collision",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Vector},
                TableIndex = (int) Events.LAND_COLLISION
            }},

            {"land_collision_end", new FunctionSig { 
                FunctionName = "land_collision_end",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Vector},
                TableIndex = (int) Events.LAND_COLLISION_END
            }},

            {"land_collision_start", new FunctionSig { 
                FunctionName = "land_collision_start",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Vector},
                TableIndex = (int) Events.LAND_COLLISION_START
            }},

            {"link_message", new FunctionSig { 
                FunctionName = "link_message",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.String, VarType.Key},
                TableIndex = (int) Events.LINK_MESSAGE
            }},

            {"listen", new FunctionSig { 
                FunctionName = "listen",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String, VarType.Key, VarType.String},
                TableIndex = (int) Events.LISTEN
            }},

            {"money", new FunctionSig { 
                FunctionName = "money",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
                TableIndex = (int) Events.MONEY
            }},

            {"moving_end", new FunctionSig { 
                FunctionName = "moving_end",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {},
                TableIndex = (int) Events.MOVING_END
            }},

            {"moving_start", new FunctionSig { 
                FunctionName = "moving_start",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {},
                TableIndex = (int) Events.MOVING_START
            }},

            {"no_sensor", new FunctionSig { 
                FunctionName = "no_sensor",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {},
                TableIndex = (int) Events.NO_SENSOR
            }},

            {"not_at_rot_target", new FunctionSig { 
                FunctionName = "not_at_rot_target",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {},
                TableIndex = (int) Events.NOT_AT_ROT_TARGET
            }},

            {"not_at_target", new FunctionSig { 
                FunctionName = "not_at_target",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {},
                TableIndex = (int) Events.NOT_AT_TARGET
            }},

            {"object_rez", new FunctionSig { 
                FunctionName = "object_rez",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key},
                TableIndex = (int) Events.OBJECT_REZ
            }},

            {"on_rez", new FunctionSig { 
                FunctionName = "on_rez",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer},
                TableIndex = (int) Events.ON_REZ
            }},

            {"remote_data", new FunctionSig { 
                FunctionName = "remote_data",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Key, VarType.Key,
                   VarType.String, VarType.Integer, VarType.String},
                TableIndex = (int) Events.REMOTE_DATA
            }},

            {"run_time_permissions", new FunctionSig { 
                FunctionName = "run_time_permissions",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer},
                TableIndex = (int) Events.RUN_TIME_PERMISSIONS
            }},

            {"sensor", new FunctionSig { 
                FunctionName = "sensor",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer},
                TableIndex = (int) Events.SENSOR
            }},

            {"state_entry", new FunctionSig { 
                FunctionName = "state_entry",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {},
                TableIndex = (int) Events.STATE_ENTRY
            }},

            {"state_exit", new FunctionSig { 
                FunctionName = "state_exit",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {},
                TableIndex = (int) Events.STATE_EXIT
            }},

            {"timer", new FunctionSig { 
                FunctionName = "timer",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {},
                TableIndex = (int) Events.TIMER
            }},

            {"touch", new FunctionSig { 
                FunctionName = "touch",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer},
                TableIndex = (int) Events.TOUCH
            }},

            {"touch_start", new FunctionSig { 
                FunctionName = "touch_start",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer},
                TableIndex = (int) Events.TOUCH_START
            }},

            {"touch_end", new FunctionSig { 
                FunctionName = "touch_end",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer},
                TableIndex = (int) Events.TOUCH_END
            }},

            {"bot_update", new FunctionSig { 
                FunctionName = "bot_update",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.String,VarType.Integer,VarType.List},
                TableIndex = (int) Events.BOT_UPDATE
            }},

            {"transaction_result", new FunctionSig { 
                FunctionName = "transaction_result",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key, VarType.Integer, VarType.String},
                TableIndex = (int) Events.TRANSACTION_RESULT
            }},

            {"linkset_data", new FunctionSig {
                FunctionName = "linkset_data",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String, VarType.String},
                TableIndex = (int) Events.LINKSET_DATA
            }},
			{"experience_permissions", new FunctionSig {
                FunctionName = "experience_permissions",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key},
                TableIndex = (int) Events.EXPERIENCE_PERMISSIONS
            }},

            {"experience_permissions_denied", new FunctionSig {
                FunctionName = "experience_permissions_denied",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
                TableIndex = (int) Events.EXPERIENCE_PERMISSIONS_DENIED
            }},
        };

        public SupportedEventList()
        {
        }

        public FunctionSig GetEventByName(string name)
        {
            return _supportedEvents[name];
        }

        /// <summary>
        /// Does this list contain the given event name
        /// </summary>
        /// <param name="name">The event name to search for</param>
        /// <returns>True if the event is found, false if not</returns>
        public bool HasEventByName(string name)
        {
            return _supportedEvents.ContainsKey(name);
        }

        /// <summary>
        /// Does this list contain the given event and match the given signature
        /// </summary>
        /// <param name="name">The name of the event</param>
        /// <param name="returnType">The return type of the event</param>
        /// <param name="parms">A list of expected parameter types</param>
        /// <returns></returns>
        public bool HasEventBySig(string name, VarType returnType, IEnumerable<VarType> parms)
        {
            FunctionSig sig;
            if (_supportedEvents.TryGetValue(name, out sig))
            {
                //match return type
                if (sig.ReturnType != returnType)
                {
                    return false;
                }

                //match params
                IEnumerator<VarType> givenParms = parms.GetEnumerator();
                foreach (VarType type in sig.ParamTypes)
                {
                    if (!givenParms.MoveNext())
                    {
                        return false;
                    }

                    if (givenParms.Current != type)
                    {
                        return false;
                    }
                }

                if (givenParms.MoveNext())
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        public IEnumerable<VarType> GetArguments(string eventName)
        {
            FunctionSig sig = _supportedEvents[eventName];
            return sig.ParamTypes;
        }
    }
}
