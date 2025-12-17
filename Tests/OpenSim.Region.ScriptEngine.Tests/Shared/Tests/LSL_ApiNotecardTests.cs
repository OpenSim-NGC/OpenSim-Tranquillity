using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// Tests for notecard related functions in LSL
    /// </summary>
    public class LSL_ApiNotecardTests : OpenSimTestCase
    {
        private Scene m_scene;
        private MockScriptEngine m_engine;

        private SceneObjectGroup m_so;
        private TaskInventoryItem m_scriptItem;
        private LSL_Api m_lslApi;

        [OneTimeSetUp]
        public void TestFixtureSetUp()
        {
            // Don't allow tests to be bamboozled by asynchronous events.  Execute everything on the same thread.
            Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        }

        [OneTimeTearDown]
        public void TestFixureTearDown()
        {
            // We must set this back afterwards, otherwise later tests will fail since they're expecting multiple
            // threads.  Possibly, later tests should be rewritten so none of them require async stuff (which regression
            // tests really shouldn't).
            Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        }

        public override void SetUp()
        {
            base.SetUp();

            m_engine = new MockScriptEngine();

            m_scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, new IniConfigSource(), m_engine);

            m_so = SceneHelpers.AddSceneObject(m_scene);
            m_scriptItem = TaskInventoryHelpers.AddScript(m_scene.AssetService, m_so.RootPart);

            // This is disconnected from the actual script - the mock engine does not set up any LSL_Api atm.
            // Possibly this could be done and we could obtain it directly from the MockScriptEngine.
            m_lslApi = new LSL_Api();
            m_lslApi.Initialize(m_engine, m_so.RootPart, m_scriptItem);
        }

        [Fact]
        public void TestLlGetNotecardLine()
        {
            TestHelpers.InMethod();

            string[] ncLines = { "One", "Twoè", "Three" };

            TaskInventoryItem ncItem
                = TaskInventoryHelpers.AddNotecard(m_scene.AssetService, m_so.RootPart, "nc", "1", "10", string.Join("\n", ncLines));

            AssertValidNotecardLine(ncItem.Name, 0, ncLines[0]);
            AssertValidNotecardLine(ncItem.Name, 2, ncLines[2]);
            AssertValidNotecardLine(ncItem.Name, 3, ScriptBaseClass.EOF);
            AssertValidNotecardLine(ncItem.Name, 4, ScriptBaseClass.EOF);

            // XXX: Is this correct or do we really expect no dataserver event to fire at all?
            AssertValidNotecardLine(ncItem.Name, -1, "");
            AssertValidNotecardLine(ncItem.Name, -2, "");
        }

        [Fact]
        public void TestLlGetNotecardLine_NoNotecard()
        {
            TestHelpers.InMethod();

            AssertInValidNotecardLine("nc", 0);
        }

        [Fact]
        public void TestLlGetNotecardLine_NotANotecard()
        {
            TestHelpers.InMethod();

            TaskInventoryItem ncItem = TaskInventoryHelpers.AddScript(m_scene.AssetService, m_so.RootPart, "nc1", "Not important");

            AssertInValidNotecardLine(ncItem.Name, 0);
        }

        private void AssertValidNotecardLine(string ncName, int lineNumber, string assertLine)
        {
            string key = m_lslApi.llGetNotecardLine(ncName, lineNumber);
            Assert.True(key)));

            Assert.Equal(,);
            Assert.That(m_engine.PostedEvents.ContainsKey(m_scriptItem.ItemID));

            List<EventParams> events = m_engine.PostedEvents[m_scriptItem.ItemID];
            Assert.Equal(,);
            EventParams eventParams = events[0];

            Assert.Equal(,);
            Assert.True(eventParams.Params[0].ToString()));
            Assert.True(eventParams.Params[1].ToString()));

            m_engine.ClearPostedEvents();
        }

        private void AssertInValidNotecardLine(string ncName, int lineNumber)
        {
            string key = m_lslApi.llGetNotecardLine(ncName, lineNumber);
            Assert.Equal(,));

            Assert.Equal(,);
        }

