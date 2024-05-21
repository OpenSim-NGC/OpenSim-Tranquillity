/*
 * GloebitSubscription.cs is part of OpenSim-MoneyModule-Gloebit
 * Copyright (C) 2015 Gloebit LLC
 *
 * OpenSim-MoneyModule-Gloebit is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * OpenSim-MoneyModule-Gloebit is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with OpenSim-MoneyModule-Gloebit.  If not, see <https://www.gnu.org/licenses/>.
 */

/*
 * GloebitSubscription.cs
 * 
 * Object representation of a Subscription for use with the GloebitAPI
 * See GloebitSubscriptionData.cs for DB implementation
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenSim.Server.Base;

using OpenMetaverse;

namespace Gloebit.GloebitMoneyModule {

    // Compiler complains about not declaring an override for GetHashCode, but we don't use that so we'll just tell it to stop complaining		
    #pragma warning disable 0659

    public class GloebitSubscription {
        private static ILogger m_logger;

        // These 3 make up the primary key -- allows sim to swap back and forth between apps or GlbEnvs without getting errors
        // public UUID ObjectID;       // ID of object with an LLGiveMoney or LLTransferLinden's script - local subscription ID
        public string ObjectID;       // ID of object with an LLGiveMoney or LLTransferLinden's script - local subscription ID
        public string AppKey;       // AppKey active when created
        public string GlbApiUrl;    // GlbEnv Url active when created

        //public UUID SubscriptionID; // ID returned by create-subscription Gloebit endpoint
        public string SubscriptionID; // ID returned by create-subscription Gloebit endpoint
        public bool Enabled;        // enabled returned by Gloebit Endpoint - if not enabled, can't use.
        public DateTime cTime;      // time of creation

        // TODO: Are these necessary beyond sending to Gloebit? - can be rebuilt from object
        // TODO: a name or description change doesn't necessarily change the the UUID of the object --- how to deal with this?
        // TODO: name and description could be empty/blank --
        public string ObjectName;   // Name of object - treated as subscription_name by Gloebit
        public string Description;  // subscription_description - (should include object description, but may include additional details)
        // TODO: additional details --- how to store --- do we need to store?

        private static Dictionary<string, GloebitSubscription> s_subscriptionMap = new Dictionary<string, GloebitSubscription>();

        public GloebitSubscription() {
            m_logger ??= OpenSimServer.Instance.ServiceProvider.GetRequiredService<ILogger<GloebitSubscription>>();
        }

        private GloebitSubscription(string objectID, string appKey, string apiURL,
                            string objectName, string objectDescription): this()  {
            this.ObjectID = objectID;
            this.AppKey = appKey;
            this.GlbApiUrl = apiURL;

            this.ObjectName = objectName;
            this.Description = objectDescription;

            // Set defaults until we fill them in
            SubscriptionID = UUID.Zero.ToString();
            this.cTime = DateTime.UtcNow;
            this.Enabled = false;

            m_logger.LogInformation("[GLOEBITMONEYMODULE] in GloebitSubscription() oID:{0}, oN:{1}, oD:{2}", ObjectID, ObjectName, Description);

        }

        public static GloebitSubscription Init(string objectID, string appKey, string apiUrl, string objectName, string objectDescription) 
        {
            GloebitSubscription s = new GloebitSubscription(objectID, appKey, apiUrl, objectName, objectDescription);

            lock(s_subscriptionMap) 
            {
                s_subscriptionMap[objectID] = s;
            }

            GloebitSubscriptionData.Instance.Store(s);
            return s;
        }

        public static GloebitSubscription[] Get(UUID objectID) {
            return Get(objectID.ToString());
        }

        public static GloebitSubscription[] Get(string objectIDStr) {
            m_logger.LogInformation("[GLOEBITMONEYMODULE] in Subscription.Get");
            GloebitSubscription subscription = null;
            lock(s_subscriptionMap) {
                s_subscriptionMap.TryGetValue(objectIDStr, out subscription);
            }

            /*if(subscription == null) {*/
            m_logger.LogDebug("[GLOEBITMONEYMODULE] Looking for subscriptions for {0}", objectIDStr);
            GloebitSubscription[] subscriptions = GloebitSubscriptionData.Instance.Get("ObjectID", objectIDStr);
            /*
                    Subscription[] subsForAppWithKey = new Subscription[];
                    foreach (Subscription sub in subscriptions) {
                        if (sub.AppKey = "appkey" && sub.GlbApiUrl = "url") {
                            subsForAppWithKey.Append(sub);
                        }
                    }
                     */
            bool cacheDuplicate = false;
            m_logger.LogDebug("[GLOEBITMONEYMODULE] Found {0} subscriptions for {0} saved in the DB", subscriptions.Length, objectIDStr);
            if (subscription != null) {
                m_logger.LogDebug("[GLOEBITMONEYMODULE] Found 1 cached subscriptions for {0}", subscriptions.Length, objectIDStr);
                if (subscriptions.Length == 0) {
                    subscriptions = new GloebitSubscription[1];
                    subscriptions[0] = subscription;
                } else {
                    for (int i = 0; i < subscriptions.Length; i++) {
                        if (subscriptions[i].ObjectID == subscription.ObjectID &&
                            subscriptions[i].AppKey == subscription.AppKey &&
                            subscriptions[i].GlbApiUrl == subscription.GlbApiUrl)
                        {
                            cacheDuplicate = true;
                            subscriptions[i] = subscription;
                            m_logger.LogDebug("[GLOEBITMONEYMODULE] Cached subscription was in db.  Replacing with cached version.");
                            break;
                        }
                    }
                    if (!cacheDuplicate) {
                        m_logger.LogDebug("[GLOEBITMONEYMODULE] Combining Cached subscription with those from db.");
                        GloebitSubscription[] dbSubs = subscriptions;
                        subscriptions = new GloebitSubscription[dbSubs.Length + 1];
                        subscriptions[0] = subscription;
                        for (int i = 1; i < subscriptions.Length; i++) {
                            subscriptions[i] = dbSubs[i-1];
                        }
                    }

                }

            } else {
                m_logger.LogDebug("[GLOEBITMONEYMODULE] Found no cached subscriptions for {0}", subscriptions.Length, objectIDStr);
            }

            m_logger.LogDebug("[GLOEBITMONEYMODULE] Returning {0} subscriptions for {0}", subscriptions.Length, objectIDStr);
            return subscriptions;
            /*
                    switch(subscriptions.Length) {
                        case 1:
                            subscription = subsForAppWithKey[0];
                            m_logger.LogInformation("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION! {0} {1} {2} {3}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID);
                            lock(s_subscriptionMap) {
                                s_subscriptionMap[objectIDStr] = subscription;
                            }
                            return subscription;
                        case 0:
                            m_logger.LogInformation("[GLOEBITMONEYMODULE] Could not find subscription matching oID:{0}", objectIDStr);
                            return null;
                        default:
                            throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one subscription for {0} {1} {2}", objectIDStr));
                            return null;
                    }
                }
                
                return subscription;
                  */
        }
        public static GloebitSubscription Get(UUID objectID, string appKey, Uri apiUrl) {
            return Get(objectID.ToString(), appKey, apiUrl.ToString());
        }

        public static GloebitSubscription Get(string objectIDStr, string appKey, Uri apiUrl) {
            return Get(objectIDStr, appKey, apiUrl.ToString());
        }

        public static GloebitSubscription Get(UUID objectID, string appKey, string apiUrl) {
            return Get(objectID.ToString(), appKey, apiUrl);
        }

        public static GloebitSubscription Get(string objectIDStr, string appKey, string apiUrl) {
            m_logger.LogInformation("[GLOEBITMONEYMODULE] in GloebitSubscription.Get");
            GloebitSubscription subscription = null;
            lock(s_subscriptionMap) {
                s_subscriptionMap.TryGetValue(objectIDStr, out subscription);
            }

            if(subscription == null) {
                m_logger.LogDebug("[GLOEBITMONEYMODULE] Looking for prior subscription for {0} {1} {2}", objectIDStr, appKey, apiUrl);
                string[] keys = new string[] {"ObjectID", "AppKey", "GlbApiUrl"};
                string[] values = new string[] {objectIDStr, appKey, apiUrl};
                GloebitSubscription[] subscriptions = GloebitSubscriptionData.Instance.Get(keys, values);

                switch(subscriptions.Length) {
                case 1:
                    subscription = subscriptions[0];
                    m_logger.LogDebug("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in DB! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
                    lock(s_subscriptionMap) {
                        s_subscriptionMap[objectIDStr] = subscription;
                    }
                    return subscription;
                case 0:
                    m_logger.LogDebug("[GLOEBITMONEYMODULE] Could not find subscription matching oID:{0} appKey:{1} apiUrl:{2}", objectIDStr, appKey, apiUrl);
                    return null;
                default:
                    throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one subscription for {0} {1} {2}", objectIDStr, appKey, apiUrl));
                }
            }
            m_logger.LogDebug("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in cache! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
            return subscription;
        }

        public static GloebitSubscription GetBySubscriptionID(string subscriptionIDStr, string apiUrl) {
            m_logger.LogInformation("[GLOEBITMONEYMODULE] in GloebitSubscription.GetBySubscriptionID");
            GloebitSubscription subscription = null;
            GloebitSubscription localSub = null;


            m_logger.LogDebug("[GLOEBITMONEYMODULE] Looking for prior subscription for {0} {1}", subscriptionIDStr, apiUrl);
            string[] keys = new string[] {"SubscriptionID", "GlbApiUrl"};
            string[] values = new string[] {subscriptionIDStr, apiUrl};
            GloebitSubscription[] subscriptions = GloebitSubscriptionData.Instance.Get(keys, values);


            switch(subscriptions.Length) {
            case 1:
                subscription = subscriptions[0];
                m_logger.LogDebug("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in DB! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
                lock(s_subscriptionMap) {
                    s_subscriptionMap.TryGetValue(subscription.ObjectID.ToString(), out localSub);
                    if (localSub == null) {
                        s_subscriptionMap[subscription.ObjectID.ToString()] = subscription;
                    }
                }
                if (localSub == null) {
                    // do nothing.  already added subscription to cache in lock
                } else if (localSub.Equals(subscription)) {
                    // return cached sub instead of new sub from DB
                    subscription = localSub;
                } else {
                    m_logger.LogError("[GLOEBITMONEYMODULE] mapped Subscription is not equal to DB return --- shouldn't happen.  Investigate.");
                    m_logger.LogError("Local Sub\n sID:{0}\n oID:{1}\n appKey:{2}\n apiUrl:{3}\n oN:{4}\n oD:{5}\n enabled:{6}\n ctime:{7}", localSub.SubscriptionID, localSub.ObjectID, localSub.AppKey, localSub.GlbApiUrl, localSub.ObjectName, localSub.Description, localSub.Enabled, localSub.cTime);
                    m_logger.LogError("DB Sub\n sID:{0}\n oID:{1}\n appKey:{2}\n apiUrl:{3}\n oN:{4}\n oD:{5}\n enabled:{6}\n ctime:{7}", subscription.SubscriptionID, subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.ObjectName, subscription.Description, subscription.Enabled, subscription.cTime);
                    // still return cached sub instead of new sub from DB
                    subscription = localSub;
                }
                return subscription;
            case 0:
                m_logger.LogDebug("[GLOEBITMONEYMODULE] Could not find subscription matching sID:{0} apiUrl:{1}", subscriptionIDStr, apiUrl);
                return null;
            default:
                throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one subscription for {0} {1}", subscriptionIDStr, apiUrl));
            }
            //m_logger.LogDebug("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in cache! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
            //return subscription;
        }

        public override bool Equals(Object obj)
        {
            //Check for null and compare run-time types.
            if ((obj == null) || ! this.GetType().Equals(obj.GetType())) {
                return false;
            }
            else {
                GloebitSubscription s = (GloebitSubscription) obj;
                // TODO: remove these info logs once we understand why things are not always equal
                // m_logger.LogInformation("[GLOEBITMONEYMODULE] Subscription.Equals()");
                // m_logger.LogInformation("ObjectID:{0}", (ObjectID == s.ObjectID));
                // m_logger.LogInformation("AppKey:{0}", (AppKey == s.AppKey));
                // m_logger.LogInformation("GlbApiUrl:{0}", (GlbApiUrl == s.GlbApiUrl));
                // m_logger.LogInformation("ObjectName:{0}", (ObjectName == s.ObjectName));
                // m_logger.LogInformation("Description:{0}", (Description == s.Description));
                // m_logger.LogInformation("SubscriptionID:{0}", (SubscriptionID == s.SubscriptionID));
                // m_logger.LogInformation("Enabled:{0}", (Enabled == s.Enabled));
                // m_logger.LogInformation("ctime:{0}", (ctime == s.ctime));
                // m_logger.LogInformation("ctime Equals:{0}", (ctime.Equals(s.ctime)));
                // m_logger.LogInformation("ctime CompareTo:{0}", (ctime.CompareTo(s.ctime)));
                // m_logger.LogInformation("ctime ticks:{0} == {1}", ctime.Ticks, s.ctime.Ticks);

                // NOTE: intentionally does not compare ctime as db truncates miliseconds to zero.
                return ((ObjectID == s.ObjectID) &&
                    (AppKey == s.AppKey) &&
                    (GlbApiUrl == s.GlbApiUrl) &&
                    (ObjectName == s.ObjectName) &&
                    (Description == s.Description) &&
                    (SubscriptionID == s.SubscriptionID) &&
                    (Enabled == s.Enabled));
            }
        }
    }
    #pragma warning restore 0659
}

