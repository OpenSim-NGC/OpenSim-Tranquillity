using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenMetaverse;

using OpenSim.Server.RobustServer.Models;

namespace OpenSim.Server.RobustServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AssetServiceController : ControllerBase
{
    // GET: api/assets/{id}
    // Handles HTTP GET requests to a route with an ID parameter
    [HttpGet("{id}")]
    public ActionResult<AssetDataDTO> GetAssetById(UUID id)
    {
        return NotFound(); // Returns a 404 Not Found status if the ID is invalid
    }
    
    // PUT: api/assets/{id}
    // Update an existing asset
    [HttpPut("{id}")]
    public ActionResult<AssetDataDTO> UpdateAssetById(UUID id, AssetDataDTO data)
    {
        if (id != data.AssetId)
            return NotFound();

        return NotFound(); // Returns a 404 Not Found status if the ID is invalid
    }

    // DELETE: api/assets/{id}
    // Delete an assets (requires authentication)
    [HttpDelete("{id}")]
    public ActionResult<AssetDataDTO> DeleteAssetById(int id)
    {
        return NotFound(); // Returns a 404 Not Found status if the ID is invalid
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<bool[]> AssetsExist([FromBody] string[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return NotFound();
    }
}