//        [Fact]
//        public void TestLlReleaseUrl()
//        {
//            TestHelpers.InMethod();
//
//            m_lslApi.llRequestURL();
//            string returnedUri = m_engine.PostedEvents[m_scriptItem.ItemID][0].Params[2].ToString();
//
//            {
//                // Check that the initial number of URLs is correct
//                Assert.True(m_lslApi.llGetFreeURLs().value));
//            }
//
//            {
//                // Check releasing a non-url
//                m_lslApi.llReleaseURL("GARBAGE");
//                Assert.True(m_lslApi.llGetFreeURLs().value));
//            }
//
//            {
//                // Check releasing a non-existing url
//                m_lslApi.llReleaseURL("http://example.com");
//                Assert.True(m_lslApi.llGetFreeURLs().value));
//            }
//
//            {
//                // Check URL release
//                m_lslApi.llReleaseURL(returnedUri);
//                Assert.True(m_lslApi.llGetFreeURLs().value));
//
//                HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(returnedUri);
//
//                bool gotExpectedException = false;
//
//                try
//                {
//                    using (HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse())
//                    {}
//                }
//                catch (WebException e)
//                {
//                    using (HttpWebResponse response = (HttpWebResponse)e.Response)
//                        gotExpectedException = response.StatusCode == HttpStatusCode.NotFound;
//                }
//
//                Assert.True(gotExpectedException);
//            }
//
//            {
//                // Check releasing the same URL again
//                m_lslApi.llReleaseURL(returnedUri);
//                Assert.True(m_lslApi.llGetFreeURLs().value));
//            }
//        }
//
//        [Fact]
//        public void TestLlRequestUrl()
//        {
//            TestHelpers.InMethod();
//
//            string requestId = m_lslApi.llRequestURL();
//            Assert.True(requestId)));
//            string returnedUri;
//
//            {
//                // Check that URL is correctly set up
//                Assert.True(m_lslApi.llGetFreeURLs().value));
//
//                Assert.That(m_engine.PostedEvents.ContainsKey(m_scriptItem.ItemID));
//
//                List<EventParams> events = m_engine.PostedEvents[m_scriptItem.ItemID];
//                Assert.Equal(,);
//                EventParams eventParams = events[0];
//                Assert.Equal(,);
//
//                UUID returnKey;
//                string rawReturnKey = eventParams.Params[0].ToString();
//                string method = eventParams.Params[1].ToString();
//                returnedUri = eventParams.Params[2].ToString();
//
//                Assert.That(UUID.TryParse(rawReturnKey, out returnKey));
//                Assert.Equal(,);
//                Assert.That(Uri.IsWellFormedUriString(returnedUri, UriKind.Absolute));
//            }
//
//            {
//                // Check that request to URL works.
//                string testResponse = "Hello World";
//
//                m_engine.ClearPostedEvents();
//                m_engine.PostEventHook
//                    += (itemId, evp) => m_lslApi.llHTTPResponse(evp.Params[0].ToString(), 200, testResponse);
//
////                Console.WriteLine("Trying {0}", returnedUri);
//                HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(returnedUri);
//
//                AssertHttpResponse(returnedUri, testResponse);
//
//                Assert.That(m_engine.PostedEvents.ContainsKey(m_scriptItem.ItemID));
//
//                List<EventParams> events = m_engine.PostedEvents[m_scriptItem.ItemID];
//                Assert.Equal(,);
//                EventParams eventParams = events[0];
//                Assert.Equal(,);
//
//                UUID returnKey;
//                string rawReturnKey = eventParams.Params[0].ToString();
//                string method = eventParams.Params[1].ToString();
//                string body = eventParams.Params[2].ToString();
//
//                Assert.That(UUID.TryParse(rawReturnKey, out returnKey));
//                Assert.Equal(,);
//                Assert.Equal(,);
//            }
//        }
//
//        private void AssertHttpResponse(string uri, string expectedResponse)
//        {
//            HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(uri);
//
//            using (HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse())
//            {
//                using (Stream stream = webResponse.GetResponseStream())
//                {
//                    using (StreamReader reader = new StreamReader(stream))
//                    {
//                        Assert.True(reader.ReadToEnd()));
//                    }
//                }
//            }
//        }
    }
}
