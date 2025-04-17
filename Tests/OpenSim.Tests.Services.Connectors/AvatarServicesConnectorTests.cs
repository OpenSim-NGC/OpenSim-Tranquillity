using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Services.Connectors;
using OpenSim.Services.Interfaces;

using OpenMetaverse;

namespace OpenSim.Tests.Services.Connectors
{
    public class AvatarServicesConnectorTests
    {
        private readonly Mock<ILogger<AvatarServicesConnector>> _mockLogger;
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IServiceAuth> _mockAuth;
        private readonly AvatarServicesConnector _connector;

        public AvatarServicesConnectorTests()
        {
            _mockLogger = new Mock<ILogger<AvatarServicesConnector>>();
            _mockConfiguration = new Mock<IConfiguration>();
            _mockAuth = new Mock<IServiceAuth>();

            // Mock configuration setup
            _mockConfiguration
                .Setup(c => c[$"ServiceURI:{AvatarServicesConnector._serviceName}:AvatarServerURI"])
                .Returns("http://mockserver/avatar");

            _connector = new AvatarServicesConnector(_mockConfiguration.Object, _mockLogger.Object);
        }

        [Fact]
        public void GetAppearance_ShouldReturnAvatarAppearance()
        {
            // Arrange
            var userId = UUID.Random();
            var mockAvatarData = new AvatarData();
            var mockAppearance = new AvatarAppearance();

            var connectorMock = new Mock<AvatarServicesConnector>(_mockConfiguration.Object, _mockLogger.Object);
            connectorMock.Setup(c => c.GetAvatar(userId)).Returns(mockAvatarData);
            connectorMock.Setup(c => mockAvatarData.ToAvatarAppearance()).Returns(mockAppearance);

            // Act
            var result = connectorMock.Object.GetAppearance(userId);

            // Assert
            Assert.NotNull(result);
            Assert.IsType<AvatarAppearance>(result);
        }

        [Fact]
        public void SetAppearance_ShouldReturnTrue_WhenSuccessful()
        {
            // Arrange
            var userId = UUID.Random();
            var mockAppearance = new AvatarAppearance();
            var mockAvatarData = new AvatarData(mockAppearance);

            var connectorMock = new Mock<AvatarServicesConnector>(_mockConfiguration.Object, _mockLogger.Object);
            connectorMock.Setup(c => c.SetAvatar(userId, mockAvatarData)).Returns(true);

            // Act
            var result = connectorMock.Object.SetAppearance(userId, mockAppearance);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void GetAvatar_ShouldReturnAvatarData()
        {
            // Arrange
            var userId = UUID.Random();
            var mockAvatarData = new AvatarData();

            var connectorMock = new Mock<AvatarServicesConnector>(_mockConfiguration.Object, _mockLogger.Object);
            connectorMock.Setup(c => c.GetAvatar(userId)).Returns(mockAvatarData);

            // Act
            var result = connectorMock.Object.GetAvatar(userId);

            // Assert
            Assert.NotNull(result);
            Assert.IsType<AvatarData>(result);
        }

        [Fact]
        public void SetAvatar_ShouldReturnTrue_WhenSuccessful()
        {
            // Arrange
            var userId = UUID.Random();
            var mockAvatarData = new AvatarData();

            var connectorMock = new Mock<AvatarServicesConnector>(_mockConfiguration.Object, _mockLogger.Object);
            connectorMock.Setup(c => c.SetAvatar(userId, mockAvatarData)).Returns(true);

            // Act
            var result = connectorMock.Object.SetAvatar(userId, mockAvatarData);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void ResetAvatar_ShouldReturnTrue_WhenSuccessful()
        {
            // Arrange
            var userId = UUID.Random();

            var connectorMock = new Mock<AvatarServicesConnector>(_mockConfiguration.Object, _mockLogger.Object);
            connectorMock.Setup(c => c.ResetAvatar(userId)).Returns(true);

            // Act
            var result = connectorMock.Object.ResetAvatar(userId);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void SetItems_ShouldReturnTrue_WhenSuccessful()
        {
            // Arrange
            var userId = UUID.Random();
            var names = new[] { "item1", "item2" };
            var values = new[] { "value1", "value2" };

            var connectorMock = new Mock<AvatarServicesConnector>(_mockConfiguration.Object, _mockLogger.Object);
            connectorMock.Setup(c => c.SetItems(userId, names, values)).Returns(true);

            // Act
            var result = connectorMock.Object.SetItems(userId, names, values);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void RemoveItems_ShouldReturnTrue_WhenSuccessful()
        {
            // Arrange
            var userId = UUID.Random();
            var names = new[] { "item1", "item2" };

            var connectorMock = new Mock<AvatarServicesConnector>(_mockConfiguration.Object, _mockLogger.Object);
            connectorMock.Setup(c => c.RemoveItems(userId, names)).Returns(true);

            // Act
            var result = connectorMock.Object.RemoveItems(userId, names);

            // Assert
            Assert.True(result);
        }
    }
}