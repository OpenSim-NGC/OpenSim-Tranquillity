using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces
{
    public interface IDisplayNameModule
    {
        public string GetDisplayName(UUID avatar);
    }
}
