using System;
using System.Collections.Generic;
using Moq;
using Xunit;
using Microsoft.Extensions.Logging;
using OpenSim.Services.Connectors;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace OpenSim.Tests.Services.Connectors
{
    public class AgentPreferencesServicesConnectorTests
    {
        private readonly Mock<ILogger<AgentPreferencesServicesConnector>> _mockLogger;
        private readonly Mock<IServiceAuth> _mockAuth;
        private readonly Mock<IConfiguration> _mockConfig;
        private readonly AgentPreferencesServicesConnector _connector;

        public AgentPreferencesServicesConnectorTests()
        {
            _mockLogger = new Mock<ILogger<AgentPreferencesServicesConnector>>();
            _mockAuth = new Mock<IServiceAuth>();
            _mockConfig = new Mock<IConfiguration>();

            // Mock configuration for server URI
            _mockConfig.Setup(c => c[$"AgentPreferencesService:AgentPreferencesServerURI"])
                       .Returns("http://mockserver/agentprefs");

            _connector = new AgentPreferencesServicesConnector(_mockConfig.Object, _mockLogger.Object);
        }

        [Fact]
        public void GetAgentPreferences_ReturnsAgentPrefs_WhenValidResponse()
        {
            // Arrange
            var principalID = UUID.Random();
            var mockResponse = "<result>Success</result><PrincipalID>" + principalID + "</PrincipalID>";
            MockSynchronousRestFormsRequester.SetupRequest("POST", It.IsAny<string>(), It.IsAny<string>(), mockResponse);

            // Act
            var result = _connector.GetAgentPreferences(principalID);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(principalID, result.PrincipalID);
        }

        [Fact]
        public void GetAgentPreferences_ReturnsNull_WhenEmptyResponse()
        {
            // Arrange
            var principalID = UUID.Random();
            MockSynchronousRestFormsRequester.SetupRequest("POST", It.IsAny<string>(), It.IsAny<string>(), string.Empty);

            // Act
            var result = _connector.GetAgentPreferences(principalID);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void StoreAgentPreferences_ReturnsTrue_WhenSuccessResponse()
        {
            // Arrange
            var agentPrefs = new AgentPrefs
            {
                PrincipalID = UUID.Random(),
                AccessPrefs = "M",
                HoverHeight = 1.5f,
                Language = "en-us",
                LanguageIsPublic = true,
                PermEveryone = 0,
                PermGroup = 0,
                PermNextOwner = 0
            };
            MockSynchronousRestFormsRequester.SetupRequest("POST", It.IsAny<string>(), It.IsAny<string>(), "<success>true</success>");

            // Act
            var result = _connector.StoreAgentPreferences(agentPrefs);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void StoreAgentPreferences_ReturnsFalse_WhenFailureResponse()
        {
            // Arrange
            var agentPrefs = new AgentPrefs
            {
                PrincipalID = UUID.Random(),
                AccessPrefs = "M",
                HoverHeight = 1.5f,
                Language = "en-us",
                LanguageIsPublic = true,
                PermEveryone = 0,
                PermGroup = 0,
                PermNextOwner = 0
            };
            MockSynchronousRestFormsRequester.SetupRequest("POST", It.IsAny<string>(), It.IsAny<string>(), string.Empty);

            // Act
            var result = _connector.StoreAgentPreferences(agentPrefs);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void GetLang_ReturnsLanguage_WhenValidResponse()
        {
            // Arrange
            var principalID = UUID.Random();
            var mockResponse = "<result>Success</result><Language>fr-fr</Language>";
            MockSynchronousRestFormsRequester.SetupRequest("POST", It.IsAny<string>(), It.IsAny<string>(), mockResponse);

            // Act
            var result = _connector.GetLang(principalID);

            // Assert
            Assert.Equal("fr-fr", result);
        }

        [Fact]
        public void GetLang_ReturnsDefaultLanguage_WhenEmptyResponse()
        {
            // Arrange
            var principalID = UUID.Random();
            MockSynchronousRestFormsRequester.SetupRequest("POST", It.IsAny<string>(), It.IsAny<string>(), string.Empty);

            // Act
            var result = _connector.GetLang(principalID);

            // Assert
            Assert.Equal("en-us", result);
        }
    }

    // Mock helper for SynchronousRestFormsRequester
    public static class MockSynchronousRestFormsRequester
    {
        public static void SetupRequest(string method, string uri, string requestBody, string response)
        {
            // Mock the static method MakeRequest
            // This requires a library like Fakes or a wrapper around the static method
        }
    }
}
